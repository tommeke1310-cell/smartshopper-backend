using SmartShopper.API.Models;
using System.Text.Json;
using System.Globalization;
using HtmlAgilityPack;
using ScraperResult = SmartShopper.API.Models.ScraperResult;

namespace SmartShopper.API.Services.Scrapers;

// ─────────────────────────────────────────────────────────────────
//  LIDL  (NL / BE / DE)  — via Lidl Plus consumer API
// ─────────────────────────────────────────────────────────────────
public class LidlScraper
{
    private readonly HttpClient           _http;
    private readonly ILogger<LidlScraper> _logger;

    public LidlScraper(HttpClient http, ILogger<LidlScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string country = "NL")
    {
        var raw = await TryLidlPlusApi(item.Name, country);
        if (!raw.Success) raw = await TryLidlApi(item.Name, country);
        if (!raw.Success) raw = await TryLidlHtml(item.Name, country);
        if (!raw.Success) return [];

        var storeName = country switch { "DE" => "Lidl DE", "BE" => "Lidl BE", _ => "Lidl" };
        bool isBio    = raw.ProductName.Contains("bio", StringComparison.OrdinalIgnoreCase) ||
                        raw.ProductName.Contains("biologisch", StringComparison.OrdinalIgnoreCase);

        return [new ProductMatch
        {
            StoreName       = storeName,
            Country         = country,
            ProductName     = raw.ProductName,
            Price           = raw.Price,
            IsPromo         = raw.IsPromo,
            IsEstimated     = false,
            MatchConfidence = WordScore(item.Name, raw.ProductName),
            IsBiologisch    = isBio,
            IsVegan         = raw.ProductName.Contains("vegan", StringComparison.OrdinalIgnoreCase),
            IsHuisMerk      = true,
            IsAMerk         = false,
        }];
    }

