using System.Text.Json;
using System.Globalization;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

/// <summary>
/// Jumbo scraper via de publieke webshop zoek-API.
/// De mobiele API (v17) blokkeert cloud server IPs met 403.
/// De webshop API werkt wel vanaf servers.
/// </summary>
public class JumboScraper
{
    private readonly HttpClient            _http;
    private readonly ILogger<JumboScraper> _logger;

    private static readonly HashSet<string> HuisMerkPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jumbo", "jumbo biologisch", "jumbo puur & lekker", "jumbo fairtrade",
        "jumbo lactosevrij", "jumbo vegan", "jumbo economy"
    };

    public JumboScraper(HttpClient http, ILogger<JumboScraper> logger)
    {
        _http   = http;
        _logger = logger;

        // Volledige browser headers — mobiele API blokkeerde cloud IPs
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://www.jumbo.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin", "https://www.jumbo.com");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "sec-fetch-dest", "empty");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "sec-fetch-mode", "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "sec-fetch-site", "same-origin");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        // Probeer eerst de webshop JSON API
        var results = await TryWebshopApi(item);
        if (results.Count > 0) return results;

        // Fallback: probeer de algolia search API die Jumbo ook gebruikt
        results = await TryAlgoliaApi(item);
        if (results.Count > 0) return results;

        _logger.LogWarning("Jumbo: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    // ─── Jumbo webshop zoek API ───────────────────────────────────
    private async Task<List<ProductMatch>> TryWebshopApi(GroceryItem item)
    {
        try
        {
            var url = $"https://www.jumbo.com/api/search-page/v1?searchType=keyword" +
                      $"&searchTerms={Uri.EscapeDataString(item.Name)}&pageSize=5&currentPage=0";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");

            var response = await _http.SendAsync(req);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jumbo webshop API: {Status} voor '{Product}'",
                    response.StatusCode, item.Name);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Structuur: { "products": { "items": [...] } }
            if (!doc.RootElement.TryGetProperty("products", out var productsRoot)) return [];
            if (!productsRoot.TryGetProperty("items", out var items)) return [];

            return ParseProducts(items, item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo webshop API mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    // ─── Algolia fallback (Jumbo gebruikt dit intern ook) ────────
    private async Task<List<ProductMatch>> TryAlgoliaApi(GroceryItem item)
    {
        try
        {
            // Jumbo's publieke Algolia applicatie
            var url = "https://d3tsd07qon7u3w.cloudfront.net/";
            var body = new
            {
                requests = new[]
                {
                    new
                    {
                        indexName = "products",
                        @params = $"query={Uri.EscapeDataString(item.Name)}&hitsPerPage=5"
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = JsonContent.Create(body);
            req.Headers.TryAddWithoutValidation("x-algolia-application-id", "ILTPNR2PBR");
            req.Headers.TryAddWithoutValidation("x-algolia-api-key", "ee08ba4491a4c7cc6cd3c19cb7c9f2a7");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
            if (!results[0].TryGetProperty("hits", out var hits)) return [];

            return ParseAlgoliaProducts(hits, item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jumbo Algolia mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    // ─── Parser voor webshop API response ────────────────────────
    private List<ProductMatch> ParseProducts(JsonElement items, string query)
    {
        var results = new List<ProductMatch>();

        foreach (var p in items.EnumerateArray())
        {
            decimal price   = 0;
            bool    isPromo = false;
            string  promoText = "";

            // Webshop API prijs structuur
            if (p.TryGetProperty("prices", out var prices))
            {
                if (prices.TryGetProperty("price", out var priceObj))
                {
                    if (priceObj.TryGetProperty("amount", out var amt))
                        price = amt.GetDecimal() / 100m;
                    else if (priceObj.ValueKind == JsonValueKind.Number)
                        price = priceObj.GetDecimal();
                }

                if (prices.TryGetProperty("promotionalPrice", out var promo))
                {
                    isPromo = true;
                    if (promo.TryGetProperty("amount", out var promoAmt))
                        price = promoAmt.GetDecimal() / 100m;
                    promoText = "Jumbo aanbieding";
                }
            }
            else if (p.TryGetProperty("price", out var directPrice))
            {
                price = directPrice.ValueKind == JsonValueKind.Number
                    ? directPrice.GetDecimal()
                    : 0;
            }

            if (price <= 0) continue;

            var name  = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" :
                        p.TryGetProperty("name",  out var n) ? n.GetString() ?? "" : query;
            var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";

            bool isHuisMerk = IsHuisMerk(brand, name);
            bool isBio      = name.Contains("biologisch", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains(" bio ", StringComparison.OrdinalIgnoreCase);
            bool isVegan    = name.Contains("vegan", StringComparison.OrdinalIgnoreCase);

            results.Add(new ProductMatch
            {
                StoreName       = "Jumbo",
                Country         = "NL",
                ProductName     = name,
                Price           = price,
                IsPromo         = isPromo,
                PromoText       = promoText,
                IsEstimated     = false,
                MatchConfidence = WordScore(query, name),
                IsBiologisch    = isBio,
                IsVegan         = isVegan,
                IsHuisMerk      = isHuisMerk,
                IsAMerk         = !isHuisMerk,
                LastUpdated     = DateTime.UtcNow,
            });

            if (results.Count >= 3) break;
        }

        if (results.Count > 0)
            _logger.LogInformation("Jumbo webshop: {Count} resultaten voor '{Product}'",
                results.Count, query);

        return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
    }

    // ─── Parser voor Algolia response ────────────────────────────
    private List<ProductMatch> ParseAlgoliaProducts(JsonElement hits, string query)
    {
        var results = new List<ProductMatch>();

        foreach (var hit in hits.EnumerateArray())
        {
            decimal price = 0;

            if (hit.TryGetProperty("price", out var priceEl))
            {
                if (priceEl.ValueKind == JsonValueKind.Number)
                    price = priceEl.GetDecimal();
                else if (priceEl.TryGetProperty("current", out var cur))
                    price = cur.GetDecimal();
            }

            if (price <= 0 && hit.TryGetProperty("priceBeforeDiscount", out var pbd))
                price = pbd.GetDecimal();

            if (price <= 0) continue;

            var name  = hit.TryGetProperty("name",  out var n) ? n.GetString() ?? "" :
                        hit.TryGetProperty("title", out var t) ? t.GetString() ?? "" : query;
            var brand = hit.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";

            results.Add(new ProductMatch
            {
                StoreName       = "Jumbo",
                Country         = "NL",
                ProductName     = name,
                Price           = price,
                IsEstimated     = false,
                MatchConfidence = WordScore(query, name),
                IsBiologisch    = name.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                IsVegan         = name.Contains("vegan", StringComparison.OrdinalIgnoreCase),
                IsHuisMerk      = IsHuisMerk(brand, name),
                IsAMerk         = !IsHuisMerk(brand, name),
                LastUpdated     = DateTime.UtcNow,
            });

            if (results.Count >= 3) break;
        }

        if (results.Count > 0)
            _logger.LogInformation("Jumbo Algolia: {Count} resultaten voor '{Product}'",
                results.Count, query);

        return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
    }

    // ─── Helpers ─────────────────────────────────────────────────
    private static double WordScore(string query, string product)
    {
        var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        var lower = product.ToLower();
        return (double)words.Count(w => lower.Contains(w)) / words.Length;
    }

    private static bool IsHuisMerk(string brand, string name) =>
        HuisMerkPrefixes.Contains(brand) ||
        HuisMerkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
