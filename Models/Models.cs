using System;
using System.Collections.Generic;

namespace SmartShopper.API.Models
{
    // ─── REQUEST MODELS ──────────────────────────────────────────────

    public class CompareRequest
    {
        public string UserId { get; set; } = "";
        public List<GroceryItem> Items { get; set; } = new();

        // Locatie
        public double UserLatitude { get; set; }
        public double UserLongitude { get; set; }

        // Regio
        public bool IncludeGermany { get; set; } = true;
        public bool IncludeBelgium { get; set; } = true;
        public int MaxDistanceKm { get; set; } = 50;

        // Brandstof
        public decimal FuelConsumptionLPer100Km { get; set; } = 7.0m;
        public decimal? FuelPriceNl { get; set; }
        public decimal? FuelPriceBe { get; set; }
        public decimal? FuelPriceDe { get; set; }

        // Voorkeuren
        public UserPreferences? Preferences { get; set; }
        public List<string> PreferredStores { get; set; } = new();

        // AH bonuskaart (optioneel, ingelogd)
        public string? AhBearerToken { get; set; }
    }

    public class UserPreferences
    {
        public bool IsVegan { get; set; }
        public bool IsVegetarisch { get; set; }
        public bool IsGlutenvrij { get; set; }
        public bool IsLactosevrij { get; set; }
        public bool IsHalal { get; set; }
        public bool VoorkeurBiologisch { get; set; }
        public bool VoorkeurLokaal { get; set; }
        public string PrijsPrioriteit { get; set; } = "prijs-kwaliteit"; // goedkoopst | prijs-kwaliteit | kwaliteit
        public Dictionary<string, string> Merkvoorkeur { get; set; } = new(); // categorie → a-merk/huismerk/maakt-niet-uit
        public List<string> FavorieteWinkels { get; set; } = new();
        public decimal? Weekbudget { get; set; }
        public bool BudgetWaarschuwing { get; set; } = true;
    }

