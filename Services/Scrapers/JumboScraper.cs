using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

public class JumboScraper
{
    private readonly HttpClient            _http;
    private readonly ILogger<JumboScraper> _logger;

    private static readonly HashSet<string> HuisMerkPrefixes = new(StringComparer.OrdinalIgnoreCase)
        { "jumbo", "jumbo biologisch", "jumbo puur & lekker", "jumbo fairtrade" };

    public JumboScraper(HttpClient http, ILogger<JumboScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.jumbo.com/");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        // Stap 1: Jumbo v17 mobile API
        var results = await TryMobileApi(item);
        if (results.Count > 0) return results;

        // Stap 2: Jumbo webshop search-page API
        results = await TryWebshopApi(item);
        if (results.Count > 0) return results;

        _logger.LogWarning("Jumbo: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    // ─── Jumbo mobile API v17 ─────────────────────────────────────
    private async Task<List<ProductMatch>> TryMobileApi(GroceryItem item)
    {
        try
        {
            var url = $"https://mobileapi.jumbo.com/v17/search" +
                      $"?q={Uri.EscapeDataString(item.Name)}&pageSize=5&currentPage=0&language=nl";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-jumbo-app-version", "10.0.0");
            req.Headers.TryAddWithoutValidation("app-version", "10.0.0");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jumbo mobile API: {Status}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Structuur: { "products": { "data": [...] } }
            if (!doc.RootElement.TryGetProperty("products", out var pr)) return [];
            if (!pr.TryGetProperty("data", out var data)) return [];

            return ParseJumboProducts(data, item.Name, "mobile");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo mobile API mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    // ─── Jumbo webshop API fallback ───────────────────────────────
    private async Task<List<ProductMatch>> TryWebshopApi(GroceryItem item)
    {
        try
        {
            var url = $"https://www.jumbo.com/api/search-page/v1?searchType=keyword" +
                      $"&searchTerms={Uri.EscapeDataString(item.Name)}&pageSize=5&currentPage=0";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("products", out var productsRoot)) return [];
            if (!productsRoot.TryGetProperty("items", out var items)) return [];

            return ParseJumboProducts(items, item.Name, "webshop");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo webshop API mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    private List<ProductMatch> ParseJumboProducts(JsonElement items, string query, string source)
    {
        var results = new List<ProductMatch>();

        foreach (var p in items.EnumerateArray())
        {
            decimal price = 0;
            bool isPromo = false;
            string promoText = "";

            // Mobile API structuur: p.data.prices of p.prices
            var priceRoot = p.TryGetProperty("data", out var d) ? d : p;

            if (priceRoot.TryGetProperty("prices", out var prices))
            {
                if (prices.TryGetProperty("price", out var priceObj))
                {
                    price = priceObj.TryGetProperty("amount", out var amt)
                        ? amt.GetDecimal() / 100m
                        : priceObj.ValueKind == JsonValueKind.Number ? priceObj.GetDecimal() : 0;
                }
                if (prices.TryGetProperty("promotionalPrice", out var promo) && promo.ValueKind != JsonValueKind.Null)
                {
                    isPromo   = true;
                    promoText = "Jumbo aanbieding";
                    if (promo.TryGetProperty("amount", out var promoAmt))
                        price = promoAmt.GetDecimal() / 100m;
                }
            }
            else if (priceRoot.TryGetProperty("price", out var directPrice))
            {
                price = directPrice.ValueKind == JsonValueKind.Number ? directPrice.GetDecimal() : 0;
            }

            if (price <= 0) continue;

            var titleEl = priceRoot.TryGetProperty("title", out var t) ? t :
                          priceRoot.TryGetProperty("name",  out var n) ? n : default;
            var name = titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() ?? query : query;

            var brandEl = priceRoot.TryGetProperty("brand", out var b) ? b : default;
            var brand = brandEl.ValueKind == JsonValueKind.String ? brandEl.GetString() ?? "" : "";

            bool isHuisMerk = IsHuisMerk(brand, name);

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
                IsBiologisch    = name.Contains("biologisch", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains(" bio ", StringComparison.OrdinalIgnoreCase),
                IsVegan         = name.Contains("vegan", StringComparison.OrdinalIgnoreCase),
                IsHuisMerk      = isHuisMerk,
                IsAMerk         = !isHuisMerk,
                LastUpdated     = DateTime.UtcNow,
            });

            if (results.Count >= 3) break;
        }

        if (results.Count > 0)
            _logger.LogInformation("Jumbo {Source}: {Count} resultaten voor '{Product}'",
                source, results.Count, query);

        return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
    }

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
