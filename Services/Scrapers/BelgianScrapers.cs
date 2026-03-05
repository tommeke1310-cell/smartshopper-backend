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
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-BE,nl;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        var result = await TryColruytApi(item.Name);
        if (result == null) result = await TryColruytHtml(item.Name);
        if (result == null) return [];

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
            MatchConfidence = WordScore(item.Name, result.ProductName),
            IsBiologisch    = isBio,
            IsVegan         = isVegan,
            IsHuisMerk      = result.ProductName.Contains("Boni", StringComparison.OrdinalIgnoreCase),
            IsAMerk         = !result.ProductName.Contains("Boni", StringComparison.OrdinalIgnoreCase),
        }];
    }

    private async Task<ScraperResult?> TryColruytApi(string query)
    {
        try
        {
            // Colruyt gebruikt een interne product search API
            var url = $"https://ecg.colruyt.be/PRODUITS/services/searchProducts" +
                      $"?searchTerm={Uri.EscapeDataString(query)}&start=0&count=5&site=Colruyt";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.colruyt.be/");
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Probeer verschillende response structuren
            JsonElement products = default;
            if (doc.RootElement.TryGetProperty("products", out products) ||
                doc.RootElement.TryGetProperty("data",     out products) ||
                doc.RootElement.TryGetProperty("items",    out products))
            {
                foreach (var p in products.EnumerateArray())
                {
                    decimal price = ExtractPrice(p, ["price", "basicPrice", "currentPrice", "recommendedPrice"]);
                    if (price <= 0) continue;

                    var name = GetString(p, ["name", "title", "description"]) ?? query;
                    bool isPromo = p.TryGetProperty("promotion", out _) ||
                                   p.TryGetProperty("isPromo",   out _);

                    _logger.LogInformation("Colruyt BE: {Product} → €{Price}", name, price);
                    return new ScraperResult(name, price, true) { IsPromo = isPromo };
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Colruyt API mislukt voor {Product}", query); }
        return null;
    }

    private async Task<ScraperResult?> TryColruytHtml(string query)
    {
        try
        {
            var url  = $"https://www.colruyt.be/nl/zoekopdracht/{Uri.EscapeDataString(query)}";
            var html = await _http.GetStringAsync(url);
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);

            // Colruyt prijs selectors
            string[] selectors =
            [
                "//span[contains(@class,'product-price__price')]",
                "//span[contains(@class,'price-box')]",
                "//p[contains(@class,'price')]",
                "//*[@data-testid='product-price']",
                "//meta[@itemprop='price']",
            ];

            foreach (var sel in selectors)
            {
                var node = doc.DocumentNode.SelectSingleNode(sel);
                if (node == null) continue;
                var raw = node.Name == "meta" ? node.GetAttributeValue("content", "") : node.InnerText;
                var m   = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", "."), @"\d+\.\d{2}");
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                {
                    _logger.LogInformation("Colruyt HTML BE: {Product} → €{Price}", query, p);
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

    private static double WordScore(string q, string p)
    {
        var words = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0.65;
        return (double)words.Count(w => p.ToLower().Contains(w)) / words.Length;
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
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-BE,nl;q=0.9");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        var result = await TryDelhaizeApi(item.Name);
        if (result == null) result = await TryDelhaizeHtml(item.Name);
        if (result == null) return [];

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
            MatchConfidence = WordScore(item.Name, result.ProductName),
            IsBiologisch    = isBio,
            IsVegan         = isVegan,
            IsHuisMerk      = result.ProductName.Contains("365", StringComparison.OrdinalIgnoreCase),
            IsAMerk         = !result.ProductName.Contains("365", StringComparison.OrdinalIgnoreCase),
        }];
    }

    private async Task<ScraperResult?> TryDelhaizeApi(string query)
    {
        try
        {
            // Delhaize gebruikt de Okta-beveiligde shop API — probeer publieke zoek endpoint
            var url = $"https://www.delhaize.be/api/v2/search?q={Uri.EscapeDataString(query)}" +
                      $"&pageSize=5&currentPage=0&lang=nl";

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

            foreach (var p in products.EnumerateArray())
            {
                decimal price = TryGetDelhaizePrice(p);
                if (price <= 0) continue;

                var name = (p.TryGetProperty("name",  out var n) ? n.GetString() : null) ??
                           (p.TryGetProperty("title", out var t) ? t.GetString() : null) ?? query;

                bool isPromo = p.TryGetProperty("discountedPrice", out _) ||
                               p.TryGetProperty("promotionPrice",  out _);

                _logger.LogInformation("Delhaize BE: {Product} → €{Price}", name, price);
                return new ScraperResult(name, price, true) { IsPromo = isPromo };
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delhaize API mislukt voor {Product}", query); }
        return null;
    }

    private static decimal TryGetDelhaizePrice(JsonElement p)
    {
        // Delhaize kan price op meerdere niveaus hebben
        string[] priceKeys = ["price", "currentPrice", "normalPrice", "priceValue"];
        foreach (var key in priceKeys)
        {
            if (!p.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("value",        out var val)) return val.GetDecimal();
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
            var html = await _http.GetStringAsync(url);
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
                    _logger.LogInformation("Delhaize HTML BE: {Product} → €{Price}", query, p);
                    return new ScraperResult(query, p, true);
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Delhaize HTML fout voor {Product}", query); }
        return null;
    }

    private static double WordScore(string q, string p)
    {
        var words = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0.65;
        return (double)words.Count(w => p.ToLower().Contains(w)) / words.Length;
    }
}
