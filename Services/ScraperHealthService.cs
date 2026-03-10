using SmartShopper.API.Models;
using SmartShopper.API.Services.Scrapers;
using System.Collections.Concurrent;

namespace SmartShopper.API.Services;

/// <summary>
/// Draait elke 30 minuten een gezondheidscheck op alle scrapers.
/// Resultaten zijn beschikbaar via GET /api/health/scrapers.
/// Zo weet je altijd welke scrapers live werken en welke op schatting vallen.
/// </summary>
public class ScraperHealthService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ScraperHealthService> _logger;

    // Thread-safe resultaten opslaan
    private readonly ConcurrentDictionary<string, ScraperHealthResult> _results = new();

    // Testproduct per scraper — iets wat altijd bestaat
    private static readonly Dictionary<string, (string name, string country)> TestProducts = new()
    {
        ["AlbertHeijn"] = ("Coca-Cola", "NL"),
        ["Jumbo"]       = ("Coca-Cola", "NL"),
        ["Lidl NL"]     = ("Cola", "NL"),
        ["Lidl DE"]     = ("Cola", "DE"),
        ["Aldi NL"]     = ("Cola", "NL"),
        ["Aldi DE"]     = ("Cola", "DE"),
    };

    public ScraperHealthService(IServiceProvider services, ILogger<ScraperHealthService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, ScraperHealthResult> Results => _results;

    /// <summary>
    /// Draait continu op achtergrond, elke 30 minuten.
    /// Gestart in Program.cs via Task.Run.
    /// </summary>
    public async Task RunHealthChecksAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("ScraperHealthService gestart");

        while (!ct.IsCancellationRequested)
        {
            await CheckAllScrapersAsync();
            await Task.Delay(TimeSpan.FromMinutes(30), ct);
        }
    }

    public async Task CheckAllScrapersAsync()
    {
        _logger.LogInformation("Scraper health check gestart...");

        using var scope = _services.CreateScope();
        var ah    = scope.ServiceProvider.GetRequiredService<AlbertHeijnScraper>();
        var jumbo = scope.ServiceProvider.GetRequiredService<JumboScraper>();
        var lidl  = scope.ServiceProvider.GetRequiredService<LidlScraper>();
        var aldi  = scope.ServiceProvider.GetRequiredService<AldiScraper>();
        var plus  = scope.ServiceProvider.GetRequiredService<PlusScraper>();
        var dirk  = scope.ServiceProvider.GetRequiredService<DirkScraper>();

        await Task.WhenAll(
            CheckAsync("AlbertHeijn", () => ah.SearchProductAsync(new() { Name = "Coca-Cola", Quantity = 1 })),
            CheckAsync("Jumbo",       () => jumbo.SearchProductAsync(new() { Name = "Coca-Cola", Quantity = 1 })),
            CheckAsync("Lidl NL",     () => lidl.SearchProductAsync(new() { Name = "Cola", Quantity = 1 }, "NL")),
            CheckAsync("Lidl DE",     () => lidl.SearchProductAsync(new() { Name = "Cola", Quantity = 1 }, "DE")),
            CheckAsync("Aldi NL",     () => aldi.SearchProductAsync(new() { Name = "Cola", Quantity = 1 }, "NL")),
            CheckAsync("Aldi DE",     () => aldi.SearchProductAsync(new() { Name = "Cola", Quantity = 1 }, "DE")),
            CheckAsync("Plus NL",     () => plus.SearchProductAsync(new() { Name = "Coca-Cola", Quantity = 1 })),
            CheckAsync("Dirk NL",     () => dirk.SearchProductAsync(new() { Name = "Coca-Cola", Quantity = 1 }))
        );

        var live  = _results.Values.Count(r => r.IsLive);
        var total = _results.Count;
        _logger.LogInformation("Health check klaar: {Live}/{Total} scrapers live", live, total);
    }

    private async Task CheckAsync(string name, Func<Task<List<ProductMatch>>> scrapeFunc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var results = await scrapeFunc();
            sw.Stop();

            var isLive = results.Count > 0 && results[0].Price > 0 && !results[0].IsEstimated;

            _results[name] = new ScraperHealthResult
            {
                Scraper = name,
                IsLive = isLive,
                LastKnownPrice = results.FirstOrDefault()?.Price ?? 0,
                TestedAt = DateTime.UtcNow,
                ResponseMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage = isLive ? null : "Geen live prijs gevonden"
            };

            _logger.LogInformation("  {Name}: {Status} ({Ms}ms)",
                name, isLive ? "✅ LIVE" : "⚠️  SCHATTING", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _results[name] = new ScraperHealthResult
            {
                Scraper = name,
                IsLive = false,
                TestedAt = DateTime.UtcNow,
                ResponseMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message
            };
            _logger.LogWarning("  {Name}: ❌ FOUT — {Error}", name, ex.Message);
        }
    }
}
