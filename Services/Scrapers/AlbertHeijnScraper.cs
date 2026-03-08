using System.Net.Http.Json;
using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

public class AlbertHeijnScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<AlbertHeijnScraper> _logger;

    private const string AH_GRAPHQL_URL = "https://api.ah.nl/graphql";
    private const string AH_TOKEN_URL   = "https://api.ah.nl/mobile-auth/v1/auth/token/anonymous";

    private static string?   _cachedAnonToken;
    private static DateTime  _tokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AlbertHeijnScraper(HttpClient http, ILogger<AlbertHeijnScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Appie/8.22.3 Model/phone Android/14");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-clientid", "appie");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string? bearerToken = null)
    {
        var results = await TryGraphQlApi(item);
        if (results.Count > 0) return results;
        results = await TryMobileApi(item, bearerToken);
        if (results.Count > 0) return results;
        _logger.LogWarning("AH: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    private async Task<List<ProductMatch>> TryGraphQlApi(GroceryItem item)
    {
        try
        {
            var query = new
            {
                operationName = "SearchProducts",
                query = "query SearchProducts($query: String!, $size: Int) { searchProducts(query: $query, size: $size) { products { title brand { name } price { now was } discount { description } } } }",
                variables = new { query = item.Name, size = 5 }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, AH_GRAPHQL_URL);
            req.Content = JsonContent.Create(query);
            req.Headers.TryAddWithoutValidation("x-application", "AHWEBSHOP");
            req.Headers.TryAddWithoutValidation("x-clientid", "appie");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH GraphQL: {Status}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return [];
            if (!data.TryGetProperty("searchProducts", out var search)) return [];
            if (!search.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(title)) continue;

                decimal price = 0, normalPrice = 0;
                bool isPromo = false;
                string promoText = "";

                if (p.TryGetProperty("price", out var priceEl))
                {
                    price = priceEl.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.Number
                        ? now.GetDecimal() : 0;
                    normalPrice = priceEl.TryGetProperty("was", out var was) && was.ValueKind == JsonValueKind.Number
                        ? was.GetDecimal() : price;
                    if (normalPrice > price && price > 0) isPromo = true;
                }

                if (price <= 0) continue;

                if (p.TryGetProperty("discount", out var disc) &&
                    disc.TryGetProperty("description", out var descEl))
                {
                    isPromo   = true;
                    promoText = descEl.GetString() ?? "Bonus";
                }

                var brand = p.TryGetProperty("brand", out var b) &&
                            b.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "";
                bool isHuismerk = string.IsNullOrEmpty(brand) ||
                                  brand.Equals("AH", StringComparison.OrdinalIgnoreCase) ||
                                  title.StartsWith("AH ", StringComparison.OrdinalIgnoreCase);

                results.Add(new ProductMatch
                {
                    StoreName   = "Albert Heijn", Country = "NL",
                    ProductName = title, Price = price,
                    NormalPrice = normalPrice > 0 ? normalPrice : price,
                    IsPromo = isPromo, PromoText = promoText,
                    IsEstimated = false,
                    IsBiologisch = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains(" bio ", StringComparison.OrdinalIgnoreCase),
                    IsVegan     = title.Contains("vegan", StringComparison.OrdinalIgnoreCase),
                    IsHuisMerk  = isHuismerk, IsAMerk = !isHuismerk,
                    MatchConfidence = 0.9, LastUpdated = DateTime.UtcNow
                });
                if (results.Count >= 3) break;
            }

            _logger.LogInformation("AH GraphQL: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH GraphQL mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

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
            req.Headers.TryAddWithoutValidation("x-application", "appie");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(title)) continue;
                decimal price = 0;
                if (p.TryGetProperty("price", out var pr))
                    price = pr.TryGetProperty("now", out var now) ? now.GetDecimal() :
                            pr.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : 0;
                if (price <= 0) continue;
                results.Add(new ProductMatch
                {
                    StoreName = "Albert Heijn", Country = "NL",
                    ProductName = title, Price = price, NormalPrice = price,
                    IsEstimated = false, MatchConfidence = 0.85, LastUpdated = DateTime.UtcNow
                });
                break;
            }

            _logger.LogInformation("AH Mobile: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH Mobile mislukt voor '{Product}'", item.Name);
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
            req.Content = JsonContent.Create(new { clientId = "appie" });
            req.Headers.TryAddWithoutValidation("x-application", "appie");
            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return "";
            var json = await response.Content.ReadAsStringAsync();
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
                clientId = "appie", username = email,
                password = password, grantType = "password"
            });
            req.Headers.TryAddWithoutValidation("x-application", "appie");
            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Proxy config mislukt voor {Env}", envVarName);
            }
        }
        return new HttpClient();
    }
}
