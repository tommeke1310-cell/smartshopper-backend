using Microsoft.AspNetCore.Mvc;
using SmartShopper.API.Models;
using SmartShopper.API.Services;

namespace SmartShopper.API.Controllers;

[ApiController]
[Route("api")]
public class CompareController : ControllerBase
{
    private readonly CompareService _compare;
    private readonly ScraperHealthService _health;
    private readonly ILogger<CompareController> _logger;

    public CompareController(
        CompareService compare,
        ScraperHealthService health,
        ILogger<CompareController> logger)
    {
        _compare = compare;
        _health = health;
        _logger = logger;
    }

    // POST /api/compare
    [HttpPost("compare")]
    public async Task<IActionResult> Compare([FromBody] CompareRequestDto dto)
    {
        if (dto.Items == null || !dto.Items.Any())
            return BadRequest("Voeg minimaal 1 product toe aan je lijst");

        var request = new CompareRequest
        {
            UserId = dto.UserId ?? "",
            Items = dto.Items,
            UserLatitude = dto.UserLatitude != 0 ? dto.UserLatitude : dto.Lat,
            UserLongitude = dto.UserLongitude != 0 ? dto.UserLongitude : dto.Lng,
            MaxDistanceKm = dto.MaxDistanceKm > 0 ? dto.MaxDistanceKm : 50,
            IncludeGermany = dto.IncludeGermany,
            IncludeBelgium = dto.IncludeBelgium,
            FuelConsumptionLPer100Km = dto.FuelConsumption > 0 ? dto.FuelConsumption : 7.0m,
            FuelPriceNl = dto.FuelPriceNl,
            FuelPriceBe = dto.FuelPriceBe,
            FuelPriceDe = dto.FuelPriceDe,
            PreferredStores = dto.PreferredStores ?? new(),
            AhBearerToken = dto.AhBearerToken,
            Preferences = dto.Preferences,
        };

        _logger.LogInformation(
            "Vergelijking: {Count} producten, locatie {Lat}/{Lng}, {Items}",
            dto.Items.Count, dto.Lat, dto.Lng,
            string.Join(", ", dto.Items.Select(i => i.Name)));

        var result = await _compare.ComparePricesAsync(request);

        if (!result.Stores.Any())
            return Ok(new
            {
                stores = Array.Empty<object>(),
                message = result.HasFallbackStores
                    ? "Geen Google Maps key geconfigureerd — fallback winkels worden gebruikt"
                    : "Geen winkels gevonden in jouw buurt"
            });

        return Ok(new
        {
            stores = result.Stores.Select(s => new
            {
                store = new
                {
                    chain = s.Store.Chain,
                    country = s.Store.Country,
                    city = s.Store.City,
                    address = s.Store.Address,
                    distanceKm = s.Store.DistanceKm,
                    driveTimeMinutes = s.Store.DriveTimeMinutes,
                    openNow = s.Store.OpenNow,
                    lat = s.Store.Latitude,
                    lng = s.Store.Longitude,
                },
                products = s.Products.Select((p, i) => new
                {
                    name = p.ProductName,
                    price = p.Price,
                    isEstimated = p.IsEstimated,      // ← frontend toont ~ als true
                    fromCache = false,
                    isPromo = p.IsPromo,
                    success = p.Success,
                }),
                groceryTotal = s.GroceryTotal,
                fuelCostEur = s.FuelCostEur,
                totalCost = s.TotalCost,
                savingsVsReference = s.SavingsVsReference,
                isBestDeal = s.IsBestDeal,
                preferenceMatchCount = s.PreferenceMatchCount,
                preferenceTotalCount = s.PreferenceTotalCount,
            }),
            bestDeal = result.BestDeal == null ? null : new
            {
                chain = result.BestDeal.Store.Chain,
                totalCost = result.BestDeal.TotalCost,
                savings = result.MaxSavings,
            },
            maxSavings = result.MaxSavings,
            hasFallbackStores = result.HasFallbackStores,
            budget = result.Budget,
            bulkSuggestions = result.BulkSuggestions,
            crossBorderSuggestions = result.CrossBorderSuggestions,
        });
    }

    // GET /api/health/scrapers
    // Toont welke scrapers live werken en welke op schatting vallen
    [HttpGet("health/scrapers")]
    public IActionResult ScraperHealth()
    {
        var results = _health.Results;
        int liveCount = results.Values.Count(r => r.IsLive);

        return Ok(new
        {
            summary = new
            {
                total = results.Count,
                live = liveCount,
                estimated = results.Count - liveCount,
                allLive = liveCount == results.Count,
            },
            scrapers = results.Values.Select(r => new
            {
                scraper = r.Scraper,
                isLive = r.IsLive,
                lastKnownPrice = r.LastKnownPrice,
                testedAt = r.TestedAt,
                responseMs = r.ResponseMs,
                errorMessage = r.ErrorMessage,
            })
        });
    }

    // POST /api/health/scrapers/refresh
    // Forceer een nieuwe health check
    [HttpPost("health/scrapers/refresh")]
    public async Task<IActionResult> RefreshHealth([FromServices] ScraperHealthService health)
    {
        await health.CheckAllScrapersAsync();
        return Ok(new { message = "Health check uitgevoerd", time = DateTime.UtcNow });
    }
}

// ─── Request DTO ──────────────────────────────────────────────────
// Aparte DTO zodat we backward compatible blijven met de app
public class CompareRequestDto
{
    public string? UserId { get; set; }
    public List<GroceryItem> Items { get; set; } = new();
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double UserLatitude { get; set; }
    public double UserLongitude { get; set; }
    public int MaxDistanceKm { get; set; } = 50;
    public bool IncludeGermany { get; set; } = true;
    public bool IncludeBelgium { get; set; } = true;
    public decimal FuelConsumption { get; set; } = 7.0m;
    public decimal? FuelPriceNl { get; set; }
    public decimal? FuelPriceBe { get; set; }
    public decimal? FuelPriceDe { get; set; }
    public List<string>? PreferredStores { get; set; }
    public string? AhBearerToken { get; set; }
    public UserPreferences? Preferences { get; set; }
}
