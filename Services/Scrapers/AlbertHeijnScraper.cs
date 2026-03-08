using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

// ─────────────────────────────────────────────────────────────────────
//  Open Food Facts helper — productnaam + labels, GEEN prijs
// ─────────────────────────────────────────────────────────────────────
public static class OFFHelper
{
    public static async Task<string?> GetProductNameAsync(
        string query, string country, HttpClient http, ILogger logger)
    {
        try
        {
            string lang = country == "DE" ? "de" : "nl";
            var url = $"https://world.openfoodfacts.org/cgi/search.pl" +
                      $"?search_terms={Uri.EscapeDataString(query)}&search_simple=1" +
                      $"&action=process&json=1&page_size=3&lc={lang}&cc={country.ToLower()}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "SmartShopper/3.0 (contact@smartshopper.nl)");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response  = await http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products)) return null;

            foreach (var p in products.EnumerateArray())
            {
                var name = p.TryGetProperty("product_name_nl", out var nl) && !string.IsNullOrEmpty(nl.GetString()) ? nl.GetString() :
                           p.TryGetProperty("product_name_en", out var en) && !string.IsNullOrEmpty(en.GetString()) ? en.GetString() :
                           p.TryGetProperty("product_name",    out var pn) ? pn.GetString() : null;
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "OFF mislukt voor '{Query}'", query); }
        return null;
    }
}

// ─────────────────────────────────────────────────────────────────────
//  ALBERT HEIJN SCRAPER  — geen Playwright
//  1) Mobiele API (anoniem token)
//  2) Web zoek-API
//  3) Leeg → CompareService gebruikt schatting
// ─────────────────────────────────────────────────────────────────────
public class AlbertHeijnScraper
{
    private readonly HttpClient                  _http;
    private readonly ILogger<AlbertHeijnScraper> _logger;

    private const string TOKEN_URL  = "https://api.ah.nl/mobile-auth/v1/auth/token/anonymous";
    private const string SEARCH_URL = "https://api.ah.nl/mobile-services/product/search/v2";
    private const string WEB_URL    = "https://www.ah.nl/zoeken/api/v1/search";

    private static string?  _cachedAnonToken;
    private static DateTime _tokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AlbertHeijnScraper(HttpClient http, ILogger<AlbertHeijnScraper> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string? bearerToken = null)
    {
        var results = await TryMobileApi(item, bearerToken);
        if (results.Count > 0) return results;

        results = await TryWebApi(item);
        if (results.Count > 0) return results;

        _logger.LogWarning("AH: alle methoden mislukt voor '{Product}'", item.Name);
        return [];
    }

    private async Task<List<ProductMatch>> TryMobileApi(GroceryItem item, string? userToken)
    {
        try
        {
            string token = userToken ?? await GetAnonTokenAsync();
            if (string.IsNullOrEmpty(token)) return [];

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{SEARCH_URL}?query={Uri.EscapeDataString(item.Name)}&size=5&sortBy=RELEVANCE");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.TryAddWithoutValidation("x-application", "appie");
            req.Headers.TryAddWithoutValidation("x-clientid",    "appie");
            req.Headers.TryAddWithoutValidation("User-Agent",    "Appie/8.22.3 Model/phone Android/14");
            req.Headers.TryAddWithoutValidation("Accept",        "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response  = await _http.SendAsync(req, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH Mobile: {Status} voor '{Product}'", response.StatusCode, item.Name);
                if (userToken == null && response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    _cachedAnonToken = null;
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var match = ParseProduct(p, item.Name, bonuskaart: userToken != null);
                if (match != null) results.Add(match);
                if (results.Count >= 3) break;
            }
            if (results.Count == 0) return [];

            _logger.LogInformation("AH Mobile: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return [results.OrderByDescending(r => r.MatchConfidence).First()];
        }
        catch (TaskCanceledException) { _logger.LogWarning("AH Mobile: timeout voor '{Product}'", item.Name); return []; }
        catch (Exception ex)          { _logger.LogWarning(ex, "AH Mobile mislukt voor '{Product}'", item.Name); return []; }
    }

    private async Task<List<ProductMatch>> TryWebApi(GroceryItem item)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{WEB_URL}?query={Uri.EscapeDataString(item.Name)}&size=5");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept",         "application/json");
            req.Headers.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
            req.Headers.TryAddWithoutValidation("Referer",         "https://www.ah.nl/");
            req.Headers.TryAddWithoutValidation("sec-fetch-dest",  "empty");
            req.Headers.TryAddWithoutValidation("sec-fetch-mode",  "cors");
            req.Headers.TryAddWithoutValidation("sec-fetch-site",  "same-origin");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response  = await _http.SendAsync(req, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH Web: {Status} voor '{Product}'", response.StatusCode, item.Name);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var match = ParseProduct(p, item.Name, bonuskaart: false);
                if (match != null) results.Add(match);
                if (results.Count >= 3) break;
            }
            if (results.Count == 0) return [];

