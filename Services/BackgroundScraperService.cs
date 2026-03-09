using SmartShopper.API.Models;
using SmartShopper.API.Services.Scrapers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Globalization;
using HtmlAgilityPack;

namespace SmartShopper.API.Services;

/// <summary>
/// Draait elke 6 uur op de achtergrond en scrapt alle bekende scrapers incl. AH en Jumbo.
/// Resultaten worden opgeslagen in Supabase `prices` tabel.
/// Dit zorgt dat de vergelijking altijd actuele prijzen heeft,
/// ook als een on-demand scrape tijdelijk mislukt.
/// </summary>
public class BackgroundScraperService : BackgroundService
{
    private readonly ILogger<BackgroundScraperService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _services;

    private static readonly TimeSpan SCRAPE_INTERVAL = TimeSpan.FromHours(6);
    private static readonly TimeSpan REQUEST_DELAY   = TimeSpan.FromMilliseconds(800);

    public BackgroundScraperService(
        ILogger<BackgroundScraperService> logger,
        IConfiguration config,
        IServiceProvider services)
    {
        _logger   = logger;
        _config   = config;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔄 BackgroundScraperService gestart");

        // Wacht 30s zodat de app volledig opgestart is
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        var url = _config["Supabase:Url"];
        var key = _config["Supabase:ServiceKey"];

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
        {
            _logger.LogError("❌ Supabase config ontbreekt — Url:{Url} Key:{Key}",
                string.IsNullOrEmpty(url) ? "LEEG" : "ok",
                string.IsNullOrEmpty(key) ? "LEEG" : "ok");
            return;
        }

        _logger.LogInformation("✅ Config ok: {Url}", url);

        while (!ct.IsCancellationRequested)
        {
            try   { await RunScrapeAsync(url, key, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Scrape-run mislukt"); }

            _logger.LogInformation("⏱ Volgende scrape over {H} uur", SCRAPE_INTERVAL.TotalHours);
            await Task.Delay(SCRAPE_INTERVAL, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    private async Task RunScrapeAsync(string supabaseUrl, string supabaseKey, CancellationToken ct)
    {
        _logger.LogInformation("🛒 Scrape-run gestart: {Time}", DateTime.UtcNow);

        var products = await FetchProductsAsync(supabaseUrl, supabaseKey);
        if (products.Count == 0) { _logger.LogWarning("Geen producten gevonden"); return; }

        _logger.LogInformation("📦 {N} producten te scrapen", products.Count);

        int saved = 0, skipped = 0;

        foreach (var product in products)
        {
            if (ct.IsCancellationRequested) break;

            var prices = await ScrapeAllStoresAsync(product.Name, ct);

            if (prices.Count > 0)
            {
                var rows = prices.Select(p => new PriceRow
                {
                    ProductId   = product.Id,
                    Store       = p.Store,
                    Country     = p.Country,
                    Price       = p.Price,
                    IsPromo     = p.IsPromo,
                    IsEstimated = false,
                    Source      = "scraper",
                    ScrapedAt   = DateTime.UtcNow,
                }).ToList();

                await UpsertPricesAsync(supabaseUrl, supabaseKey, rows);
                saved += rows.Count;
                _logger.LogInformation("✅ {Product}: {N} prijzen opgeslagen", product.Name, rows.Count);
            }
            else
            {
                skipped++;
                _logger.LogDebug("⚠️ {Product}: geen resultaat van scrapers", product.Name);
            }

            await Task.Delay(REQUEST_DELAY, ct);
        }

        _logger.LogInformation("🏁 Klaar: {Saved} prijzen opgeslagen, {Skip} overgeslagen", saved, skipped);
    }

    // ─────────────────────────────────────────────────────────────────
    // Scrape ALLE winkels voor één product, inclusief AH en Jumbo
    // ─────────────────────────────────────────────────────────────────
    private async Task<List<(string Store, string Country, decimal Price, bool IsPromo)>> ScrapeAllStoresAsync(
        string productName, CancellationToken ct)
    {
        var results = new List<(string, string, decimal, bool)>();
        using var http = MakeHttpClient();

        var item = new GroceryItem { Name = productName, Id = "", Quantity = 1 };

        // ── NL scrapers ──────────────────────────────────────────────
        var ahLogger   = _services.GetRequiredService<ILogger<AlbertHeijnScraper>>();
        var jumboLogger = _services.GetRequiredService<ILogger<JumboScraper>>();

        var nlTasks = new List<Task<(string Store, string Country, decimal Price, bool IsPromo)?>>();

        // Albert Heijn — nu ook in background!
        nlTasks.Add(TryScrapeService("Albert Heijn", "NL", async () =>
        {
            using var ahHttp = MakeHttpClient();
            var scraper = new AlbertHeijnScraper(ahHttp, ahLogger);
            var matches = await scraper.SearchProductAsync(item);
            var best = matches.OrderByDescending(m => m.MatchConfidence).FirstOrDefault();
            return best != null && best.Price > 0 ? (best.Price, best.IsPromo) : ((decimal, bool)?)null;
        }));

        // Jumbo — nu ook in background!
        nlTasks.Add(TryScrapeService("Jumbo", "NL", async () =>
        {
            using var jumboHttp = MakeHttpClient();
            var scraper = new JumboScraper(jumboHttp, jumboLogger);
            var matches = await scraper.SearchProductAsync(item);
            var best = matches.OrderByDescending(m => m.MatchConfidence).FirstOrDefault();
            return best != null && best.Price > 0 ? (best.Price, best.IsPromo) : ((decimal, bool)?)null;
        }));

        // Lidl NL
        nlTasks.Add(TryScrape("Lidl", "NL", () => ScrapeLidl(http, productName, "NL")));
        // Aldi NL
        nlTasks.Add(TryScrape("Aldi", "NL", () => ScrapeAldi(http, productName, "NL")));

        // ── BE scrapers ──────────────────────────────────────────────
        nlTasks.Add(TryScrape("Lidl",     "BE", () => ScrapeLidl(http, productName, "BE")));
        nlTasks.Add(TryScrape("Aldi",     "BE", () => ScrapeAldi(http, productName, "BE")));
        nlTasks.Add(TryScrape("Colruyt",  "BE", () => ScrapeColruyt(http, productName)));
        nlTasks.Add(TryScrape("Delhaize", "BE", () => ScrapeDelhaize(http, productName)));

        // ── DE scrapers ──────────────────────────────────────────────
        nlTasks.Add(TryScrape("Lidl",     "DE", () => ScrapeLidl(http, productName, "DE")));
        nlTasks.Add(TryScrape("Aldi Süd", "DE", () => ScrapeAldi(http, productName, "DE")));
        nlTasks.Add(TryScrape("Rewe",     "DE", () => ScrapeRewe(http, productName)));
        nlTasks.Add(TryScrape("Edeka",    "DE", () => ScrapeEdeka(http, productName)));

        var all = await Task.WhenAll(nlTasks);
        foreach (var r in all)
            if (r.HasValue) results.Add(r.Value);

        return results;
    }

    private async Task<(string Store, string Country, decimal Price, bool IsPromo)?> TryScrapeService(
        string store, string country, Func<Task<(decimal price, bool isPromo)?>> fn)
    {
        try
        {
            var result = await fn();
            if (result.HasValue && result.Value.price > 0)
                return (store, country, result.Value.price, result.Value.isPromo);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Service scrape mislukt {Store}/{Country}: {Msg}", store, country, ex.Message);
        }
        return null;
    }

    private async Task<(string Store, string Country, decimal Price, bool IsPromo)?> TryScrape(
        string store, string country, Func<Task<(decimal price, bool isPromo)>> fn)
    {
        try
        {
            var (price, isPromo) = await fn();
            if (price > 0) return (store, country, price, isPromo);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Scrape mislukt {Store}/{Country}: {Msg}", store, country, ex.Message);
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // LIDL
    // ─────────────────────────────────────────────────────────────────
    private static async Task<(decimal, bool)> ScrapeLidl(HttpClient http, string query, string country)
    {
        var (domain, locale) = country switch
        {
            "DE" => ("lidl.de", "DE/de"),
            "BE" => ("lidl.be", "BE/nl"),
            _    => ("lidl.nl", "NL/nl")
        };

        var url  = $"https://www.{domain}/p/api/gridboxes/{locale}/?max=10&search={Uri.EscapeDataString(query)}";
        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array) return (0, false);

        var candidates = new List<(decimal price, bool promo, double score)>();

        foreach (var box in doc.RootElement.EnumerateArray())
        {
            if (!box.TryGetProperty("gridList", out var list)) continue;
            foreach (var item in list.EnumerateArray())
            {
                var price = ExtractNumeric(item, "price", "price") ??
                            ExtractNumeric(item, "price", "regularPrice") ?? 0;
                if (price <= 0) continue;

                var title = item.TryGetProperty("keyfacts", out var kf) && kf.TryGetProperty("name", out var n)
                    ? n.GetString() ?? query : query;
                bool promo = item.TryGetProperty("price", out var po) && po.TryGetProperty("discount", out _);
                double score = ProductMatcher.Score(query, title);
                candidates.Add((price, promo, score));
            }
        }

        if (candidates.Count == 0) return (0, false);
        var best = candidates.OrderByDescending(c => c.score).First();
        return (best.price, best.promo);
    }

    // ─────────────────────────────────────────────────────────────────
    // ALDI
    // ─────────────────────────────────────────────────────────────────
    private static async Task<(decimal, bool)> ScrapeAldi(HttpClient http, string query, string country)
    {
        if (country == "DE")
        {
            var apiUrl = $"https://api.aldi-sued.de/v1/search?term={Uri.EscapeDataString(query)}&page=1&pageSize=5&country=DE";
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.TryAddWithoutValidation("x-api-key", "public");
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("data", out var data))
                    foreach (var item in data.EnumerateArray())
                    {
                        var price = ExtractNumeric(item, "pricing", "currentPrice") ?? 0;
                        if (price > 0) return (price, false);
                    }
            }
        }
        else
        {
            var domain = country == "BE" ? "aldi.be" : "aldi.nl";
            var html   = await http.GetStringAsync($"https://www.{domain}/nl/zoeken.html?q={Uri.EscapeDataString(query)}");
            var price  = ExtractPriceFromHtml(html);
            if (price > 0) return (price, false);
        }
        return (0, false);
    }

    // ─────────────────────────────────────────────────────────────────
    // COLRUYT
    // ─────────────────────────────────────────────────────────────────
    private static async Task<(decimal, bool)> ScrapeColruyt(HttpClient http, string query)
    {
        // Probeer publieke catalog API eerst
        var urls = new[]
        {
            $"https://www.colruyt.be/colruytAPI/api/products?text={Uri.EscapeDataString(query)}&placeId=&site=colruyt&language=NL&count=5",
            $"https://ecg.colruyt.be/PRODUITS/services/searchProducts?searchTerm={Uri.EscapeDataString(query)}&start=0&count=5&site=Colruyt",
        };

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Referer", "https://www.colruyt.be/");
                var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                JsonElement products;
                if (doc.RootElement.TryGetProperty("products", out products) ||
                    doc.RootElement.TryGetProperty("data",     out products))
                    foreach (var p in products.EnumerateArray())
                    {
                        var price = ExtractFirstNumeric(p, "price", "basicPrice", "currentPrice", "recommendedPrice") ?? 0;
                        if (price > 0) return (price, p.TryGetProperty("promotion", out _));
                    }
            }
            catch { /* probeer volgende */ }
        }

        var html2 = await http.GetStringAsync($"https://www.colruyt.be/nl/zoekopdracht/{Uri.EscapeDataString(query)}");
        return (ExtractPriceFromHtml(html2), false);
    }

    // ─────────────────────────────────────────────────────────────────
    // DELHAIZE
    // ─────────────────────────────────────────────────────────────────
    private static async Task<(decimal, bool)> ScrapeDelhaize(HttpClient http, string query)
    {
        var url = $"https://www.delhaize.be/api/v2/search?q={Uri.EscapeDataString(query)}&pageSize=5&currentPage=0&lang=nl";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.delhaize.be/");
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement products;
            if (doc.RootElement.TryGetProperty("products", out products) ||
                doc.RootElement.TryGetProperty("results",  out products))
                foreach (var p in products.EnumerateArray())
                {
                    var price = ExtractFirstNumeric(p, "price", "currentPrice", "normalPrice") ?? 0;
                    if (price > 0) return (price, p.TryGetProperty("discountedPrice", out _));
                }
        }
        var html2 = await http.GetStringAsync($"https://www.delhaize.be/nl-be/recherche?text={Uri.EscapeDataString(query)}");
        return (ExtractPriceFromHtml(html2), false);
    }

    // ─────────────────────────────────────────────────────────────────
    // REWE
    // ─────────────────────────────────────────────────────────────────
    private static async Task<(decimal, bool)> ScrapeRewe(HttpClient http, string query)
    {
        var url = $"https://shop.rewe.de/api/v7/products?search={Uri.EscapeDataString(query)}&page=1&pageSize=5&marketId=562223";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", "https://shop.rewe.de/");
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement items;
            if (doc.RootElement.TryGetProperty("products", out items) ||
                doc.RootElement.TryGetProperty("items",    out items))
                foreach (var p in items.EnumerateArray())
                {
                    var price = ExtractNumeric(p, "pricing", "currentRetailPrice") ??
                                ExtractNumeric(p, "pricing", "price") ??
                                ExtractFirstNumeric(p, "price") ?? 0;
                    if (price > 0) return (price, false);
                }
        }
        var html2 = await http.GetStringAsync($"https://www.rewe.de/suche/?search={Uri.EscapeDataString(query)}");
        return (ExtractPriceFromHtml(html2), false);
    }

