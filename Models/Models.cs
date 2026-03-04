namespace SmartShopper.API.Models;

public class ScraperResult
{
    public string  ProductName { get; set; }
    public decimal Price       { get; set; }
    public bool    Success     { get; set; }
    public bool    IsPromo     { get; set; }
    public bool    IsEstimated { get; set; }

    public ScraperResult(string productName, decimal price, bool success)
    {
        ProductName = productName;
        Price       = price;
        Success     = success;
    }
}

public class StoreTemplate
{
    public string   Chain             { get; set; } = "";
    public string   City              { get; set; } = "";
    public string   Address           { get; set; } = "";
    public double   Latitude          { get; set; }
    public double   Longitude         { get; set; }
    public double   DistanceKm        { get; set; }
    public int      DriveTimeMinutes  { get; set; }
    public string   Country           { get; set; } = "NL";
    public bool     OpenNow           { get; set; }
    public bool     DeliveryAvailable { get; set; }
    public decimal? DeliveryCost      { get; set; }
}

public class ProductMatch
{
    public string  StoreName       { get; set; } = "";
    public string  Country         { get; set; } = "NL";
    public string  ProductName     { get; set; } = "";
    public decimal Price           { get; set; }
    public double  MatchConfidence { get; set; }
    public bool    IsPromo         { get; set; }
    public bool    IsEstimated     { get; set; }
    public bool    IsVegan         { get; set; }
    public bool    IsBiologisch    { get; set; }
    public bool    IsAMerk         { get; set; }
    public bool    IsHuisMerk      { get; set; }
    public string? AllergenInfo    { get; set; }
}

public class UserPreferences
{
    public bool            IsVegan          { get; set; }
    public bool            IsVegetarisch    { get; set; }
    public bool            IsGlutenvrij     { get; set; }
    public bool            IsLactosevrij    { get; set; }
    public bool            IsHalal          { get; set; }
    public bool            IsSuikerarm      { get; set; }
    public List<string>    Allergieën       { get; set; } = new();
    public Dictionary<string, string> Merkvoorkeur { get; set; } = new();
    public bool VoorkeurBiologisch  { get; set; }
    public bool VoorkeurLokaal      { get; set; }
    public bool GeenPalmolie        { get; set; }
    public bool MinderPlastic       { get; set; }
    public bool         PreferSingleStore { get; set; }
    public List<string> FavorieteWinkels  { get; set; } = new();
    public string       PrijsPrioriteit   { get; set; } = "prijs-kwaliteit";
    public decimal? Weekbudget         { get; set; }
    public bool     BudgetWaarschuwing { get; set; } = true;
}

public class CompareRequest
{
    public List<GroceryItem> Items           { get; set; } = new();
    public double   UserLatitude             { get; set; }
    public double   UserLongitude            { get; set; }
    public int      MaxDistanceKm            { get; set; } = 50;
    public bool     IncludeBelgium           { get; set; }
    public bool     IncludeGermany           { get; set; }
    public decimal  FuelConsumptionLPer100Km { get; set; } = 7.0m;
    public string   FuelType                 { get; set; } = "e10";
    public decimal? FuelPriceNl              { get; set; }
    public decimal? FuelPriceBe              { get; set; }
    public decimal? FuelPriceDe              { get; set; }
    public List<string> PreferredStores      { get; set; } = new();
    public string?  UserId                   { get; set; }
    public UserPreferences? Preferences      { get; set; }
}

public class GroceryItem
{
    public string  Name     { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public string  Unit     { get; set; } = "stuk";
}

public class StoreComparison
{
    public StoreTemplate       Store               { get; set; } = new();
    public List<ScraperResult> Products            { get; set; } = new();
    public decimal GroceryTotal                    { get; set; }
    public decimal FuelCostEur                     { get; set; }
    public decimal TotalCost                       { get; set; }
    public decimal SavingsVsReference              { get; set; }
    public bool    IsBestDeal                      { get; set; }
    public int     PreferenceMatchCount            { get; set; }
    public int     PreferenceTotalCount            { get; set; }
}

public class CompareResult
{
    public List<StoreComparison> Stores     { get; set; } = new();
    public StoreComparison?      BestDeal   { get; set; }
    public decimal               MaxSavings { get; set; }
    public FuelPriceSnapshot?    FuelPrices { get; set; }
    public BudgetWarning?        Budget     { get; set; }
}

public class FuelPriceSnapshot
{
    public CountryFuelPrices Nl { get; set; } = new();
    public CountryFuelPrices Be { get; set; } = new();
    public CountryFuelPrices De { get; set; } = new();
}

public class CountryFuelPrices
{
    public decimal E10    { get; set; }
    public decimal E98    { get; set; }
    public decimal Diesel { get; set; }
    public decimal Lpg    { get; set; }
}

public class BudgetWarning
{
    public bool    OverWeekBudget { get; set; }
    public decimal WeekBudget     { get; set; }
    public decimal BestDealTotal  { get; set; }
    public decimal Overshoot      { get; set; }
}

public class SharedList
{
    public string       Id        { get; set; } = Guid.NewGuid().ToString();
    public string       Name      { get; set; } = "";
    public string       OwnerId   { get; set; } = "";
    public List<string> MemberIds { get; set; } = new();
    public List<SharedListItem> Items { get; set; } = new();
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt     { get; set; } = DateTime.UtcNow;
}

public class SharedListItem
{
    public string   Id          { get; set; } = Guid.NewGuid().ToString();
    public string   Name        { get; set; } = "";
    public decimal  Quantity    { get; set; } = 1;
    public string   Unit        { get; set; } = "stuk";
    public bool     Checked     { get; set; }
    public string   AddedByName { get; set; } = "";
    public string   AddedById   { get; set; } = "";
    public string?  ImageUrl    { get; set; }
    public string?  Category    { get; set; }
    public string   Emoji       { get; set; } = "🛒";
    public DateTime AddedAt     { get; set; } = DateTime.UtcNow;
}

public class CreateSharedListRequest
{
    public string       Name      { get; set; } = "";
    public string       OwnerId   { get; set; } = "";
    public List<string> MemberIds { get; set; } = new();
}

public class AddItemToListRequest
{
    public string        UserId   { get; set; } = "";
    public string        UserName { get; set; } = "";
    public SharedListItem Item    { get; set; } = new();
}

public class UpdateItemRequest
{
    public string   UserId   { get; set; } = "";
    public bool?    Checked  { get; set; }
    public decimal? Quantity { get; set; }
}

public class InviteMemberRequest
{
    public string InvitedByUserId { get; set; } = "";
    public string InviteEmail     { get; set; } = "";
}
