using System.Text.Json;
using System.Globalization;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

// ─────────────────────────────────────────────────────────────────────
//  JUMBO SCRAPER — geen Playwright
//  1) REST search-page API (officieel, geen auth nodig)
//  2) Algolia API (fallback)
//  3) Leeg → CompareService gebruikt schatting
// ─────────────────────────────────────────────────────────────────────
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
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                          "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",         "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer",         "https://www.jumbo.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin",          "https://www.jumbo.com");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest",  "empty");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode",  "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site",  "same-origin");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        var results = await TrySearchPageApi(item);
        if (results.Count > 0) return results;

        results = await TryAlgoliaApi(item);
        if (results.Count > 0) return results;

        _logger.LogWarning("Jumbo: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    // ─── 1. search-page REST API ─────────────────────────────────────
    private async Task<List<ProductMatch>> TrySearchPageApi(GroceryItem item)
    {
        try
        {
            var url = $"https://www.jumbo.com/api/search-page/v1" +
                      $"?searchPhrase={Uri.EscapeDataString(item.Name)}&pageSize=5&currentPage=0";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response  = await _http.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jumbo SearchPage: {Status} voor '{Product}'", response.StatusCode, item.Name);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Response structuur: { searchResult: { products: { items: [...] } } }
            JsonElement items = default;
            if (doc.RootElement.TryGetProperty("searchResult", out var sr) &&
                sr.TryGetProperty("products",    out var prod) &&
                prod.TryGetProperty("items",     out items))
            { /* gevonden */ }
            else if (doc.RootElement.TryGetProperty("products", out var p2) &&
                     p2.TryGetProperty("items", out items))
            { /* alternatieve structuur */ }
            else if (doc.RootElement.TryGetProperty("items", out items))
            { /* plat */ }
            else
            {
                _logger.LogWarning("Jumbo SearchPage: onbekende JSON structuur voor '{Product}'", item.Name);
                return [];
            }

            var results = ParseItems(items, item.Name);
            if (results.Count > 0)
                _logger.LogInformation("Jumbo SearchPage: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results;
        }
        catch (TaskCanceledException) { _logger.LogWarning("Jumbo SearchPage: timeout voor '{Product}'", item.Name); return []; }
        catch (Exception ex)          { _logger.LogWarning(ex, "Jumbo SearchPage mislukt voor '{Product}'", item.Name); return []; }
    }

    // ─── 2. Algolia API ──────────────────────────────────────────────
    private async Task<List<ProductMatch>> TryAlgoliaApi(GroceryItem item)
    {
        try
        {
            // Jumbo gebruikt Algolia voor zoeken — publieke app credentials
            const string algoliaUrl = "https://24jcap3n3f-dsn.algolia.net/1/indexes/JUMBO_products_NL_nl/query";

            var body = new { query = item.Name, hitsPerPage = 5, attributesToRetrieve = new[] { "title", "price", "brand", "normalPrice" } };

            using var req = new HttpRequestMessage(HttpMethod.Post, algoliaUrl);
            req.Content = JsonContent.Create(body);
            req.Headers.TryAddWithoutValidation("X-Algolia-Application-Id", "24JCAP3N3F");
            req.Headers.TryAddWithoutValidation("X-Algolia-API-Key",        "fd5ca40d43e8e40c3d64535e25c7ab51");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var response  = await _http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hits", out var hits)) return [];

            var results = new List<ProductMatch>();
            foreach (var hit in hits.EnumerateArray())
            {
                var match = ParseAlgoliaHit(hit, item.Name);
                if (match != null) results.Add(match);
                if (results.Count >= 1) break;
            }

            if (results.Count > 0)
                _logger.LogInformation("Jumbo Algolia: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results;
        }
        catch (TaskCanceledException) { _logger.LogWarning("Jumbo Algolia: timeout voor '{Product}'", item.Name); return []; }
        catch (Exception ex)          { _logger.LogWarning(ex, "Jumbo Algolia mislukt voor '{Product}'", item.Name); return []; }
    }

    // ─── Parsers ─────────────────────────────────────────────────────
    private List<ProductMatch> ParseItems(JsonElement items, string query)
    {
        var results = new List<ProductMatch>();
        foreach (var item in items.EnumerateArray())
        {
            // Prijs zit in: item.product.prices.price.amount (in centen) OF item.prices.price.amount
            var productEl = item.TryGetProperty("product", out var p) ? p : item;

            var title = productEl.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(title)) continue;

            decimal price = 0, normalPrice = 0;
            bool isPromo = false; string promoText = "";

            // Zoek prijs recursief in geneste structuur
            price = FindJumboPrice(productEl, out normalPrice, out isPromo, out promoText);
            if (price <= 0) continue;

            var brand = productEl.TryGetProperty("brand", out var b) ? b.GetString() ?? "" :
                        productEl.TryGetProperty("brandName", out var bn) ? bn.GetString() ?? "" : "";
            bool isHuisMerk = string.IsNullOrEmpty(brand) ||
                              HuisMerkPrefixes.Any(p => title.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lower = title.ToLower();
            double score = words.Length == 0 ? 0.5 : (double)words.Count(w => lower.Contains(w)) / words.Length;

            results.Add(new ProductMatch
            {
                StoreName       = "Jumbo", Country = "NL",
                ProductName     = title, Price = price,
                NormalPrice     = normalPrice > 0 ? normalPrice : price,
                IsPromo         = isPromo, PromoText = promoText, IsEstimated = false,
                IsBiologisch    = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                IsVegan         = title.Contains("vegan",      StringComparison.OrdinalIgnoreCase),
                IsHuisMerk      = isHuisMerk, IsAMerk = !isHuisMerk,
                MatchConfidence = score, LastUpdated = DateTime.UtcNow
            });
            if (results.Count >= 1) break;
        }
        return results;
    }

    private static decimal FindJumboPrice(JsonElement el, out decimal normalPrice,
        out bool isPromo, out string promoText)
    {
        normalPrice = 0; isPromo = false; promoText = "";

        // prices.price.amount (in centen)
        if (el.TryGetProperty("prices", out var prices))
        {
            if (prices.TryGetProperty("price", out var priceObj))
            {
                decimal amount = priceObj.TryGetProperty("amount", out var a) ? a.GetDecimal() / 100m : 0;
                if (amount > 0)
                {
                    if (prices.TryGetProperty("promotionalPrice", out var promo))
                    {
                        decimal promoAmount = promo.TryGetProperty("amount", out var pa) ? pa.GetDecimal() / 100m : 0;
                        if (promoAmount > 0 && promoAmount < amount)
                        {
                            normalPrice = amount; isPromo = true;
                            promoText   = "Aanbieding";
                            return promoAmount;
                        }
                    }
                    normalPrice = amount;
                    return amount;
                }
            }
        }

        // Direct price fields
        foreach (var key in new[] { "price", "currentPrice", "salePrice" })
        {
            if (!el.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) { normalPrice = v.GetDecimal(); return v.GetDecimal(); }
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()?.Replace(",", ".").Replace("€", "").Trim();
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d) && d > 0)
                { normalPrice = d; return d; }
            }
        }
        return 0;
    }

    private ProductMatch? ParseAlgoliaHit(JsonElement hit, string query)
    {
        var title = hit.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        decimal price = 0;
        if (hit.TryGetProperty("price", out var pr))
        {
            if (pr.ValueKind == JsonValueKind.Number) price = pr.GetDecimal();
            else if (pr.ValueKind == JsonValueKind.String)
            {
                var s = pr.GetString()?.Replace(",", ".") ?? "";
                decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
            }
        }
        if (price <= 0) return null;

        var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lower = title.ToLower();
        double score = words.Length == 0 ? 0.5 : (double)words.Count(w => lower.Contains(w)) / words.Length;

        return new ProductMatch
        {
            StoreName       = "Jumbo", Country = "NL",
            ProductName     = title, Price = price, NormalPrice = price,
            IsPromo         = false, IsEstimated = false,
            IsBiologisch    = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
            MatchConfidence = score, LastUpdated = DateTime.UtcNow
        };
    }
}
