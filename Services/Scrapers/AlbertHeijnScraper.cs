using System.Net.Http.Json;
using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

public class AlbertHeijnScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<AlbertHeijnScraper> _logger;

    // AH Mobile OAuth endpoint voor bonuskaartprijzen
    private const string AH_TOKEN_URL = "https://api.ah.nl/mobile-auth/v1/auth/token/anonymous";
    private const string AH_SEARCH_URL = "https://api.ah.nl/mobile-services/product/search/v2";
    private const string AH_WEB_SEARCH_URL = "https://www.ah.nl/zoeken/api/v1/search";

    private static string? _cachedAnonToken;
    private static DateTime _tokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AlbertHeijnScraper(HttpClient http, ILogger<AlbertHeijnScraper> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Appie/8.22.3 Model/phone Android/14");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "x-application", "AHWEBSHOP");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "x-clientid", "appie");
    }

    // ─── Publieke zoekopdracht (geen login nodig) ─────────────────
    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string? bearerToken = null)
    {
        // Probeer eerst de mobiele API (geeft betere data incl. bonusprijzen)
        var results = await TryMobileApi(item, bearerToken);
        if (results.Count > 0) return results;

        // Fallback: web API
        return await TryWebApi(item);
    }

    // ─── AH Mobiele API (geeft bonuskaartprijzen als ingelogd) ────
    private async Task<List<ProductMatch>> TryMobileApi(GroceryItem item, string? userToken)
    {
        try
        {
            string token = userToken ?? await GetAnonTokenAsync();
            if (string.IsNullOrEmpty(token)) return [];

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{AH_SEARCH_URL}?query={Uri.EscapeDataString(item.Name)}&size=5&sortBy=RELEVANCE");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.TryAddWithoutValidation("x-application", "appie");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH Mobile API: {Status} voor {Product}", response.StatusCode, item.Name);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var match = ParseAhMobileProduct(p, userToken != null);
                if (match != null) results.Add(match);
                if (results.Count >= 3) break;
            }

            _logger.LogInformation("AH Mobile: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH Mobile API mislukt voor {Product}", item.Name);
            return [];
        }
    }

    private ProductMatch? ParseAhMobileProduct(JsonElement p, bool isBonuskaart)
    {
        var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        decimal price = 0;
        decimal normalPrice = 0;
        bool isPromo = false;
        string promoText = "";

        if (p.TryGetProperty("price", out var priceEl))
        {
            // Bonuskaartprijs (ingelogd) heeft andere structuur
            if (isBonuskaart && priceEl.TryGetProperty("bonusPrice", out var bp))
            {
                price = bp.GetDecimal();
                normalPrice = priceEl.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : price;
                isPromo = true;
                promoText = "Bonuskaartprijs";
            }
            else
            {
                price = priceEl.TryGetProperty("now", out var now) ? now.GetDecimal() :
                        priceEl.TryGetProperty("unitPrice", out var up2) ? up2.GetDecimal() : 0;
                normalPrice = price;
            }

            if (p.TryGetProperty("discount", out var disc))
            {
                isPromo = true;
                promoText = disc.TryGetProperty("label", out var l) ? l.GetString() ?? "Bonus" : "Bonus";
                if (priceEl.TryGetProperty("was", out var was))
                    normalPrice = was.GetDecimal();
            }
        }

        if (price <= 0) return null;

        var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";
        bool isHuismerk = string.IsNullOrEmpty(brand) || brand.Equals("AH", StringComparison.OrdinalIgnoreCase)
                          || title.StartsWith("AH ", StringComparison.OrdinalIgnoreCase);

        return new ProductMatch
        {
            StoreName = "Albert Heijn",
            Country = "NL",
            ProductName = title,
            Price = price,
            NormalPrice = normalPrice,
            IsPromo = isPromo,
            PromoText = promoText,
            IsEstimated = false,
            IsBiologisch = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase) ||
                           title.Contains(" bio ", StringComparison.OrdinalIgnoreCase),
            IsVegan = title.Contains("vegan", StringComparison.OrdinalIgnoreCase),
            IsHuisMerk = isHuismerk,
            IsAMerk = !isHuismerk,
            MatchConfidence = 0.9,
            LastUpdated = DateTime.UtcNow
        };
    }

    // ─── Web API fallback ────────────────────────────────────────
    private async Task<List<ProductMatch>> TryWebApi(GroceryItem item)
    {
        try
        {
            var url = $"{AH_WEB_SEARCH_URL}?query={Uri.EscapeDataString(item.Name)}&size=5";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH Web API: {Status} voor {Product}", resp.StatusCode, item.Name);
                return [];
            }
            var response = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(response);

            if (!doc.RootElement.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(title)) continue;

                decimal price = 0;
                decimal normalPrice = 0;
                bool isPromo = false;
                string promoText = "";

                if (p.TryGetProperty("price", out var priceProp))
                {
                    price = priceProp.TryGetProperty("now", out var now) ? now.GetDecimal() : 0;
                    normalPrice = price;
                    if (priceProp.TryGetProperty("was", out var was))
                    {
                        normalPrice = was.GetDecimal();
                        isPromo = true;
                    }
                    if (p.TryGetProperty("discount", out var disc))
                    {
                        isPromo = true;
                        promoText = disc.TryGetProperty("label", out var l) ? l.GetString() ?? "Bonus" : "Bonus";
                    }
                }

                if (price <= 0) continue;

                var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";
                results.Add(new ProductMatch
                {
                    StoreName = "Albert Heijn",
                    Country = "NL",
                    ProductName = title,
                    Price = price,
                    NormalPrice = normalPrice,
                    IsPromo = isPromo,
                    PromoText = promoText,
                    IsEstimated = false,
                    IsBiologisch = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                    IsVegan = title.Contains("vegan", StringComparison.OrdinalIgnoreCase),
                    IsHuisMerk = string.IsNullOrEmpty(brand) || brand == "AH",
                    IsAMerk = !string.IsNullOrEmpty(brand) && brand != "AH",
                    MatchConfidence = 0.85,
                    LastUpdated = DateTime.UtcNow
                });

                if (results.Count >= 3) break;
            }

            _logger.LogInformation("AH Web: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results.Take(1).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AH Web API mislukt voor {Product}", item.Name);
            return [];
        }
    }

    // ─── Anoniem OAuth token ophalen (voor mobiele API zonder login) ─
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
            _tokenExpiry = DateTime.UtcNow.AddMinutes(55); // tokens zijn 1 uur geldig

            _logger.LogDebug("AH anon token vernieuwd");
            return _cachedAnonToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH token ophalen mislukt");
            return "";
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ─── Ingelogd token ophalen via gebruikersaccount ─────────────
    public async Task<string?> GetUserTokenAsync(string email, string password)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.ah.nl/mobile-auth/v1/auth/token");
            req.Content = JsonContent.Create(new
            {
                clientId = "appie",
                username = email,
                password = password,
                grantType = "password"
            });
            req.Headers.TryAddWithoutValidation("x-application", "appie");

            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH login mislukt voor {Email}: {Status}", email, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH gebruiker login mislukt");
            return null;
        }
    }
}

// ─── Proxy configuratie (per-scraper, activeer via PROXY_URL_AH env var) ──────
// Zet in Railway: PROXY_URL_AH=http://user:pass@proxy.brightdata.com:22225
// Als leeg → directe verbinding (huidig gedrag)
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
                logger.LogInformation("Proxy actief voor {Env}: {Proxy}", envVarName, proxyUrl);
                return new HttpClient(handler);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Proxy config mislukt voor {Env}, directe verbinding", envVarName);
            }
        }
        return new HttpClient();
    }
}
