using System.Text.Json;
using SmartShopper.API.Models;
using Microsoft.Playwright;

namespace SmartShopper.API.Services.Scrapers;

// ─────────────────────────────────────────────────────────────────────
//  PlaywrightPool — gedeelde Chromium instantie (1 per proces)
// ─────────────────────────────────────────────────────────────────────
public static class PlaywrightPool
{
    private static IPlaywright? _pw;
    private static IBrowser?    _browser;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser != null && _browser.IsConnected) return _browser;

        await _lock.WaitAsync();
        try
        {
            if (_browser != null && _browser.IsConnected) return _browser;

            _pw      = await Playwright.CreateAsync();
            _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args     = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--no-zygote",
                    "--disable-extensions",
                    "--disable-background-networking",
                    "--disable-default-apps",
                    "--disable-sync",
                    "--disable-translate",
                    "--hide-scrollbars",
                    "--metrics-recording-only",
                    "--mute-audio",
                    "--no-first-run",
                    "--safebrowsing-disable-auto-update",
                }
            });
            return _browser;
        }
        finally { _lock.Release(); }
    }

    public static async Task<IBrowserContext> NewContextAsync(string locale = "nl-NL")
    {
        var browser = await GetBrowserAsync();
        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/124.0.0.0 Safari/537.36",
            Locale       = locale,
            TimezoneId   = "Europe/Amsterdam",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = $"{locale},{locale.Split('-')[0]};q=0.9,en;q=0.8"
            }
        });
    }
}

// ─────────────────────────────────────────────────────────────────────
//  Open Food Facts helper — productnaam + labels, GEEN prijs
//  Prijs wordt daarna via EstimatePriceByName geschat in CompareService
// ─────────────────────────────────────────────────────────────────────
public static class OFFHelper
{
    public static async Task<ProductMatch?> SearchAsync(
        string query, string storeName, string country,
        HttpClient http, ILogger logger)
    {
        try
        {
            string lang = country == "DE" ? "de" : "nl";
            var url = $"https://world.openfoodfacts.org/cgi/search.pl" +
                      $"?search_terms={Uri.EscapeDataString(query)}&search_simple=1" +
                      $"&action=process&json=1&page_size=5&lc={lang}&cc={country.ToLower()}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "SmartShopper/3.0 (contact@smartshopper.nl)");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var response  = await http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("products", out var products)) return null;

            foreach (var p in products.EnumerateArray())
            {
                var name = p.TryGetProperty("product_name_nl", out var nl) && !string.IsNullOrEmpty(nl.GetString())
                    ? nl.GetString()!
                    : p.TryGetProperty("product_name_en", out var en) && !string.IsNullOrEmpty(en.GetString())
                        ? en.GetString()!
                        : p.TryGetProperty("product_name", out var pn) ? pn.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(name)) continue;

                bool isBio = name.Contains("bio", StringComparison.OrdinalIgnoreCase) ||
                             (p.TryGetProperty("labels_tags", out var lbls) &&
                              lbls.EnumerateArray().Any(l => l.GetString()?.Contains("organic") == true));
                bool isVegan = p.TryGetProperty("labels_tags", out var lbt) &&
                               lbt.EnumerateArray().Any(l => l.GetString()?.Contains("vegan") == true);

                logger.LogDebug("OFF match voor '{Query}': {Name}", query, name);
                return new ProductMatch
                {
                    StoreName       = storeName,
                    Country         = country,
                    ProductName     = name,
                    Price           = 0,        // caller vult prijs via schatting
                    IsEstimated     = true,
                    IsBiologisch    = isBio,
                    IsVegan         = isVegan,
                    MatchConfidence = 0.55,
                    LastUpdated     = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "OFF fallback mislukt voor '{Query}'", query);
        }
        return null;
    }
}

// ─────────────────────────────────────────────────────────────────────
//  ALBERT HEIJN SCRAPER
//  Volgorde: 1) Mobiele API  2) Playwright HTML  3) leeg (→ OFF in CompareService)
// ─────────────────────────────────────────────────────────────────────
public class AlbertHeijnScraper
{
    private readonly HttpClient                  _http;
    private readonly ILogger<AlbertHeijnScraper> _logger;

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
        // 1️⃣ Mobiele API (werkt soms nog)
        var results = await TryMobileApi(item, bearerToken);
        if (results.Count > 0) return results;

