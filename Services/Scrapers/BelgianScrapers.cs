using SmartShopper.API.Models;
using System.Text.Json;
using System.Globalization;
using HtmlAgilityPack;

namespace SmartShopper.API.Services.Scrapers;

// ─────────────────────────────────────────────────────────────────
//  COLRUYT  (BE)
// ─────────────────────────────────────────────────────────────────
public class ColruytScraper
{
    private readonly HttpClient             _http;
    private readonly ILogger<ColruytScraper> _logger;

    public ColruytScraper(HttpClient http, ILogger<ColruytScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-BE,nl;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        // Stap 1: Colruyt publieke catalog API (meest stabiel)
        var result = await TryColruytCatalogApi(item.Name);

        // Stap 2: Legacy ECG endpoint (was soms geblokkeerd)
        if (result == null) result = await TryColruytLegacyApi(item.Name);

        // Stap 3: HTML fallback
        if (result == null) result = await TryColruytHtml(item.Name);

        if (result == null) return [];

        var score = ProductMatcher.Score(item.Name, result.ProductName);
        bool isBio   = result.ProductName.Contains("bio", StringComparison.OrdinalIgnoreCase) ||
                       result.ProductName.Contains("biologisch", StringComparison.OrdinalIgnoreCase);
        bool isVegan = result.ProductName.Contains("vegan", StringComparison.OrdinalIgnoreCase);

        return [new ProductMatch
        {
            StoreName       = "Colruyt",
            Country         = "BE",
            ProductName     = result.ProductName,
            Price           = result.Price,
            IsPromo         = result.IsPromo,
            IsEstimated     = false,
            MatchConfidence = score,
            IsBiologisch    = isBio,
            IsVegan         = isVegan,
            IsHuisMerk      = result.ProductName.Contains("Boni", StringComparison.OrdinalIgnoreCase) ||
                              result.ProductName.Contains("ColruytBest", StringComparison.OrdinalIgnoreCase),
            IsAMerk         = !result.ProductName.Contains("Boni", StringComparison.OrdinalIgnoreCase),
        }];
    }

    // ─── Colruyt publieke catalog API ────────────────────────────
    private async Task<ScraperResult?> TryColruytCatalogApi(string query)
    {
        try
        {
            // Colruyt gebruikt een publiek toegankelijke catalog search
            var url = $"https://www.colruyt.be/colruytAPI/api/products" +
                      $"?text={Uri.EscapeDataString(query)}&placeId=&site=colruyt&language=NL&count=10";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer",           "https://www.colruyt.be/");
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
            req.Headers.TryAddWithoutValidation("Origin",            "https://www.colruyt.be");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);

            // Probeer verschillende response structuren
            JsonElement products = default;
            bool found =
                doc.RootElement.TryGetProperty("products", out products) ||
                doc.RootElement.TryGetProperty("data",     out products) ||
                doc.RootElement.TryGetProperty("items",    out products);

            if (!found || products.ValueKind != JsonValueKind.Array) return null;

            return FindBestColruytMatch(products, query, "Colruyt catalog");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Colruyt catalog API mislukt voor {Product}", query); }
        return null;
    }