    public class GroceryItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public string Category { get; set; } = "";
        public string Unit { get; set; } = "stuk";
    }

    // ─── PRODUCT MATCH ────────────────────────────────────────────────

    public class ProductMatch
    {
        public string StoreName { get; set; } = "";
        public string Country { get; set; } = "NL";
        public string ProductName { get; set; } = "";
        public decimal Price { get; set; }
        public decimal NormalPrice { get; set; }
        public bool IsPromo { get; set; }
        public string PromoText { get; set; } = "";
        public bool IsEstimated { get; set; }       // true = schatting, toon ~ in UI
        public bool FromCache { get; set; }          // true = uit cache (<2u oud)
        public bool IsBiologisch { get; set; }
        public bool IsVegan { get; set; }
        public bool IsHuisMerk { get; set; }
        public bool IsAMerk { get; set; }
        public double MatchConfidence { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    // ─── STORE MODELS ────────────────────────────────────────────────

    public class StoreTemplate
    {
        public string Chain { get; set; } = "";
        public string City { get; set; } = "";
        public string Address { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double DistanceKm { get; set; }
        public int DriveTimeMinutes { get; set; }
        public string Country { get; set; } = "NL";
        public bool OpenNow { get; set; } = true;
    }

    public class ScraperResult
    {
        public string ProductName { get; set; }
        public decimal Price { get; set; }
        public bool Success { get; set; }
        public bool IsPromo { get; set; }
        public bool IsEstimated { get; set; }
        public string? FoundName { get; set; }  // Werkelijke gevonden naam bij de winkel
        public string? Name => ProductName;

        public ScraperResult(string productName, decimal price, bool success)
        {
            ProductName = productName;
            Price = price;
            Success = success;
        }
    }

    // ─── COMPARE RESULT ───────────────────────────────────────────────

    public class StoreComparison
    {
        public StoreTemplate Store { get; set; } = new();
        public List<ScraperResult> Products { get; set; } = new();
        public decimal GroceryTotal { get; set; }
        public decimal FuelCostEur { get; set; }
        public decimal TotalCost { get; set; }
        public decimal SavingsVsReference { get; set; }
        public bool IsBestDeal { get; set; }
        public int PreferenceMatchCount { get; set; }
        public int PreferenceTotalCount { get; set; }
    }

    public class CompareResult
    {
        public List<StoreComparison> Stores { get; set; } = new();
        public StoreComparison? BestDeal { get; set; }
        public decimal MaxSavings { get; set; }
        public BudgetWarning? Budget { get; set; }
        public List<BulkSuggestion> BulkSuggestions { get; set; } = new();
        public List<CrossBorderSuggestion> CrossBorderSuggestions { get; set; } = new();
        public bool HasFallbackStores { get; set; } // true = geen Google Maps key
        public string? Error { get; set; }           // foutmelding voor frontend
        public bool InvalidLocation { get; set; }    // true = 0,0 ontvangen
    }

    public class BudgetWarning
    {
        public bool OverWeekBudget { get; set; }
        public decimal WeekBudget { get; set; }
        public decimal BestDealTotal { get; set; }
        public decimal Overshoot { get; set; }
    }

    // ─── SUGGESTIES ───────────────────────────────────────────────────

    public class BulkSuggestion
    {
        public string ProductName { get; set; } = "";
        public int CurrentQty { get; set; }
        public int SuggestedQty { get; set; }
        public string CheapestStore { get; set; } = "";
        public string CheapestCountry { get; set; } = "";
        public decimal PricePerUnit { get; set; }
        public decimal SavingsEur { get; set; }
        public string Tip { get; set; } = "";
    }

    public class CrossBorderSuggestion
    {
        public string ProductName { get; set; } = "";
        public string NlStore { get; set; } = "";
        public decimal NlPrice { get; set; }
        public string ForeignStore { get; set; } = "";
        public string ForeignCountry { get; set; } = "";
        public decimal ForeignPrice { get; set; }
        public int SavingsPct { get; set; }
        public int DataPoints { get; set; }       // hoeveel metingen
        public string Tip { get; set; } = "";
    }

    // ─── SCRAPER HEALTH ───────────────────────────────────────────────

    public class ScraperHealthResult
    {
        public string Scraper { get; set; } = "";
        public bool IsLive { get; set; }
        public decimal LastKnownPrice { get; set; }
        public DateTime TestedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public int ResponseMs { get; set; }
    }

    // ─── SHARED LISTS ────────────────────────────────────────────────

    public class SharedList
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public List<string> MemberIds { get; set; } = new();
        public List<SharedListItem> Items { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class SharedListItem
    {
        public string Id { get; set; } = "";
        public string ListId { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
        public string Unit { get; set; } = "stuk";
        public bool Checked { get; set; }
        public string? AddedById { get; set; }
        public string? AddedByName { get; set; }
        public string? ImageUrl { get; set; }
        public string? Category { get; set; }
        public string Emoji { get; set; } = "🛒";
        public DateTime AddedAt { get; set; }
    }

    public class CreateSharedListRequest
    {
        public string Name { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public List<string> MemberIds { get; set; } = new();
    }

    public class AddItemToListRequest
    {
        public SharedListItem? Item { get; set; }
    }

    public class UpdateItemRequest
    {
        public bool? Checked { get; set; }
        public decimal? Quantity { get; set; }
        public string? Name { get; set; }
    }

    public class InviteMemberRequest
    {
        public string InviteEmail { get; set; } = "";
    }

    // ─── INTELLIGENCE MODELS ──────────────────────────────────────────

    public class BehaviorEvent
    {
        public string ProductName { get; set; } = "";
        public string? Barcode { get; set; }
        public string Action { get; set; } = "added";
        public string? StoreChain { get; set; }
        public string? Country { get; set; }
        public decimal PricePaid { get; set; }
        public int Quantity { get; set; } = 1;
        public string? SessionId { get; set; }
    }

    public class PurchaseRecord
    {
        public string StoreChain { get; set; } = "";
        public string Country { get; set; } = "NL";
        public string City { get; set; } = "";
        public decimal TotalSpent { get; set; }
        public decimal Savings { get; set; }
        public List<PurchaseItemRecord> Items { get; set; } = new();
    }

    public class PurchaseItemRecord
    {
        public string ProductName { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public decimal PricePaid { get; set; }
        public decimal CheapestPrice { get; set; }
        public string CheapestStore { get; set; } = "";
    }

    public class ProductRecommendation
    {
        public string ProductName { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string BestChain { get; set; } = "";
        public string BestCountry { get; set; } = "";
        public decimal BestPrice { get; set; }
        public decimal NormalPrice { get; set; }
        public int SavingsPct { get; set; }
        public bool IsPromo { get; set; }
        public long TimesBought { get; set; }
        public string Reason { get; set; } = "";
    }

    public class ShoppingPattern
    {
        public decimal TotalSpent90Days { get; set; }
        public decimal TotalSavings90Days { get; set; }
        public int TotalVisits90Days { get; set; }
        public string FavoriteStore { get; set; } = "";
        public decimal WeeklyBudget { get; set; }
        public decimal AvgSavingsPerTrip { get; set; }
        public Dictionary<string, int> StoreVisitCounts { get; set; } = new();
    }
}
