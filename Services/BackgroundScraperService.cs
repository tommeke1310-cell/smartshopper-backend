using SmartShopper.API.Models;
using SmartShopper.API.Services.Scrapers;
using System.Text.Json;
using System.Text;

namespace SmartShopper.API.Services;

/// <summary>
/// Draait als achtergrond-service in Railway.
/// Scrapet elk product uit de Supabase `products` tabel bij alle winkels
/// en slaat de prijzen op in de `prices` tabel.
///
/// Schema:
///   - Direct na opstart: eerste scrape-run
///   - Daarna elke 6 uur opnieuw
/// </summary>
public class BackgroundScraperService : BackgroundService
{
    private readonly IServiceProvider          _services;
    private readonly ILogger<BackgroundScraperService> _logger;
    private readonly IConfiguration            _config;

    // Interval tussen scrape-runs
    private static readonly TimeSpan SCRAPE_INTERVAL  = TimeSpan.FromHours(6);
    // Wachttijd tussen individuele requests (voorkomt rate limiting)
    private static readonly TimeSpan REQUEST_DELAY    = TimeSpan.FromMilliseconds(800);
    // Wachttijd na een geblokkeerde store (429/403)
    private static readonly TimeSpan BACKOFF_DELAY    = TimeSpan.FromSeconds(30);

    public BackgroundScraperService(
        IServiceProvider services,
        ILogger<BackgroundScraperService> logger,
        IConfiguration config)
    {
        _services = services;
        _logger   = logger;
        _config   = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🔄 BackgroundScraperService gestart");

        // Kleine vertraging bij opstart zodat de rest van de app klaar is
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScrapeAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scrape-run mislukt");
            }

