using SmartShopper.API.Models;
using SmartShopper.API.Services.Scrapers;
using SmartShopper.API.Services.Routing;

namespace SmartShopper.API.Services;

public class CompareService
{
    private readonly AlbertHeijnScraper _ah;
    private readonly JumboScraper _jumbo;
    private readonly LidlScraper _lidl;
    private readonly AldiScraper _aldi;
    private readonly RoutingService _routing;
    private readonly FuelPriceService _fuel;
    private readonly ILogger<CompareService> _logger;

    public CompareService(
        AlbertHeijnScraper ah, JumboScraper jumbo, LidlScraper lidl,
        AldiScraper aldi, RoutingService routing, FuelPriceService fuel,
        ILogger<CompareService> logger)
    {
        _ah = ah; _jumbo = jumbo; _lidl = lidl; _aldi = aldi;
        _routing = routing; _fuel = fuel; _logger = logger;
    }

    public async Task<CompareResult> CompareAsync(CompareRequest req)
    {
        // 1. Haal actuele brandstofprijzen op
        var fuelPrices = await _fuel.GetCurrentPricesAsync();

        // 2. Definieer de zoekparameters (straal van de gebruiker)
        // TODO: In de toekomst halen we hier ECHTE winkels op via Google Places
        // Voor nu optimaliseren we de berekening voor de locaties die we vinden
        var potentialStores = GetNearbyStoreTemplates(req.UserLocation);

        var storeComparisons = new List<StoreComparison>();

        foreach (var store in potentialStores)
        {
            var storeTotal = 0m;
            var matchedProducts = new List<ProductMatch>();

            // 3. Per winkel de producten scrapen
            foreach (var item in req.Items)
            {
                var match = await GetBestMatchForStore(store, item);
                if (match != null)
                {
                    matchedProducts.Add(match);
                    storeTotal += match.Price;
                }
            }

            // 4. Bereken reiskosten naar deze specifieke winkel
            // We gebruiken de land-specifieke benzineprijs
            decimal localFuelPrice = store.Country switch
            {
                "DE" => fuelPrices.DE,
                "BE" => fuelPrices.BE,
                _ => fuelPrices.NL
            };

            var route = await _routing.CalculateTripAsync(
                req.UserLocation.Lat, req.UserLocation.Lng,
                store.Lat, store.Lng,
                localFuelPrice,
                (double)req.UserPreferences.FuelConsumption // Gebruikers-specifiek verbruik!
            );

            storeComparisons.Add(new StoreComparison
            {
                Store = new StoreInfo
                {
                    Chain = store.Chain,
                    Country = store.Country,
                    City = store.City,
                    DistanceKm = route.km,
                    DriveTimeMinutes = route.min
                },
                Products = matchedProducts,
                GroceryTotal = storeTotal,
                FuelCostEur = route.fuel,
                TotalCost = storeTotal + route.fuel,
                CrossBorderTip = GenerateTip(store, fuelPrices, route.km)
            });
        }

        // 5. Bepaal de winnaar
        var best = storeComparisons.OrderBy(s => s.TotalCost).First();
        best.IsBestDeal = true;

        return new CompareResult
        {
            Stores = storeComparisons.OrderBy(s => s.TotalCost).ToList(),
            BestDeal = best,
            FuelPrices = fuelPrices
        };
    }

    private async Task<ProductMatch?> GetBestMatchForStore(StoreTemplate store, GroceryItem item)
    {
        return store.Chain.ToLower() switch
        {
            "albert heijn" => (await _ah.SearchProductAsync(item)).FirstOrDefault(),
            "jumbo" => (await _jumbo.SearchProductAsync(item)).FirstOrDefault(),
            "lidl" => (await _lidl.SearchProductAsync(item, store.Country)).FirstOrDefault(),
            "aldi" => (await _aldi.SearchProductAsync(item, store.Country)).FirstOrDefault(),
            _ => null
        };
    }

    private string GenerateTip(StoreTemplate store, FuelPrices prices, double km)
    {
        if (store.Country == "DE" && prices.DE < prices.NL - 0.15m)
            return "Tanken in Duitsland bespaart je veel geld op deze rit!";
        if (km > 20)
            return "Lange rit, overweeg om meer in te slaan om reiskosten te dekken.";
        return "";
    }

    // Tijdelijke helper totdat we Google Places integreren
    private List<StoreTemplate> GetNearbyStoreTemplates(Location userLoc) => new() {
        new() { Chain="Albert Heijn", Country="NL", City="Lokaal", Lat=userLoc.Lat+0.02, Lng=userLoc.Lng+0.01 },
        new() { Chain="Jumbo", Country="NL", City="Lokaal", Lat=userLoc.Lat-0.01, Lng=userLoc.Lng+0.02 },
        new() { Chain="Aldi", Country="DE", City="Grens", Lat=userLoc.Lat+0.15, Lng=userLoc.Lng+0.15 },
        new() { Chain="Lidl", Country="BE", City="Grens", Lat=userLoc.Lat+0.12, Lng=userLoc.Lng-0.10 }
    };
}

public class StoreTemplate
{
    public string Chain { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
}