    // ─── Legacy ECG endpoint ─────────────────────────────────────
    private async Task<ScraperResult?> TryColruytLegacyApi(string query)
    {
        try
        {
            var urls = new[]
            {
                $"https://ecg.colruyt.be/PRODUITS/services/searchProducts?searchTerm={Uri.EscapeDataString(query)}&start=0&count=10&site=Colruyt",
                $"https://www.colruyt.be/colruytAPI/api/v2/products?text={Uri.EscapeDataString(query)}&count=10",
            };

            foreach (var url in urls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Referer", "https://www.colruyt.be/");

                    var response = await _http.SendAsync(req);
                    if (!response.IsSuccessStatusCode) continue;

                    var json = await response.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    using var doc = JsonDocument.Parse(json);

                    JsonElement products = default;
                    bool found =
                        doc.RootElement.TryGetProperty("products", out products) ||
                        doc.RootElement.TryGetProperty("data",     out products) ||
                        doc.RootElement.TryGetProperty("items",    out products);

                    if (!found || products.ValueKind != JsonValueKind.Array) continue;

                    var result = FindBestColruytMatch(products, query, "Colruyt legacy");
                    if (result != null) return result;
                }
                catch { /* probeer volgende */ }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Colruyt legacy API mislukt voor {Product}", query); }
        return null;
    }

    private ScraperResult? FindBestColruytMatch(JsonElement products, string query, string source)
    {
        var candidates = new List<(string Name, decimal Price, bool IsPromo, double Score)>();

        foreach (var p in products.EnumerateArray())
        {
            decimal price = ExtractPrice(p, ["price", "basicPrice", "currentPrice", "recommendedPrice", "normalPrice"]);
            if (price <= 0) continue;

            var name = GetString(p, ["name", "title", "description"]) ?? query;
            bool isPromo = p.TryGetProperty("promotion", out _) || p.TryGetProperty("isPromo", out _);
            var score = ProductMatcher.Score(query, name);

            candidates.Add((name, price, isPromo, score));
        }

        if (candidates.Count == 0) return null;

        var best = candidates.OrderByDescending(c => c.Score).First();
        _logger.LogInformation("{Source}: '{Name}' €{Price} (score {Score:F2}) voor '{Query}'",
            source, best.Name, best.Price, best.Score, query);

        return new ScraperResult(best.Name, best.Price, true) { IsPromo = best.IsPromo };
    }

    private async Task<ScraperResult?> TryColruytHtml(string query)
    {
        try
        {
            var url  = $"https://www.colruyt.be/nl/zoekopdracht/{Uri.EscapeDataString(query)}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);

            string[] selectors =
            [
                "//span[contains(@class,'product-price__price')]",
                "//span[contains(@class,'price-box')]",
                "//p[contains(@class,'price')]",
                "//*[@data-testid='product-price']",
                "//meta[@itemprop='price']",
                "//span[contains(@class,'price') and not(contains(@class,'old'))]",
            ];

            foreach (var sel in selectors)
            {
                var node = doc.DocumentNode.SelectSingleNode(sel);
                if (node == null) continue;
                var raw = node.Name == "meta" ? node.GetAttributeValue("content", "") : node.InnerText;
                var m   = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", "."), @"\d+\.\d{2}");
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                {
                    _logger.LogInformation("Colruyt HTML: {Product} → €{Price}", query, p);
                    return new ScraperResult(query, p, true);
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Colruyt HTML fout voor {Product}", query); }
        return null;
    }

    private static decimal ExtractPrice(JsonElement el, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!el.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner))
                return inner.GetDecimal();
            if (v.ValueKind == JsonValueKind.String &&
                decimal.TryParse(v.GetString()?.Replace(",", "."), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out decimal parsed))
                return parsed;
        }
        return 0;
    }

    private static string? GetString(JsonElement el, string[] keys)
    {
        foreach (var key in keys)
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }
}

// ─────────────────────────────────────────────────────────────────
//  DELHAIZE  (BE)
// ─────────────────────────────────────────────────────────────────
public class DelhaizeScraper
{
    private readonly HttpClient              _http;
    private readonly ILogger<DelhaizeScraper> _logger;

    public DelhaizeScraper(HttpClient http, ILogger<DelhaizeScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-BE,nl;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        var result = await TryDelhaizeApi(item.Name);
        if (result == null) result = await TryDelhaizeAlternativeApi(item.Name);
        if (result == null) result = await TryDelhaizeHtml(item.Name);
        if (result == null) return [];

        var score = ProductMatcher.Score(item.Name, result.ProductName);
        bool isBio   = result.ProductName.Contains("bio", StringComparison.OrdinalIgnoreCase) ||
                       result.ProductName.Contains("biologisch", StringComparison.OrdinalIgnoreCase);
        bool isVegan = result.ProductName.Contains("vegan", StringComparison.OrdinalIgnoreCase);

        return [new ProductMatch
        {
            StoreName       = "Delhaize",
            Country         = "BE",
            ProductName     = result.ProductName,
            Price           = result.Price,
            IsPromo         = result.IsPromo,
            IsEstimated     = false,
            MatchConfidence = score,
            IsBiologisch    = isBio,
            IsVegan         = isVegan,
            IsHuisMerk      = result.ProductName.Contains("365", StringComparison.OrdinalIgnoreCase) ||
                              result.ProductName.Contains("Delhaize", StringComparison.OrdinalIgnoreCase),
            IsAMerk         = !result.ProductName.Contains("365", StringComparison.OrdinalIgnoreCase),
        }];
    }