        // 2️⃣ Playwright
        results = await TryPlaywright(item);
        if (results.Count > 0) return results;

        // 3️⃣ Leeg teruggeven → CompareService roept EstimateViaOFF aan
        _logger.LogWarning("AH: alle methoden mislukt voor '{Product}'", item.Name);
        return [];
    }

    // ─── 1. Mobiele API ──────────────────────────────────────────
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
            req.Headers.TryAddWithoutValidation("User-Agent",    "Appie/8.22.3 Model/phone Android/14");
            req.Headers.TryAddWithoutValidation("x-clientid",    "appie");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response  = await _http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH Mobile API: {Status} voor '{Product}'", response.StatusCode, item.Name);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var products)) return [];

            var results = new List<ProductMatch>();
            foreach (var p in products.EnumerateArray())
            {
                var match = ParseMobileProduct(p, userToken != null);
                if (match != null) results.Add(match);
                if (results.Count >= 3) break;
            }

            _logger.LogInformation("AH Mobile: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH Mobile mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    // ─── 2. Playwright ───────────────────────────────────────────
    private async Task<List<ProductMatch>> TryPlaywright(GroceryItem item)
    {
        IBrowserContext? ctx  = null;
        IPage?           page = null;
        try
        {
            ctx  = await PlaywrightPool.NewContextAsync();
            page = await ctx.NewPageAsync();

            // Blokkeer zware resources
            await page.RouteAsync("**/*.{png,jpg,jpeg,gif,webp,svg,woff,woff2,ttf}", r => r.AbortAsync());
            await page.RouteAsync("**/analytics**",   r => r.AbortAsync());
            await page.RouteAsync("**/doubleclick**", r => r.AbortAsync());
            await page.RouteAsync("**/googletag**",   r => r.AbortAsync());

            var url = $"https://www.ah.nl/zoeken?query={Uri.EscapeDataString(item.Name)}";
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 20_000
            });

            // Cookie banner wegklikken
            try
            {
                var btn = page.Locator("[id*='accept'], button:has-text('Accepteer'), button:has-text('Akkoord')");
                if (await btn.CountAsync() > 0)
                    await btn.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            }
            catch { /* geen banner */ }

            // Wacht tot de pagina geladen is
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 12_000 });

            // Extract Apollo SSR JSON
            var products = await page.EvaluateAsync<List<AhProductDto>?>(@"
                () => {
                    try {
                        const scripts = [...document.querySelectorAll('script')];
                        for (const s of scripts) {
                            const txt = s.textContent || '';
                            if (!txt.includes('searchProducts')) continue;
                            // ApolloSSRDataTransport pattern
                            const m = txt.match(/\.push\((\{""rehydrate"":.*\})\)/s)
                                   || txt.match(/\((\{""rehydrate"":.*\})\)/s)
                                   || txt.match(/(\{""rehydrate"":.*\})/s);
                            if (!m) continue;
                            const data = JSON.parse(m[1]);
                            const key  = Object.keys(data.rehydrate)[0];
                            const prods = data?.rehydrate?.[key]?.data?.searchProducts?.products;
                            if (!prods || !prods.length) continue;
                            return prods.slice(0, 5).map(p => ({
                                title:    p.title || '',
                                priceNow: p.price?.now || 0,
                                priceWas: p.price?.was || 0,
                                isBonus:  !!(p.price?.was),
                                brand:    p.brand?.name || '',
                                isBio:    (p.title||'').toLowerCase().includes('biologisch') ||
                                          (p.title||'').toLowerCase().includes(' bio '),
                                isVegan:  (p.title||'').toLowerCase().includes('vegan')
                            }));
                        }
                        return null;
                    } catch(e) { return null; }
                }
            ");

            if (products == null || products.Count == 0)
            {
                _logger.LogWarning("AH Playwright: geen producten gevonden voor '{Product}'", item.Name);
                return [];
            }

            var results = products
                .Where(p => p.PriceNow > 0 && !string.IsNullOrEmpty(p.Title))
                .Select(p => new ProductMatch
                {
                    StoreName       = "Albert Heijn",
                    Country         = "NL",
                    ProductName     = p.Title,
                    Price           = p.PriceNow,
                    NormalPrice     = p.PriceWas > 0 ? p.PriceWas : p.PriceNow,
                    IsPromo         = p.IsBonus,
                    PromoText       = p.IsBonus ? "Bonus" : "",
                    IsEstimated     = false,
                    IsBiologisch    = p.IsBio,
                    IsVegan         = p.IsVegan,
                    IsHuisMerk      = string.IsNullOrEmpty(p.Brand) || p.Brand.Equals("AH", StringComparison.OrdinalIgnoreCase),
                    IsAMerk         = !string.IsNullOrEmpty(p.Brand) && !p.Brand.Equals("AH", StringComparison.OrdinalIgnoreCase),
                    MatchConfidence = WordScore(item.Name, p.Title),
                    LastUpdated     = DateTime.UtcNow
                })
                .OrderByDescending(r => r.MatchConfidence)
                .Take(1)
                .ToList();

            _logger.LogInformation("AH Playwright: {Count} resultaten voor '{Product}'", results.Count, item.Name);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AH Playwright mislukt voor '{Product}'", item.Name);
            return [];
        }
        finally
        {
            if (page != null) try { await page.CloseAsync(); } catch { }
            if (ctx  != null) try { await ctx.CloseAsync();  } catch { }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────
    private ProductMatch? ParseMobileProduct(JsonElement p, bool isBonuskaart)
    {
        var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        decimal price = 0, normalPrice = 0;
        bool isPromo = false; string promoText = "";

        if (p.TryGetProperty("price", out var priceEl))
        {
            if (isBonuskaart && priceEl.TryGetProperty("bonusPrice", out var bp))
            {
                price = bp.GetDecimal();
                normalPrice = priceEl.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : price;
                isPromo = true; promoText = "Bonuskaartprijs";
            }
            else
            {
                price = priceEl.TryGetProperty("now",       out var now) ? now.GetDecimal() :
                        priceEl.TryGetProperty("unitPrice", out var up2) ? up2.GetDecimal() : 0;
                normalPrice = price;
            }
            if (p.TryGetProperty("discount", out var disc))
            {
                isPromo = true;
                promoText = disc.TryGetProperty("label", out var l) ? l.GetString() ?? "Bonus" : "Bonus";
                if (priceEl.TryGetProperty("was", out var was)) normalPrice = was.GetDecimal();
            }
        }
        if (price <= 0) return null;

        var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";
        return new ProductMatch
        {
            StoreName       = "Albert Heijn", Country = "NL",
            ProductName     = title, Price = price, NormalPrice = normalPrice,
            IsPromo         = isPromo, PromoText = promoText, IsEstimated = false,
            IsBiologisch    = title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
            IsVegan         = title.Contains("vegan",      StringComparison.OrdinalIgnoreCase),
            IsHuisMerk      = string.IsNullOrEmpty(brand) || brand.Equals("AH", StringComparison.OrdinalIgnoreCase),
            IsAMerk         = !string.IsNullOrEmpty(brand) && !brand.Equals("AH", StringComparison.OrdinalIgnoreCase),
            MatchConfidence = 0.9, LastUpdated = DateTime.UtcNow
        };
    }

    private static double WordScore(string query, string product)
    {
        var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0.5;
        var lower = product.ToLower();
        return (double)words.Count(w => lower.Contains(w)) / words.Length;
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

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.ah.nl/mobile-auth/v1/auth/token/anonymous");
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
                clientId  = "appie", username = email,
                password  = password, grantType = "password"
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

    private record AhProductDto
    {
        public string  Title    { get; init; } = "";
        public decimal PriceNow { get; init; }
        public decimal PriceWas { get; init; }
        public bool    IsBonus  { get; init; }
        public string  Brand    { get; init; } = "";
        public bool    IsBio    { get; init; }
        public bool    IsVegan  { get; init; }
    }
}

// ─── ProxyConfig (ongewijzigd) ────────────────────────────────────
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
                    Proxy    = new System.Net.WebProxy(proxyUrl, true),
                    UseProxy = true,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                logger.LogInformation("Proxy actief voor {Env}: {Proxy}", envVarName, proxyUrl);
                return new HttpClient(handler);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Proxy config mislukt voor {Env}", envVarName); }
        }
        return new HttpClient();
    }
}