            _logger.LogInformation("⏱ Volgende scrape over {Hours} uur", SCRAPE_INTERVAL.TotalHours);
            await Task.Delay(SCRAPE_INTERVAL, stoppingToken);
        }
    }

    private async Task RunScrapeAsync(CancellationToken ct)
    {
        _logger.LogInformation("🛒 Scrape-run gestart om {Time}", DateTime.UtcNow);

        using var scope = _services.CreateScope();

        var ahScraper       = scope.ServiceProvider.GetRequiredService<AlbertHeijnScraper>();
        var lidlScraper     = scope.ServiceProvider.GetRequiredService<LidlScraper>();
        var aldiScraper     = scope.ServiceProvider.GetRequiredService<AldiScraper>();
        var reweScraper     = scope.ServiceProvider.GetRequiredService<ReweScraper>();
        var edekaScraper    = scope.ServiceProvider.GetRequiredService<EdekaScraper>();
        var colruytScraper  = scope.ServiceProvider.GetRequiredService<ColruytScraper>();
        var delhaizeScraper = scope.ServiceProvider.GetRequiredService<DelhaizeScraper>();

        // Haal alle producten op uit Supabase
        var products = await FetchAllProductsAsync();
        if (products.Count == 0)
        {
            _logger.LogWarning("Geen producten gevonden in Supabase — scrape overgeslagen");
            return;
        }

        _logger.LogInformation("📦 {Count} producten te scrapen", products.Count);
        int saved = 0, failed = 0;

        foreach (var product in products)
        {
            if (ct.IsCancellationRequested) break;

            var item = new GroceryItem { Id = product.Id, Name = product.Name };

            // Definieer welke scrapers voor welk land draaien
            var scraperTasks = new List<(string store, string country, Task<List<ProductMatch>> task)>
            {
                // NL
                ("Lidl",         "NL", lidlScraper.SearchProductAsync(item, "NL")),
                ("Aldi",         "NL", aldiScraper.SearchProductAsync(item, "NL")),
                // BE
                ("Lidl",         "BE", lidlScraper.SearchProductAsync(item, "BE")),
                ("Aldi",         "BE", aldiScraper.SearchProductAsync(item, "BE")),
                ("Colruyt",      "BE", colruytScraper.SearchProductAsync(item)),
                ("Delhaize",     "BE", delhaizeScraper.SearchProductAsync(item)),
                // DE
                ("Lidl",         "DE", lidlScraper.SearchProductAsync(item, "DE")),
                ("Aldi Süd",     "DE", aldiScraper.SearchProductAsync(item, "DE")),
                ("Rewe",         "DE", reweScraper.SearchProductAsync(item)),
                ("Edeka",        "DE", edekaScraper.SearchProductAsync(item)),
            };

            // Wacht op alle scrapers parallel (met timeout per product)
            try
            {
                await Task.WhenAll(scraperTasks.Select(t => t.task)
                    .Select(t => Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(12), ct))));

                var priceRows = new List<PriceRow>();
                foreach (var (store, country, task) in scraperTasks)
                {
                    if (!task.IsCompletedSuccessfully) continue;
                    foreach (var match in task.Result)
                    {
                        if (match.Price <= 0) continue;
                        priceRows.Add(new PriceRow
                        {
                            ProductId  = product.Id,
                            Store      = match.StoreName,
                            Country    = match.Country,
                            Price      = match.Price,
                            IsPromo    = match.IsPromo,
                            IsEstimated = match.IsEstimated,
                            Source     = "scraper",
                            ScrapedAt  = DateTime.UtcNow,
                        });
                    }
                }

                if (priceRows.Count > 0)
                {
                    await UpsertPricesAsync(priceRows);
                    saved += priceRows.Count;
                    _logger.LogDebug("✅ {Product}: {Count} prijzen opgeslagen", product.Name, priceRows.Count);
                }
                else
                {
                    failed++;
                    _logger.LogDebug("⚠️ {Product}: geen prijzen gevonden", product.Name);
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Fout bij scrapen van {Product}", product.Name);
            }

            // Kleine pauze tussen producten
            await Task.Delay(REQUEST_DELAY, ct);
        }

        _logger.LogInformation(
            "✅ Scrape-run klaar: {Saved} prijzen opgeslagen, {Failed} producten zonder resultaat",
            saved, failed);

        // Update scraper_health tabel
        await UpdateScraperHealthAsync(saved, failed);
    }

    // ─── Supabase helpers ────────────────────────────────────────────

    private async Task<List<ProductInfo>> FetchAllProductsAsync()
    {
        try
        {
            using var http = CreateSupabaseClient();
            var url      = $"{_config["Supabase:Url"]}/rest/v1/products?select=id,name,search_query&limit=1000";
            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<ProductInfo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kan producten niet ophalen uit Supabase");
            return [];
        }
    }

    private async Task UpsertPricesAsync(List<PriceRow> rows)
    {
        try
        {
            using var http = CreateSupabaseClient();
            var url  = $"{_config["Supabase:Url"]}/rest/v1/prices";
            var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            // Upsert op (product_id, store, country) — vervang als nieuwer
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates");

            var response = await http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Supabase upsert fout: {Status} {Error}", response.StatusCode, err);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Prijzen opslaan mislukt"); }
    }

    private async Task UpdateScraperHealthAsync(int saved, int failed)
    {
        try
        {
            using var http = CreateSupabaseClient();
            var url  = $"{_config["Supabase:Url"]}/rest/v1/scraper_health";

            // Haal bestaande kolommen op want schema is onbekend
            var payload = new[]
            {
                new { scraper = "background_scraper", last_run = DateTime.UtcNow, prices_saved = saved, failures = failed, status = failed == 0 ? "ok" : "partial" }
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates");
            await http.SendAsync(req);
        }
        catch { /* scraper health is niet kritiek */ }
    }

    private HttpClient CreateSupabaseClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("apikey",        _config["Supabase:ServiceKey"] ?? "");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_config["Supabase:ServiceKey"] ?? ""}");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type",  "application/json");
        return http;
    }

    // ─── Interne models ──────────────────────────────────────────────

    private record ProductInfo(string Id, string Name, string? SearchQuery);

    private class PriceRow
    {
        public string   ProductId   { get; set; } = "";
        public string   Store       { get; set; } = "";
        public string   Country     { get; set; } = "";
        public decimal  Price       { get; set; }
        public bool     IsPromo     { get; set; }
        public bool     IsEstimated { get; set; }
        public string   Source      { get; set; } = "scraper";
        public DateTime ScrapedAt   { get; set; } = DateTime.UtcNow;
    }
}
