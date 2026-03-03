using Microsoft.AspNetCore.Mvc;
using SmartShopper.API.Services;

namespace SmartShopper.API.Controllers;

[ApiController]
[Route("api")]
public class IntelligenceController : ControllerBase
{
    private readonly IntelligenceService _intelligence;
    private readonly ILogger<IntelligenceController> _logger;

    public IntelligenceController(IntelligenceService intelligence, ILogger<IntelligenceController> logger)
    {
        _intelligence = intelligence;
        _logger = logger;
    }

    // POST /api/behavior
    // Gedrag tracken (product toegevoegd, gescand, etc.)
    [HttpPost("behavior")]
    public async Task<IActionResult> TrackBehavior([FromBody] BehaviorRequest req)
    {
        if (string.IsNullOrEmpty(req.UserId) || string.IsNullOrEmpty(req.ProductName))
            return BadRequest("userId en productName zijn verplicht");

        await _intelligence.TrackBehaviorAsync(req.UserId, new BehaviorEvent
        {
            ProductName = req.ProductName,
            Barcode     = req.Barcode,
            Action      = req.Action ?? "added",
            StoreChain  = req.StoreChain,
            Country     = req.Country,
            PricePaid   = req.PricePaid,
            Quantity    = req.Quantity > 0 ? req.Quantity : 1,
        });

        return Ok(new { success = true });
    }

    // POST /api/purchase
    // Volledige aankoop opslaan na vergelijking
    [HttpPost("purchase")]
    public async Task<IActionResult> SavePurchase([FromBody] PurchaseRequest req)
    {
        if (string.IsNullOrEmpty(req.UserId))
            return BadRequest("userId is verplicht");

        var purchaseId = await _intelligence.SavePurchaseAsync(req.UserId, new PurchaseRecord
        {
            StoreChain  = req.StoreChain,
            Country     = req.Country ?? "NL",
            City        = req.City ?? "",
            TotalSpent  = req.TotalSpent,
            Savings     = req.Savings,
            Items       = req.Items.Select(i => new PurchaseItemRecord
            {
                ProductName   = i.ProductName,
                Quantity      = i.Quantity,
                PricePaid     = i.PricePaid,
                CheapestPrice = i.CheapestPrice,
                CheapestStore = i.CheapestStore,
            }).ToList(),
        });

        // Detecteer gemiste deals op de achtergrond
        if (purchaseId != null)
            _ = Task.Run(() => _intelligence.DetectMissedDealsAsync(req.UserId, purchaseId));

        return Ok(new { success = true, purchaseId });
    }

    // GET /api/recommendations?userId=...&lat=...&lng=...
    // Slimme aanbevelingen op basis van shopgedrag
    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendations(
        [FromQuery] string userId,
        [FromQuery] double lat = 50.85,
        [FromQuery] double lng = 5.69)
    {
        if (string.IsNullOrEmpty(userId))
            return BadRequest("userId is verplicht");

        var recs    = await _intelligence.GetRecommendationsAsync(userId, lat, lng);
        var pattern = await _intelligence.AnalyzePatternAsync(userId);

        return Ok(new
        {
            recommendations = recs.Select(r => new
            {
                productName  = r.ProductName,
                imageUrl     = r.ImageUrl,
                bestChain    = r.BestChain,
                bestCountry  = r.BestCountry,
                bestPrice    = r.BestPrice,
                normalPrice  = r.NormalPrice,
                savingsPct   = r.SavingsPct,
                isPromo      = r.IsPromo,
                timesBought  = r.TimesBought,
                reason       = r.Reason,
            }),
            pattern = new
            {
                totalSpent90Days   = pattern.TotalSpent90Days,
                totalSavings90Days = pattern.TotalSavings90Days,
                totalVisits90Days  = pattern.TotalVisits90Days,
                favoriteStore      = pattern.FavoriteStore,
                weeklyBudget       = pattern.WeeklyBudget,
                avgSavingsPerTrip  = pattern.AvgSavingsPerTrip,
                storeVisitCounts   = pattern.StoreVisitCounts,
            },
        });
    }

    // GET /api/pattern?userId=...
    // Alleen shoppatroon
    [HttpGet("pattern")]
    public async Task<IActionResult> GetPattern([FromQuery] string userId)
    {
        if (string.IsNullOrEmpty(userId)) return BadRequest();
        var pattern = await _intelligence.AnalyzePatternAsync(userId);
        return Ok(pattern);
    }
}

// ── Request models ────────────────────────────────────────────────
public class BehaviorRequest
{
    public string  UserId      { get; set; } = "";
    public string  ProductName { get; set; } = "";
    public string? Barcode     { get; set; }
    public string? Action      { get; set; }
    public string? StoreChain  { get; set; }
    public string? Country     { get; set; }
    public decimal PricePaid   { get; set; }
    public int     Quantity    { get; set; } = 1;
}

public class PurchaseRequest
{
    public string  UserId     { get; set; } = "";
    public string  StoreChain { get; set; } = "";
    public string? Country    { get; set; }
    public string? City       { get; set; }
    public decimal TotalSpent { get; set; }
    public decimal Savings    { get; set; }
    public List<PurchaseItemRequest> Items { get; set; } = new();
}

public class PurchaseItemRequest
{
    public string  ProductName   { get; set; } = "";
    public int     Quantity      { get; set; } = 1;
    public decimal PricePaid     { get; set; }
    public decimal CheapestPrice { get; set; }
    public string  CheapestStore { get; set; } = "";
}