    // ─────────────────────────────────────────────────────────────────
    // EDEKA
    // ─────────────────────────────────────────────────────────────────
    private static async Task<(decimal, bool)> ScrapeEdeka(HttpClient http, string query)
    {
        var url = $"https://www.edeka.de/api/search/v1/products?q={Uri.EscapeDataString(query)}&page=0&size=5";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.edeka.de/");
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement items;
            if (doc.RootElement.TryGetProperty("products", out items) ||
                doc.RootElement.TryGetProperty("content",  out items))
                foreach (var p in items.EnumerateArray())
                {
                    var price = ExtractFirstNumeric(p, "price", "currentPrice") ?? 0;
                    if (price > 0) return (price, false);
                }
        }
        var html2 = await http.GetStringAsync($"https://www.edeka.de/produkte/suchergebnis.jsp?query={Uri.EscapeDataString(query)}");
        return (ExtractPriceFromHtml(html2), false);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────
    private static decimal ExtractPriceFromHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        string[] selectors =
        [
            "//meta[@itemprop='price']",
            "//span[contains(@class,'price__wrapper')]",
            "//span[contains(@class,'m-price__price')]",
            "//span[contains(@class,'item-price')]",
            "//span[contains(@class,'product-price')]",
            "//*[@data-testid='product-price']",
            "//span[contains(@class,'price') and not(contains(@class,'old'))]",
        ];

