using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

/// <summary>
/// AH scraper via HTML-parsing van Apollo SSR data.
/// De AH GQL API en Mobile API blokkeren datacenter-IPs (Railway).
/// De website (www.ah.nl/zoeken?query=...) servert Apollo SSR data in een script-tag,
/// die altijd beschikbaar is zonder API-sleutels of IP-whitelisting.
/// Echte prijs zit in: priceV2.now.amount (niet price.now)
/// </summary>
public class AlbertHeijnScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<AlbertHeijnScraper> _logger;

    private const string AH_SEARCH_URL = "https://www.ah.nl/zoeken?query={0}&page=1";
    private const string AH_TOKEN_URL  = "https://api.ah.nl/mobile-auth/v1/auth/token/anonymous";

    private static string?  _cachedAnonToken;
    private static DateTime _tokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AlbertHeijnScraper(HttpClient http, ILogger<AlbertHeijnScraper> logger)
    {
        _http   = http;
        _logger = logger;

        // Browser-headers zodat www.ah.nl niet blokkeert
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.ah.nl/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "document");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string? bearerToken = null)
    {
        // 1. HTML scrapen via Apollo SSR (altijd beschikbaar)
        var results = await TryHtmlScrape(item);
        if (results.Count > 0) return results;

        // 2. Mobile API fallback
        results = await TryMobileApi(item, bearerToken);
        if (results.Count > 0) return results;

        _logger.LogWarning("AH: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    // ─── Methode 1: HTML-pagina scrapen + Apollo SSR data ─────────
    private async Task<List<ProductMatch>> TryHtmlScrape(GroceryItem item)
    {
        try
        {
            var url = string.Format(AH_SEARCH_URL, Uri.EscapeDataString(item.Name));
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH HTML: HTTP {Status} voor '{Product}'", response.StatusCode, item.Name);
                return [];
            }

            var html = await response.Content.ReadAsStringAsync();
            var products = ParseApolloSsr(html, item.Name);

            _logger.LogInformation("AH HTML: {Count} resultaten voor '{Product}'", products.Count, item.Name);
            return products;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH HTML scrape mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    // ─── Apollo SSR parser ─────────────────────────────────────────
    // AH injecteert data als:
    //   (window[Symbol.for("ApolloSSRDataTransport")] ??= []).push({"rehydrate":{...}})
    // De JSON bevat searchProducts -> products[] met priceV2.now.amount
    private List<ProductMatch> ParseApolloSsr(string html, string query)
    {
        var results = new List<ProductMatch>();

        var regex = new Regex(
            @"\(window\[Symbol\.for\(""ApolloSSRDataTransport""\)\][^)]*\)\.push\((\{.+?\})\);",
            RegexOptions.Singleline);

        foreach (Match m in regex.Matches(html))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var root = doc.RootElement;

                if (!root.TryGetProperty("rehydrate", out var rehydrate)) continue;

                foreach (var cacheEntry in rehydrate.EnumerateObject())
                {
                    if (!cacheEntry.Value.TryGetProperty("data", out var data)) continue;
                    if (!data.TryGetProperty("searchProducts", out var sp)) continue;
                    if (!sp.TryGetProperty("products", out var productsEl)) continue;

                    foreach (var p in productsEl.EnumerateArray())
                    {
                        var match = ParseProduct(p, query);
                        if (match != null) results.Add(match);
                    }

                    if (results.Count > 0)
                    {
                        return results
                            .OrderByDescending(r => r.MatchConfidence)
                            .ThenBy(r => r.Price)
                            .Take(1)
                            .ToList();
                    }
                }
            }
            catch (JsonException) { /* ongeldige JSON, volgende proberen */ }
        }

        return [];
    }

    private ProductMatch? ParseProduct(JsonElement p, string query)
    {
        var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        decimal price = 0, normalPrice = 0;
        bool isPromo = false;
        string promoText = "";

        // priceV2 is het echte schema (price.now bestaat niet meer in huidige AH API)
        if (p.TryGetProperty("priceV2", out var pv2))
        {
            if (pv2.TryGetProperty("now", out var nowEl) &&
                nowEl.TryGetProperty("amount", out var amount))
                price = amount.GetDecimal();

            if (pv2.TryGetProperty("was", out var wasEl) &&
                wasEl.TryGetProperty("amount", out var wasAmt))
                normalPrice = wasAmt.GetDecimal();

            if (pv2.TryGetProperty("promotionLabels", out var labels) &&
                labels.ValueKind == JsonValueKind.Array &&
                labels.GetArrayLength() > 0)
            {
                isPromo   = true;
                var first = labels[0];
                promoText = first.TryGetProperty("text", out var lt)
                    ? lt.GetString() ?? "Bonus" : "Bonus";
            }

            if (pv2.TryGetProperty("discount", out var disc) &&
                disc.ValueKind != JsonValueKind.Null)
                isPromo = true;
        }
        // Fallback oud schema (voor mobile API responses)
        else if (p.TryGetProperty("price", out var pr))
        {
            if (pr.TryGetProperty("now", out var now))
                price = now.ValueKind == JsonValueKind.Number
                    ? now.GetDecimal()
                    : now.TryGetProperty("amount", out var a) ? a.GetDecimal() : 0;
            if (pr.TryGetProperty("was", out var was))
                normalPrice = was.ValueKind == JsonValueKind.Number
                    ? was.GetDecimal()
                    : was.TryGetProperty("amount", out var wa) ? wa.GetDecimal() : 0;
        }

        if (price <= 0) return null;
        if (normalPrice <= 0) normalPrice = price;
        if (normalPrice > price) isPromo = true;

        var brand = p.TryGetProperty("brand", out var br) ? br.GetString() ?? "" : "";
        bool isHuismerk = string.IsNullOrEmpty(brand) ||
                          brand.Equals("AH", StringComparison.OrdinalIgnoreCase) ||
                          title.StartsWith("AH ", StringComparison.OrdinalIgnoreCase);

        return new ProductMatch
        {
            StoreName       = "Albert Heijn",
            Country         = "NL",
            ProductName     = title,
            Price           = price,
            NormalPrice     = normalPrice,
            IsPromo         = isPromo,
            PromoText       = promoText,
            IsEstimated     = false,
            IsBiologisch    = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
            IsVegan         = title.Contains("vegan", StringComparison.OrdinalIgnoreCase),
            IsHuisMerk      = isHuismerk,
            IsAMerk         = !isHuismerk,
            MatchConfidence = ProductMatcher.Score(query, title),
            LastUpdated     = DateTime.UtcNow,
        };
    }

    // ─── Methode 2: Mobile API fallback ───────────────────────────
    private async Task<List<ProductMatch>> TryMobileApi(GroceryItem item, string? userToken)
    {
        try
        {
            string token = userToken ?? await GetAnonTokenAsync();
            if (string.IsNullOrEmpty(token)) return [];

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.ah.nl/mobile-services/product/search/v2" +
                $"?query={Uri.EscapeDataString(item.Name)}&size=5&sortBy=RELEVANCE");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.TryAddWithoutValidation("x-application", "appie-android");
            req.Headers.TryAddWithoutValidation("User-Agent",    "Appie/8.22.3 (Android)");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var match = ParseProduct(p, item.Name);
                if (match != null) results.Add(match);
            }

            return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH Mobile API mislukt voor '{Product}'", item.Name);
            return [];
        }
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
            using var req = new HttpRequestMessage(HttpMethod.Post, AH_TOKEN_URL);
            req.Content = JsonContent.Create(new { clientId = "appie-android" });
            req.Headers.TryAddWithoutValidation("x-application", "appie");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return "";
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            _cachedAnonToken = doc.RootElement.TryGetProperty("access_token", out var at)
                ? at.GetString() ?? "" : "";
            _tokenExpiry = DateTime.UtcNow.AddMinutes(55);
            return _cachedAnonToken;
        }
        catch { return ""; }
        finally { _tokenLock.Release(); }
    }

    public async Task<string?> GetUserTokenAsync(string email, string password)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.ah.nl/mobile-auth/v1/auth/token");
            req.Content = JsonContent.Create(new
            {
                clientId  = "appie-android",
                username  = email,
                password  = password,
                grantType = "password"
            });
            req.Headers.TryAddWithoutValidation("x-application", "appie");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        }
        catch { return null; }
    }
}

public static class ProxyConfig
{
    public static HttpClient CreateClient(string envVarName, ILogger logger)
    {
        var proxyUrl = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(proxyUrl, true),
                    UseProxy = true,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                return new HttpClient(handler);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Proxy config mislukt voor {Env}", envVarName); }
        }
        return new HttpClient();
    }
}