            _logger.LogInformation("AH Web: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return [results.OrderByDescending(r => r.MatchConfidence).First()];
        }
        catch (TaskCanceledException) { _logger.LogWarning("AH Web: timeout voor '{Product}'", item.Name); return []; }
        catch (Exception ex)          { _logger.LogWarning(ex, "AH Web mislukt voor '{Product}'", item.Name); return []; }
    }

    private ProductMatch? ParseProduct(JsonElement p, string query, bool bonuskaart)
    {
        var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        decimal price = 0, normalPrice = 0;
        bool isPromo = false;
        string promoText = "";

        if (p.TryGetProperty("price", out var pr))
        {
            if (bonuskaart && pr.TryGetProperty("bonusPrice", out var bp))
            {
                price       = bp.GetDecimal();
                normalPrice = pr.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : price;
                isPromo     = true; promoText = "Bonuskaartprijs";
            }
            else
            {
                price       = pr.TryGetProperty("now",       out var now) ? now.GetDecimal() :
                              pr.TryGetProperty("unitPrice", out var up2) ? up2.GetDecimal() : 0;
                normalPrice = price;
                if (pr.TryGetProperty("was", out var was) && was.ValueKind == JsonValueKind.Number)
                { normalPrice = was.GetDecimal(); isPromo = true; }
            }
            if (p.TryGetProperty("discount", out var disc))
            {
                isPromo   = true;
                promoText = disc.TryGetProperty("label", out var l) ? l.GetString() ?? "Bonus" : "Bonus";
            }
        }
        if (price <= 0) return null;

        var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";
        var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lower = title.ToLower();
        double score = words.Length == 0 ? 0.5 : (double)words.Count(w => lower.Contains(w)) / words.Length;

        return new ProductMatch
        {
            StoreName       = "Albert Heijn", Country = "NL",
            ProductName     = title, Price = price,
            NormalPrice     = normalPrice > 0 ? normalPrice : price,
            IsPromo         = isPromo, PromoText = promoText, IsEstimated = false,
            IsBiologisch    = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
            IsVegan         = title.Contains("vegan",      StringComparison.OrdinalIgnoreCase),
            IsHuisMerk      = string.IsNullOrEmpty(brand) || brand.Equals("AH", StringComparison.OrdinalIgnoreCase),
            IsAMerk         = !string.IsNullOrEmpty(brand) && !brand.Equals("AH", StringComparison.OrdinalIgnoreCase),
            MatchConfidence = score, LastUpdated = DateTime.UtcNow
        };
    }

    private async Task<string> GetAnonTokenAsync()
    {
        if (!string.IsNullOrEmpty(_cachedAnonToken) && DateTime.UtcNow < _tokenExpiry)
            return _cachedAnonToken;

        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_cachedAnonToken) && DateTime.UtcNow < _tokenExpiry)
                return _cachedAnonToken;

            using var req = new HttpRequestMessage(HttpMethod.Post, TOKEN_URL);
            req.Content = JsonContent.Create(new { clientId = "appie" });
            req.Headers.TryAddWithoutValidation("x-application", "appie");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var response  = await _http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode) return "";

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            _cachedAnonToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
            _tokenExpiry = DateTime.UtcNow.AddMinutes(55);
            _logger.LogDebug("AH anoniem token vernieuwd");
            return _cachedAnonToken;
        }
        catch { return ""; }
        finally { _tokenLock.Release(); }
    }

    public async Task<string?> GetUserTokenAsync(string email, string password)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.ah.nl/mobile-auth/v1/auth/token");
            req.Content = JsonContent.Create(new { clientId = "appie", username = email, password, grantType = "password" });
            req.Headers.TryAddWithoutValidation("x-application", "appie");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response  = await _http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        }
        catch { return null; }
    }
}