    // ─── Lidl website zoek-API (publiek, geen IP-blokkade) ──────────
    private async Task<ScraperResult> TryLidlPlusApi(string query, string country)
    {
        try
        {
            // Lidl gebruikt een ingebedde Next.js API op hun website
            var domain = country switch { "DE" => "lidl.de", "BE" => "lidl.be", _ => "lidl.nl" };
            var lang   = country switch { "DE" => "de", "BE" => "nl", _ => "nl" };

            // Probeer de interne API-route die de website zelf gebruikt
            var url = $"https://www.{domain}/{lang}/s/?q={Uri.EscapeDataString(query)}&source=typeAheadSuggestion";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, */*");
            req.Headers.TryAddWithoutValidation("Referer", $"https://www.{domain}/");
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return new ScraperResult(query, 0, false);

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '[' && json.TrimStart()[0] != '{')
                return new ScraperResult(query, 0, false);

            using var doc = JsonDocument.Parse(json);

            // Verschillende Lidl response structuren proberen
            var root = doc.RootElement;
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array)
                items = root;
            else if (!root.TryGetProperty("gridList",  out items) &&
                     !root.TryGetProperty("results",   out items) &&
                     !root.TryGetProperty("products",  out items) &&
                     !root.TryGetProperty("data",      out items))
                return new ScraperResult(query, 0, false);

            foreach (var item in items.EnumerateArray())
            {
                decimal price = ExtractLidlPrice(item);
                if (price <= 0) continue;
                var title = item.TryGetProperty("fullTitle", out var ft) ? ft.GetString() ?? query :
                            item.TryGetProperty("name",      out var n)  ? n.GetString()  ?? query :
                            item.TryGetProperty("title",     out var tt) ? tt.GetString() ?? query : query;
                bool isPromo = item.TryGetProperty("isDiscount", out var isd) &&
                               isd.ValueKind == JsonValueKind.True;
                _logger.LogInformation("Lidl website {Country}: {Product} → €{Price}", country, title, price);
                return new ScraperResult(title, price, true) { IsPromo = isPromo };
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Lidl website API mislukt voor {Product}", query); }
        return new ScraperResult(query, 0, false);
    }

    // ─── Lidl gridboxes API ───────────────────────────────────────
    private async Task<ScraperResult> TryLidlApi(string query, string country)
    {
        try
        {
            var (domain, locale) = country switch
            {
                "DE" => ("lidl.de", "DE/de"),
                "BE" => ("lidl.be", "BE/nl"),
                _    => ("lidl.nl", "NL/nl")
            };

            var url = $"https://www.{domain}/p/api/gridboxes/{locale}/" +
                      $"?max=5&search={Uri.EscapeDataString(query)}";

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new ScraperResult(query, 0, false);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new ScraperResult(query, 0, false);

            foreach (var box in doc.RootElement.EnumerateArray())
            {
                if (!box.TryGetProperty("gridList", out var list)) continue;
                foreach (var item in list.EnumerateArray())
                {
                    decimal price = ExtractLidlPrice(item);
                    if (price <= 0) continue;
                    string title = item.TryGetProperty("keyfacts", out var kf) &&
                                   kf.TryGetProperty("name",      out var n)
                                   ? n.GetString() ?? query : query;
                    bool isPromo = item.TryGetProperty("price", out var po) &&
                                   po.TryGetProperty("discount", out _);
                    _logger.LogInformation("Lidl grid {Country}: {Product} → €{Price}", country, title, price);
                    return new ScraperResult(title, price, true) { IsPromo = isPromo };
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Lidl API mislukt voor {Product}", query); }
        return new ScraperResult(query, 0, false);
    }

    private static decimal ExtractLidlPrice(JsonElement item)
    {
        if (item.TryGetProperty("price", out var priceObj))
        {
            if (priceObj.TryGetProperty("price",        out var p) && p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
            if (priceObj.TryGetProperty("regularPrice", out var rp) && rp.ValueKind == JsonValueKind.Number) return rp.GetDecimal();
        }
        if (item.TryGetProperty("priceString", out var ps))
        {
            var str = ps.GetString()?.Replace(",", ".") ?? "";
            if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p2)) return p2;
        }
        return 0;
    }

    private async Task<ScraperResult> TryLidlHtml(string query, string country)
    {
        try
        {
            var domain = country switch { "DE" => "lidl.de", "BE" => "lidl.be", _ => "lidl.nl" };
            var url    = country == "DE"
                ? $"https://www.lidl.de/suche?query={Uri.EscapeDataString(query)}"
                : $"https://www.{domain}/zoeken/?q={Uri.EscapeDataString(query)}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new ScraperResult(query, 0, false);
            var html = await response.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);
            var priceNode = doc.DocumentNode.SelectSingleNode(
                "//span[contains(@class,'m-price__price')] | " +
                "//span[contains(@class,'pricebox__price')] | " +
                "//div[contains(@class,'product-grid-box')]//span[contains(@class,'price')]");
            if (priceNode != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    priceNode.InnerText.Replace(",", "."), @"\d+\.\d{2}").Value;
                if (decimal.TryParse(m, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                    return new ScraperResult(query, p, true);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Lidl HTML fout voor {Product}", query); }
        return new ScraperResult(query, 0, false);
    }

    private static double WordScore(string q, string p)
    {
        var words = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0.7;
        return (double)words.Count(w => p.ToLower().Contains(w)) / words.Length;
    }
}

// ─────────────────────────────────────────────────────────────────
//  ALDI  (NL / BE / DE)
// ─────────────────────────────────────────────────────────────────
public class AldiScraper
{
    private readonly HttpClient           _http;
    private readonly ILogger<AldiScraper> _logger;

    public AldiScraper(HttpClient http, ILogger<AldiScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string country = "NL")
    {
        var raw = country == "DE"
            ? await ScrapeAldiSued(item.Name)
            : await ScrapeAldiNl(item.Name, country);

        if (!raw.Success) return [];

        var storeName = country == "DE" ? "Aldi Süd" : (country == "BE" ? "Aldi BE" : "Aldi");
        bool isBio    = raw.ProductName.Contains("bio", StringComparison.OrdinalIgnoreCase);

        return [new ProductMatch
        {
            StoreName       = storeName,
            Country         = country,
            ProductName     = raw.ProductName,
            Price           = raw.Price,
            IsPromo         = raw.IsPromo,
            IsEstimated     = false,
            MatchConfidence = WordScore(item.Name, raw.ProductName),
            IsBiologisch    = isBio,
            IsVegan         = raw.ProductName.Contains("vegan", StringComparison.OrdinalIgnoreCase),
            IsHuisMerk      = true,
            IsAMerk         = false,
        }];
    }

    // ─── Aldi NL/BE via JSON API ──────────────────────────────────
    private async Task<ScraperResult> ScrapeAldiNl(string query, string country)
    {
        try
        {
            // Aldi NL heeft een interne JSON search API
            var domain = country == "BE" ? "aldi.be" : "aldi.nl";
            var url    = $"https://www.{domain}/nl/zoeken.html?q={Uri.EscapeDataString(query)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
            req.Headers.TryAddWithoutValidation("Referer", $"https://www.{domain}/");

            var response = await _http.SendAsync(req);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                JsonElement items;
                if (doc.RootElement.TryGetProperty("products",     out items) ||
                    doc.RootElement.TryGetProperty("searchResults", out items) ||
                    doc.RootElement.TryGetProperty("results",       out items))
                {
                    foreach (var p in items.EnumerateArray())
                    {
                        decimal price = ExtractAldiJsonPrice(p);
                        if (price <= 0) continue;
                        var name = p.TryGetProperty("name",  out var n) ? n.GetString() ?? query :
                                   p.TryGetProperty("title", out var t) ? t.GetString() ?? query : query;
                        _logger.LogInformation("Aldi {Country} JSON: {Product} → €{Price}", country, name, price);
                        return new ScraperResult(name, price, true);
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Aldi NL JSON API mislukt voor {Product}", query); }

        // Fallback: HTML scraping
        return await ScrapeAldiNlHtml(query, country);
    }

    private static decimal ExtractAldiJsonPrice(JsonElement p)
    {
        string[] priceFields = ["price", "currentPrice", "regularPrice", "normalPrice"];
        foreach (var field in priceFields)
        {
            if (!p.TryGetProperty(field, out var po)) continue;
            if (po.ValueKind == JsonValueKind.Number) return po.GetDecimal();
            if (po.TryGetProperty("value",   out var v))  return v.GetDecimal();
            if (po.TryGetProperty("amount",  out var a))  return a.GetDecimal();
            if (po.TryGetProperty("regular", out var reg) && reg.ValueKind == JsonValueKind.Number) return reg.GetDecimal();
        }
        return 0;
    }

    private async Task<ScraperResult> ScrapeAldiNlHtml(string query, string country)
    {
        try
        {
            var domain = country == "BE" ? "aldi.be" : "aldi.nl";
            var url    = $"https://www.{domain}/nl/zoeken.html?q={Uri.EscapeDataString(query)}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new ScraperResult(query, 0, false);
            var html = await response.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);
            return ExtractAldiPrice(doc, query);
        }
        catch (Exception ex) { _logger.LogError(ex, "Aldi NL HTML fout voor {Product}", query); }
        return new ScraperResult(query, 0, false);
    }

    // ─── Aldi Süd (DE) ────────────────────────────────────────────
    private async Task<ScraperResult> ScrapeAldiSued(string query)
    {
        try
        {
            var url = $"https://api.aldi-sued.de/v1/search" +
                      $"?term={Uri.EscapeDataString(query)}&page=1&pageSize=5&country=DE";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-api-key", "public");

            var response = await _http.SendAsync(req);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        decimal price = 0;
                        if (item.TryGetProperty("pricing", out var pr) &&
                            pr.TryGetProperty("currentPrice", out var cp))
                            price = cp.GetDecimal();
                        if (price <= 0) continue;
                        var title = item.TryGetProperty("name", out var n) ? n.GetString() ?? query : query;
                        _logger.LogInformation("Aldi Süd DE: {Product} → €{Price}", title, price);
                        return new ScraperResult(title, price, true);
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Aldi Süd API mislukt voor {Product}", query); }

        return await ScrapeAldiSuedHtml(query);
    }

    private async Task<ScraperResult> ScrapeAldiSuedHtml(string query)
    {
        try
        {
            var url      = $"https://www.aldi-sued.de/de/sortiment/suche.html?q={Uri.EscapeDataString(query)}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new ScraperResult(query, 0, false);
            var html = await response.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);
            return ExtractAldiPrice(doc, query);
        }
        catch (Exception ex) { _logger.LogError(ex, "Aldi Süd HTML fout voor {Product}", query); }
        return new ScraperResult(query, 0, false);
    }

    private ScraperResult ExtractAldiPrice(HtmlDocument doc, string query)
    {
        string[] selectors =
        [
            "//span[contains(@class,'price__wrapper')]",
            "//span[contains(@class,'mod-article-tile__price')]",
            "//div[contains(@class,'price-box')]//span[contains(@class,'price')]",
            "//meta[@itemprop='price']",
            "//span[@class='price']",
            "//span[contains(@class,'product-tile__price')]",
            "//div[contains(@class,'product-tile')]//span[contains(@class,'price')]",
        ];
        foreach (var sel in selectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(sel);
            if (node == null) continue;
            var raw = node.Name == "meta" ? node.GetAttributeValue("content", "") : node.InnerText;
            var m   = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", "."), @"\d+\.\d{2}");
            if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                return new ScraperResult(query, p, true);
        }
        return new ScraperResult(query, 0, false);
    }

    private static double WordScore(string q, string p)
    {
        var words = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0.65;
        return (double)words.Count(w => p.ToLower().Contains(w)) / words.Length;
    }
}

// ─────────────────────────────────────────────────────────────────
//  DM  (Drogist — NL / DE)
// ─────────────────────────────────────────────────────────────────
public class DmScraper
{
    private readonly HttpClient         _http;
    private readonly ILogger<DmScraper> _logger;

    public DmScraper(HttpClient http, ILogger<DmScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string country = "NL")
    {
        try
        {
            var (domain, locale) = country == "DE" ? ("dm.de", "de_DE") : ("dm.nl", "nl_NL");
            var url = $"https://product-search.services.dmtech.com/{locale}/search/crawl" +
                      $"?q={Uri.EscapeDataString(item.Name)}&pageSize=5&currentPage=0";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("dm-app-version", "1.0");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return await TryDmHtml(item, country);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products))
                return await TryDmHtml(item, country);

            foreach (var p in products.EnumerateArray())
            {
                decimal price = 0;
                if (p.TryGetProperty("price", out var po) && po.TryGetProperty("value", out var v))
                    price = v.GetDecimal();
                if (price <= 0) continue;

                var name  = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : item.Name;
                bool isBio = name.Contains("bio", StringComparison.OrdinalIgnoreCase);

                _logger.LogInformation("DM {Country}: {Product} → €{Price}", country, name, price);
                return [new ProductMatch
                {
                    StoreName = "DM", Country = country, ProductName = name, Price = price,
                    IsEstimated = false, IsBiologisch = isBio, MatchConfidence = WordScore(item.Name, name)
                }];
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DM API fout voor {Product}", item.Name); }
        return await TryDmHtml(item, country);
    }

    private async Task<List<ProductMatch>> TryDmHtml(GroceryItem item, string country)
    {
        try
        {
            var domain = country == "DE" ? "dm.de" : "dm.nl";
            var url    = $"https://www.{domain}/search?query={Uri.EscapeDataString(item.Name)}";
            var resp   = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return [];
            var html = await resp.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);
            var priceNode = doc.DocumentNode.SelectSingleNode(
                "//span[contains(@class,'price') and not(contains(@class,'old'))]");
            if (priceNode != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    priceNode.InnerText.Replace(",", "."), @"\d+\.\d{2}");
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                    return [new ProductMatch
                    {
                        StoreName = "DM", Country = country, ProductName = item.Name,
                        Price = p, IsEstimated = false, MatchConfidence = 0.7
                    }];
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "DM HTML fout voor {Product}", item.Name); }
        return [];
    }

    private static double WordScore(string q, string p)
    {
        var words = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        return (double)words.Count(w => p.ToLower().Contains(w)) / words.Length;
    }
}

// ─────────────────────────────────────────────────────────────────
//  REWE  (DE)
// ─────────────────────────────────────────────────────────────────
public class ReweScraper
{
    private readonly HttpClient           _http;
    private readonly ILogger<ReweScraper> _logger;

    public ReweScraper(HttpClient http, ILogger<ReweScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        try
        {
            var url = $"https://shop.rewe.de/api/v7/products" +
                      $"?search={Uri.EscapeDataString(item.Name)}" +
                      $"&page=1&pageSize=5&marketId=562223&sorting=RELEVANCE";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://shop.rewe.de/");
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return await TryReweHtml(item);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            JsonElement products;
            if (!doc.RootElement.TryGetProperty("products", out products) &&
                !doc.RootElement.TryGetProperty("items",    out products))
                return await TryReweHtml(item);

            foreach (var p in products.EnumerateArray())
            {
                decimal price = ExtractRewePrice(p);
                if (price <= 0) continue;
                var name = p.TryGetProperty("name",  out var n) ? n.GetString() ?? "" :
                           p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : item.Name;
                bool isBio = name.Contains("bio", StringComparison.OrdinalIgnoreCase);
                _logger.LogInformation("REWE DE: {Product} → €{Price}", name, price);
                return [new ProductMatch
                {
                    StoreName = "Rewe", Country = "DE", ProductName = name, Price = price,
                    IsEstimated = false, IsBiologisch = isBio, MatchConfidence = WordScore(item.Name, name)
                }];
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "REWE API fout voor {Product}", item.Name); }
        return await TryReweHtml(item);
    }

    private static decimal ExtractRewePrice(JsonElement p)
    {
        if (p.TryGetProperty("pricing", out var pr))
        {
            if (pr.TryGetProperty("currentRetailPrice", out var crp)) return crp.GetDecimal();
            if (pr.TryGetProperty("price",              out var pp))  return pp.GetDecimal();
        }
        if (p.TryGetProperty("price", out var direct))
        {
            if (direct.ValueKind == JsonValueKind.Number) return direct.GetDecimal();
            if (direct.TryGetProperty("value", out var v)) return v.GetDecimal();
        }
        return 0;
    }

    private async Task<List<ProductMatch>> TryReweHtml(GroceryItem item)
    {
        try
        {
            var response = await _http.GetAsync(
                $"https://www.rewe.de/suche/?search={Uri.EscapeDataString(item.Name)}");
            if (!response.IsSuccessStatusCode) return [];
            var html = await response.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);
            var node = doc.DocumentNode.SelectSingleNode(
                "//*[@data-testid='product-price'] | //span[contains(@class,'price__value')]");
            if (node != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    node.InnerText.Replace(",", "."), @"\d+\.\d{2}");
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                    return [new ProductMatch
                    {
                        StoreName = "Rewe", Country = "DE", ProductName = item.Name,
                        Price = p, IsEstimated = false, MatchConfidence = 0.7
                    }];
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "REWE HTML fout voor {Product}", item.Name); }
        return [];
    }

    private static double WordScore(string q, string p)
    {
        var words = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        return (double)words.Count(w => p.ToLower().Contains(w)) / words.Length;
    }
}

// ─────────────────────────────────────────────────────────────────
//  EDEKA  (DE)
// ─────────────────────────────────────────────────────────────────
public class EdekaScraper
{
    private readonly HttpClient            _http;
    private readonly ILogger<EdekaScraper> _logger;

    public EdekaScraper(HttpClient http, ILogger<EdekaScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        try
        {
            var url = $"https://www.edeka.de/api/search/v1/products" +
                      $"?q={Uri.EscapeDataString(item.Name)}&page=0&size=5";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.edeka.de/");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return await TryEdekaHtml(item);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            JsonElement items;
            if (!doc.RootElement.TryGetProperty("products", out items) &&
                !doc.RootElement.TryGetProperty("items",    out items) &&
                !doc.RootElement.TryGetProperty("content",  out items))
                return await TryEdekaHtml(item);

            foreach (var p in items.EnumerateArray())
            {
                decimal price = ExtractEdekaPrice(p);
                if (price <= 0) continue;
                var name = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" :
                           p.TryGetProperty("name",  out var n) ? n.GetString() ?? "" : item.Name;
                bool isBio = name.Contains("bio", StringComparison.OrdinalIgnoreCase);
                _logger.LogInformation("Edeka DE: {Product} → €{Price}", name, price);
                return [new ProductMatch
                {
                    StoreName = "Edeka", Country = "DE", ProductName = name, Price = price,
                    IsEstimated = false, IsBiologisch = isBio, MatchConfidence = WordScore(item.Name, name)
                }];
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Edeka API fout voor {Product}", item.Name); }
        return await TryEdekaHtml(item);
    }

    private static decimal ExtractEdekaPrice(JsonElement p)
    {
        if (p.TryGetProperty("price", out var po))
        {
            if (po.ValueKind == JsonValueKind.Number) return po.GetDecimal();
            if (po.TryGetProperty("value",        out var v))  return v.GetDecimal();
            if (po.TryGetProperty("currentPrice", out var cp)) return cp.GetDecimal();
        }
        return 0;
    }

    private async Task<List<ProductMatch>> TryEdekaHtml(GroceryItem item)
    {
        try
        {
            var response = await _http.GetAsync(
                $"https://www.edeka.de/produkte/suchergebnis.jsp?query={Uri.EscapeDataString(item.Name)}");
            if (!response.IsSuccessStatusCode) return [];
            var html = await response.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);
            var node = doc.DocumentNode.SelectSingleNode(
                "//span[contains(@class,'product-detail__price')] | //p[contains(@class,'product-tile__price')]");
            if (node != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    node.InnerText.Replace(",", "."), @"\d+\.\d{2}");
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                    return [new ProductMatch
                    {
                        StoreName = "Edeka", Country = "DE", ProductName = item.Name,
                        Price = p, IsEstimated = false, MatchConfidence = 0.7
                    }];
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Edeka HTML fout voor {Product}", item.Name); }
        return [];
    }

    private static double WordScore(string q, string p)
    {
        var words = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        return (double)words.Count(w => p.ToLower().Contains(w)) / words.Length;
    }
}