        foreach (var sel in selectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(sel);
            if (node == null) continue;
            var raw = node.Name == "meta" ? node.GetAttributeValue("content", "") : node.InnerText;
            var m   = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", "."), @"\d+\.\d{2}");
            if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0)
                return p;
        }
        return 0;
    }

    private static decimal? ExtractNumeric(JsonElement el, string key1, string key2)
    {
        if (!el.TryGetProperty(key1, out var o1)) return null;
        if (!o1.TryGetProperty(key2, out var v))  return null;
        return v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    }

    private static decimal? ExtractFirstNumeric(JsonElement el, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!el.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) &&
                inner.ValueKind == JsonValueKind.Number) return inner.GetDecimal();
        }
        return null;
    }

    private static HttpClient MakeHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9,de;q=0.8");
        return client;
    }

    // ─────────────────────────────────────────────────────────────────
    // Supabase
    // ─────────────────────────────────────────────────────────────────
    private async Task<List<ProductInfo>> FetchProductsAsync(string supabaseUrl, string key)
    {
        try
        {
            using var http = MakeSupabaseClient(supabaseUrl, key);
            var resp = await http.GetAsync($"{supabaseUrl}/rest/v1/products?select=id,name&limit=1000");
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Supabase producten fout: {Status}", resp.StatusCode);
                return [];
            }
            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<ProductInfo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (Exception ex) { _logger.LogError(ex, "FetchProducts mislukt"); return []; }
    }

    private async Task UpsertPricesAsync(string supabaseUrl, string key, List<PriceRow> rows)
    {
        try
        {
            using var http = MakeSupabaseClient(supabaseUrl, key);
            var json = JsonSerializer.Serialize(rows);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{supabaseUrl}/rest/v1/prices");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates");
            var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Upsert fout: {S} {E}", resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { _logger.LogError(ex, "UpsertPrices mislukt"); }
    }

    private static HttpClient MakeSupabaseClient(string url, string key)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("apikey",        key);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type",  "application/json");
        return http;
    }

    // ─────────────────────────────────────────────────────────────────
    // Models
    // ─────────────────────────────────────────────────────────────────
    private record ProductInfo(
        [property: JsonPropertyName("id")]   string Id,
        [property: JsonPropertyName("name")] string Name
    );

    private class PriceRow
    {
        [JsonPropertyName("product_id")]   public string   ProductId   { get; set; } = "";
        [JsonPropertyName("store")]        public string   Store       { get; set; } = "";
        [JsonPropertyName("country")]      public string   Country     { get; set; } = "";
        [JsonPropertyName("price")]        public decimal  Price       { get; set; }
        [JsonPropertyName("is_promo")]     public bool     IsPromo     { get; set; }
        [JsonPropertyName("is_estimated")] public bool     IsEstimated { get; set; }
        [JsonPropertyName("source")]       public string   Source      { get; set; } = "scraper";
        [JsonPropertyName("scraped_at")]   public DateTime ScrapedAt   { get; set; } = DateTime.UtcNow;
    }
}