    private async Task<ScraperResult?> TryDelhaizeApi(string query)
    {
        try
        {
            // Primaire Delhaize API
            var url = $"https://www.delhaize.be/api/v2/search?q={Uri.EscapeDataString(query)}" +
                      $"&pageSize=10&currentPage=0&lang=nl&fields=FULL";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.delhaize.be/");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            JsonElement products = default;
            bool found = doc.RootElement.TryGetProperty("products",      out products) ||
                         doc.RootElement.TryGetProperty("results",        out products) ||
                         doc.RootElement.TryGetProperty("searchResults",  out products);
            if (!found) return null;

            return FindBestDelhaizeMatch(products, query, "Delhaize API");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delhaize API mislukt voor {Product}", query); }
        return null;
    }

    private async Task<ScraperResult?> TryDelhaizeAlternativeApi(string query)
    {
        try
        {
            // Alternatief: Delhaize GraphQL of andere publieke endpoint
            var url = $"https://www.delhaize.be/nl-be/search?text={Uri.EscapeDataString(query)}&format=json";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer",     "https://www.delhaize.be/");
            req.Headers.TryAddWithoutValidation("Accept",      "application/json");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '{') return null;

            using var doc = JsonDocument.Parse(json);

            JsonElement products = default;
            bool found = doc.RootElement.TryGetProperty("products",  out products) ||
                         doc.RootElement.TryGetProperty("results",   out products) ||
                         doc.RootElement.TryGetProperty("data",      out products);
            if (!found) return null;

            return FindBestDelhaizeMatch(products, query, "Delhaize alt API");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delhaize alt API mislukt voor {Product}", query); }
        return null;
    }

    private ScraperResult? FindBestDelhaizeMatch(JsonElement products, string query, string source)
    {
        var candidates = new List<(string Name, decimal Price, bool IsPromo, double Score)>();

        foreach (var p in products.EnumerateArray())
        {
            decimal price = TryGetDelhaizePrice(p);
            if (price <= 0) continue;

            var name = (p.TryGetProperty("name",  out var n) ? n.GetString() : null) ??
                       (p.TryGetProperty("title", out var t) ? t.GetString() : null) ?? query;

            bool isPromo = p.TryGetProperty("discountedPrice", out _) ||
                           p.TryGetProperty("promotionPrice",  out _);

            candidates.Add((name, price, isPromo, ProductMatcher.Score(query, name)));
        }

        if (candidates.Count == 0) return null;

        var best = candidates.OrderByDescending(c => c.Score).First();
        _logger.LogInformation("{Source}: '{Name}' €{Price} (score {Score:F2}) voor '{Query}'",
            source, best.Name, best.Price, best.Score, query);

        return new ScraperResult(best.Name, best.Price, true) { IsPromo = best.IsPromo };
    }

    private static decimal TryGetDelhaizePrice(JsonElement p)
    {
        string[] priceKeys = ["price", "currentPrice", "normalPrice", "priceValue", "value"];
        foreach (var key in priceKeys)
        {
            if (!p.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("value",          out var val)) return val.GetDecimal();
                if (v.TryGetProperty("formattedValue", out var fv))
                {
                    var s = fv.GetString()?.Replace("€", "").Replace(",", ".").Trim();
                    if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d)) return d;
                }
            }
        }
        return 0;
    }

    private async Task<ScraperResult?> TryDelhaizeHtml(string query)
    {
        try
        {
            var url  = $"https://www.delhaize.be/nl-be/recherche?text={Uri.EscapeDataString(query)}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);

            string[] selectors =
            [
                "//span[contains(@class,'item-price')]",
                "//div[contains(@class,'product-price')]//span",
                "//*[@data-testid='product-price']",
                "//meta[@itemprop='price']",
                "//span[contains(@class,'price') and not(contains(@class,'old'))]",
            ];

            foreach (var sel in selectors)
            {
                var node = doc.DocumentNode.SelectSingleNode(sel);
                if (node == null) continue;
                var raw = node.Name == "meta" ? node.GetAttributeValue("content", "") : node.InnerText;
                var m   = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", "."), @"\d+\.\d{2}");
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                {
                    _logger.LogInformation("Delhaize HTML: {Product} → €{Price}", query, p);
                    return new ScraperResult(query, p, true);
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Delhaize HTML fout voor {Product}", query); }
        return null;
    }
}
