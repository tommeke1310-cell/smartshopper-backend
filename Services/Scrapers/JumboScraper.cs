using System.Text.Json;
using SmartShopper.API.Models;
using Microsoft.Playwright;

namespace SmartShopper.API.Services.Scrapers;

/// <summary>
/// Jumbo scraper — volgorde:
/// 1) GraphQL API (jumbo.com/api/graphql) — werkt soms nog vanaf server
/// 2) Playwright HTML scraper — altijd betrouwbaar
/// 3) Leeg → CompareService valt terug op OFF + schatting
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
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.jumbo.com/");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        // 1️⃣ GraphQL
        var results = await TryGraphQL(item);
        if (results.Count > 0) return results;

        // 2️⃣ Playwright
        results = await TryPlaywright(item);
        if (results.Count > 0) return results;

        _logger.LogWarning("Jumbo: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    // ─── 1. GraphQL ──────────────────────────────────────────────
    private async Task<List<ProductMatch>> TryGraphQL(GroceryItem item)
    {
        try
        {
            // Jumbo webshop zoek API (REST, stabieler dan GraphQL)
            var url = $"https://www.jumbo.com/api/search-page/v1?searchType=keyword" +
                      $"&searchTerms={Uri.EscapeDataString(item.Name)}&pageSize=5&currentPage=0";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.jumbo.com");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response  = await _http.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jumbo REST: {Status} voor '{Product}'", response.StatusCode, item.Name);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // REST structuur: { "products": { "items": [...] } }
            if (!doc.RootElement.TryGetProperty("products", out var productsRoot)) return [];
            if (!productsRoot.TryGetProperty("items", out var items)) return [];

            return ParseGraphQLProducts(items, item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo GraphQL mislukt voor '{Product}'", item.Name);
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

            // Intercept GraphQL response tijdens paginanavigatie
            var graphqlData = new List<JumboProductDto>();
            page.Response += async (_, resp) =>
            {
                if (!resp.Url.Contains("graphql")) return;
                try
                {
                    var body = await resp.TextAsync();
                    using var d = JsonDocument.Parse(body);
                    if (!d.RootElement.TryGetProperty("data", out var data2)) return;
                    if (!data2.TryGetProperty("searchProducts", out var sp2)) return;
                    if (!sp2.TryGetProperty("products", out var prods)) return;
                    foreach (var p in prods.EnumerateArray())
                    {
                        var dto = ParseToDto(p);
                        if (dto != null) graphqlData.Add(dto);
                    }
                }
                catch { /* parse fout, negeer */ }
            };

            var url = $"https://www.jumbo.com/producten/?searchTerms={Uri.EscapeDataString(item.Name)}";
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 20_000
            });

            // Cookie banner
            try
            {
                var btn = page.Locator("button:has-text('Accepteer'), button:has-text('Akkoord'), [id*='accept-all']");
                if (await btn.CountAsync() > 0)
                    await btn.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            }
            catch { }

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 12_000 });

            // Gebruik GraphQL data die we onderschept hebben
            if (graphqlData.Count > 0)
            {
                var results = graphqlData
                    .Where(p => p.Price > 0)
                    .Select(p => new ProductMatch
                    {
                        StoreName       = "Jumbo",
                        Country         = "NL",
                        ProductName     = p.Title,
                        Price           = p.Price,
                        IsPromo         = p.IsPromo,
                        PromoText       = p.IsPromo ? "Jumbo aanbieding" : "",
                        IsEstimated     = false,
                        IsBiologisch    = p.Title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                        IsVegan         = p.Title.Contains("vegan",      StringComparison.OrdinalIgnoreCase),
                        IsHuisMerk      = IsHuisMerk(p.Brand, p.Title),
                        IsAMerk         = !IsHuisMerk(p.Brand, p.Title),
                        MatchConfidence = WordScore(item.Name, p.Title),
                        LastUpdated     = DateTime.UtcNow
                    })
                    .OrderByDescending(r => r.MatchConfidence)
                    .Take(1)
                    .ToList();

                _logger.LogInformation("Jumbo Playwright+GraphQL: {Count} resultaten voor '{Product}'",
                    results.Count, item.Name);
                return results;
            }

            // Fallback: DOM scrapen als GraphQL niet onderschept is
            var domProducts = await page.EvaluateAsync<List<JumboProductDto>?>(@"
                () => {
                    try {
                        const cards = [...document.querySelectorAll('[data-testid=""product-card""], article[class*=""product""]')];
                        return cards.slice(0, 5).map(c => {
                            const title = c.querySelector('[class*=""title""], h2, h3')?.textContent?.trim() || '';
                            const price = c.querySelector('[class*=""price""] [class*=""now""], [data-testid*=""price""]')?.textContent?.trim() || '';
                            const num   = parseFloat(price.replace(',', '.').replace(/[^0-9.]/g, '')) || 0;
                            return { title, price: num, isPromo: false, brand: '' };
                        }).filter(p => p.price > 0 && p.title);
                    } catch(e) { return null; }
                }
            ");

            if (domProducts != null && domProducts.Count > 0)
            {
                var results = domProducts
                    .Select(p => new ProductMatch
                    {
                        StoreName       = "Jumbo",
                        Country         = "NL",
                        ProductName     = p.Title,
                        Price           = p.Price,
                        IsEstimated     = false,
                        IsBiologisch    = p.Title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                        IsVegan         = p.Title.Contains("vegan",      StringComparison.OrdinalIgnoreCase),
                        IsHuisMerk      = IsHuisMerk("", p.Title),
                        MatchConfidence = WordScore(item.Name, p.Title),
                        LastUpdated     = DateTime.UtcNow
                    })
                    .OrderByDescending(r => r.MatchConfidence)
                    .Take(1)
                    .ToList();

                _logger.LogInformation("Jumbo Playwright DOM: {Count} resultaten voor '{Product}'",
                    results.Count, item.Name);
                return results;
            }

            _logger.LogWarning("Jumbo Playwright: geen producten voor '{Product}'", item.Name);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo Playwright mislukt voor '{Product}'", item.Name);
            return [];
        }
        finally
        {
            if (page != null) try { await page.CloseAsync(); } catch { }
            if (ctx  != null) try { await ctx.CloseAsync();  } catch { }
        }
    }

    // ─── Parsers ─────────────────────────────────────────────────
    private List<ProductMatch> ParseGraphQLProducts(JsonElement products, string query)
    {
        var results = new List<ProductMatch>();
        foreach (var p in products.EnumerateArray())
        {
            var dto = ParseToDto(p);
            if (dto == null || dto.Price <= 0) continue;

            results.Add(new ProductMatch
            {
                StoreName       = "Jumbo",
                Country         = "NL",
                ProductName     = dto.Title,
                Price           = dto.Price,
                IsPromo         = dto.IsPromo,
                PromoText       = dto.IsPromo ? "Jumbo aanbieding" : "",
                IsEstimated     = false,
                IsBiologisch    = dto.Title.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                IsVegan         = dto.Title.Contains("vegan",      StringComparison.OrdinalIgnoreCase),
                IsHuisMerk      = IsHuisMerk(dto.Brand, dto.Title),
                IsAMerk         = !IsHuisMerk(dto.Brand, dto.Title),
                MatchConfidence = WordScore(query, dto.Title),
                LastUpdated     = DateTime.UtcNow
            });
            if (results.Count >= 3) break;
        }
        return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
    }

    private JumboProductDto? ParseToDto(JsonElement p)
    {
        var title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        decimal price = 0; bool isPromo = false;
        if (p.TryGetProperty("prices", out var prices))
        {
            if (prices.TryGetProperty("promotionalPrice", out var promo) &&
                promo.TryGetProperty("amount", out var pa))
            {
                price = pa.GetDecimal() / 100m;
                isPromo = true;
            }
            else if (prices.TryGetProperty("price", out var pr) &&
                     pr.TryGetProperty("amount", out var a))
            {
                price = a.GetDecimal() / 100m;
            }
        }
        else if (p.TryGetProperty("price", out var dp))
        {
            price = dp.ValueKind == JsonValueKind.Number ? dp.GetDecimal() : 0;
        }

        if (price <= 0) return null;
        var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";
        return new JumboProductDto { Title = title, Price = price, IsPromo = isPromo, Brand = brand };
    }

    // ─── Helpers ─────────────────────────────────────────────────
    private static double WordScore(string query, string product)
    {
        var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0.5;
        var lower = product.ToLower();
        return (double)words.Count(w => lower.Contains(w)) / words.Length;
    }

    private static bool IsHuisMerk(string brand, string name) =>
        HuisMerkPrefixes.Contains(brand) ||
        HuisMerkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private record JumboProductDto
    {
        public string  Title   { get; init; } = "";
        public decimal Price   { get; init; }
        public bool    IsPromo { get; init; }
        public string  Brand   { get; init; } = "";
    }
}
