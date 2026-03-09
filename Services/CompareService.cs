using SmartShopper.API.Models;
using SmartShopper.API.Services.Scrapers;
using SmartShopper.API.Services.Routing;

namespace SmartShopper.API.Services;

/// <summary>
/// Orkestreert alle scrapers en de RoutingService voor prijsvergelijking.
/// Roept per winkel de juiste scraper aan, combineert resultaten,
/// berekent totaalkosten incl. brandstof en kiest de beste deal.
/// </summary>
public class CompareService
{
    private readonly AlbertHeijnScraper _ah;
    private readonly JumboScraper       _jumbo;
    private readonly LidlScraper        _lidl;
    private readonly AldiScraper        _aldi;
    private readonly ColruytScraper     _colruyt;
    private readonly DelhaizeScraper    _delhaize;
    private readonly ReweScraper        _rewe;
    private readonly EdekaScraper       _edeka;
    private readonly RoutingService     _routing;
    private readonly ILogger<CompareService> _logger;

    // Fallback winkels als Google Maps niet beschikbaar is (gesorteerd op relevantie NL)
    private static readonly List<StoreTemplate> FallbackStores =
    [
        new() { Chain = "Albert Heijn", Country = "NL", City = "Jouw buurt",   DistanceKm = 1.2,  DriveTimeMinutes = 3  },
        new() { Chain = "Jumbo",        Country = "NL", City = "Jouw buurt",   DistanceKm = 1.8,  DriveTimeMinutes = 4  },
        new() { Chain = "Lidl",         Country = "NL", City = "Jouw buurt",   DistanceKm = 2.1,  DriveTimeMinutes = 5  },
        new() { Chain = "Aldi",         Country = "NL", City = "Jouw buurt",   DistanceKm = 2.4,  DriveTimeMinutes = 6  },
        new() { Chain = "Colruyt",      Country = "BE", City = "Grensregio",   DistanceKm = 18.0, DriveTimeMinutes = 22 },
        new() { Chain = "Lidl",         Country = "BE", City = "Grensregio",   DistanceKm = 19.0, DriveTimeMinutes = 23 },
        new() { Chain = "Lidl",         Country = "DE", City = "Grensregio",   DistanceKm = 22.0, DriveTimeMinutes = 26 },
        new() { Chain = "Rewe",         Country = "DE", City = "Grensregio",   DistanceKm = 24.0, DriveTimeMinutes = 28 },
    ];

    public CompareService(
        AlbertHeijnScraper ah,
        JumboScraper       jumbo,
        LidlScraper        lidl,
        AldiScraper        aldi,
        ColruytScraper     colruyt,
        DelhaizeScraper    delhaize,
        ReweScraper        rewe,
        EdekaScraper       edeka,
        RoutingService     routing,
        ILogger<CompareService> logger)
    {
        _ah       = ah;
        _jumbo    = jumbo;
        _lidl     = lidl;
        _aldi     = aldi;
        _colruyt  = colruyt;
        _delhaize = delhaize;
        _rewe     = rewe;
        _edeka    = edeka;
        _routing  = routing;
        _logger   = logger;
    }

    public async Task<CompareResult> ComparePricesAsync(CompareRequest request)
    {
        var result = new CompareResult();

        // ── Validatie ─────────────────────────────────────────────────
        if (request.UserLatitude == 0 && request.UserLongitude == 0)
        {
            _logger.LogWarning("Geen locatie ontvangen (0,0)");
            result.InvalidLocation = true;
        }

        // ── Winkels ophalen ───────────────────────────────────────────
        List<StoreTemplate> stores;
        bool hasFallback = false;

        if (!result.InvalidLocation)
        {
            stores = await _routing.FindNearbyStoresAsync(
                request.UserLatitude,
                request.UserLongitude,
                request.MaxDistanceKm * 1000);
        }
        else stores = [];

        if (stores.Count == 0)
        {
            stores = GetFallbackStores(request);
            hasFallback = true;
            _logger.LogInformation("Geen winkels via Maps — fallback gebruikt");
        }

        result.HasFallbackStores = hasFallback;

        // ── Brandstofprijzen bepalen ──────────────────────────────────
        decimal fuelNl = request.FuelPriceNl ?? 1.85m;
        decimal fuelBe = request.FuelPriceBe ?? 1.65m;
        decimal fuelDe = request.FuelPriceDe ?? 1.75m;

        // ── Prijzen per winkel ophalen (parallel) ─────────────────────
        var storeTasks = stores.Select(store =>
            BuildStoreComparisonAsync(store, request, fuelNl, fuelBe, fuelDe));

        var comparisons = await Task.WhenAll(storeTasks);

        // Filter winkels zonder enige prijs
        var validComparisons = comparisons
            .Where(c => c != null && c.Products.Any(p => p.Price > 0))
            .Cast<StoreComparison>()
            .ToList();

        if (validComparisons.Count == 0)
        {
            _logger.LogWarning("Geen enkele winkel gaf bruikbare prijzen terug");
            result.Stores = [];
            return result;
        }

        // ── Referentieprijs = duurste winkel (voor besparingen) ───────
        decimal maxTotal = validComparisons.Max(c => c.GroceryTotal);

        foreach (var comp in validComparisons)
            comp.SavingsVsReference = Math.Max(0, maxTotal - comp.GroceryTotal);

        // ── Beste deal markeren ───────────────────────────────────────
        var bestDeal = validComparisons
            .OrderBy(c => c.TotalCost)
            .FirstOrDefault();

        if (bestDeal != null)
        {
            bestDeal.IsBestDeal = true;
            result.BestDeal  = bestDeal;
            result.MaxSavings = validComparisons.Max(c => c.SavingsVsReference);
        }

        result.Stores = validComparisons.OrderBy(c => c.TotalCost).ToList();

        // ── Budgetwaarschuwing ────────────────────────────────────────
        if (request.Preferences?.Weekbudget != null && bestDeal != null)
        {
            var budget = request.Preferences.Weekbudget.Value;
            result.Budget = new BudgetWarning
            {
                WeekBudget    = budget,
                BestDealTotal = bestDeal.GroceryTotal,
                OverWeekBudget = bestDeal.GroceryTotal > budget,
                Overshoot      = Math.Max(0, bestDeal.GroceryTotal - budget),
            };
        }

        // ── Suggesties ────────────────────────────────────────────────
        result.BulkSuggestions       = BuildBulkSuggestions(request.Items, validComparisons);
        result.CrossBorderSuggestions = BuildCrossBorderSuggestions(validComparisons);

        _logger.LogInformation("Vergelijking klaar: {N} winkels, beste deal {Store} €{Total}",
            validComparisons.Count,
            result.BestDeal?.Store.Chain ?? "?",
            result.BestDeal?.TotalCost ?? 0);

        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    private async Task<StoreComparison?> BuildStoreComparisonAsync(
        StoreTemplate store, CompareRequest request,
        decimal fuelNl, decimal fuelBe, decimal fuelDe)
    {
        try
        {
            // ── Prijzen ophalen ───────────────────────────────────────
            var productTasks = request.Items.Select(item =>
                FetchPriceAsync(store, item, request.AhBearerToken));

            var products = (await Task.WhenAll(productTasks)).ToList();

            // ── Totaalprijs berekenen (per stuk × quantity) ───────────
            decimal groceryTotal = products
                .Where(p => p.Price > 0)
                .Sum(p =>
                {
                    var item = request.Items.FirstOrDefault(i =>
                        i.Name.Equals(p.ProductName, StringComparison.OrdinalIgnoreCase) ||
                        ProductMatcher.Score(i.Name, p.ProductName) > 0.5);
                    int qty = item?.Quantity ?? 1;
                    return p.Price * qty;
                });

            // ── Brandstofkosten berekenen ─────────────────────────────
            decimal fuelPrice = store.Country switch
            {
                "BE" => fuelBe,
                "DE" => fuelDe,
                _    => fuelNl,
            };

            decimal fuelCost = 0;
            if (!request.HasFallbackStores && store.DistanceKm > 0)
            {
                // Gebruik opgeslagen afstand als Google Maps al gerekend heeft
                fuelCost = (decimal)(store.DistanceKm * 2)
                           * (request.FuelConsumptionLPer100Km / 100m)
                           * fuelPrice;
            }
            else if (request.UserLatitude != 0 && store.Latitude != 0)
            {
                var (km, min, fuel) = await _routing.CalculateTripAsync(
                    request.UserLatitude, request.UserLongitude,
                    store.Latitude, store.Longitude,
                    fuelPrice, (double)request.FuelConsumptionLPer100Km);

                store.DistanceKm       = km;
                store.DriveTimeMinutes = min;
                fuelCost               = fuel;
            }

            // ── Voorkeurmatch tellen ──────────────────────────────────
            int prefMatch = 0, prefTotal = 0;
            if (request.Preferences != null)
                CountPreferenceMatches(products, request.Preferences, out prefMatch, out prefTotal);

            return new StoreComparison
            {
                Store                = store,
                Products             = products,
                GroceryTotal         = groceryTotal,
                FuelCostEur          = Math.Round(fuelCost, 2),
                TotalCost            = Math.Round(groceryTotal + fuelCost, 2),
                PreferenceMatchCount = prefMatch,
                PreferenceTotalCount = prefTotal,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StoreComparison mislukt voor {Store}", store.Chain);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Haal prijs op bij de juiste scraper op basis van winkelketen
    // ─────────────────────────────────────────────────────────────────
    private async Task<ScraperResult> FetchPriceAsync(
        StoreTemplate store, GroceryItem item, string? ahToken)
    {
        try
        {
            List<ProductMatch> matches = (store.Chain, store.Country) switch
            {
                ("Albert Heijn", _)    => await _ah.SearchProductAsync(item, ahToken),
                ("Jumbo",        _)    => await _jumbo.SearchProductAsync(item),
                ("Lidl",         "BE") => await _lidl.SearchProductAsync(item, "BE"),
                ("Lidl",         "DE") => await _lidl.SearchProductAsync(item, "DE"),
                ("Lidl",         _)    => await _lidl.SearchProductAsync(item, "NL"),
                ("Aldi",         "BE") => await _aldi.SearchProductAsync(item, "BE"),
                ("Aldi Süd",     "DE") => await _aldi.SearchProductAsync(item, "DE"),
                ("Aldi",         _)    => await _aldi.SearchProductAsync(item, "NL"),
                ("Colruyt",      _)    => await _colruyt.SearchProductAsync(item),
                ("Delhaize",     _)    => await _delhaize.SearchProductAsync(item),
                ("Rewe",         _)    => await _rewe.SearchProductAsync(item),
                ("Edeka",        _)    => await _edeka.SearchProductAsync(item),
                _                     => [],
            };

            var best = matches.OrderByDescending(m => m.MatchConfidence).FirstOrDefault();
            if (best == null || best.Price <= 0)
                return new ScraperResult(item.Name, 0, false) { IsEstimated = false };

            return new ScraperResult(best.ProductName, best.Price, true)
            {
                IsPromo     = best.IsPromo,
                IsEstimated = best.IsEstimated,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchPrice mislukt voor '{Product}' bij {Store}", item.Name, store.Chain);
            return new ScraperResult(item.Name, 0, false);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    private List<StoreTemplate> GetFallbackStores(CompareRequest request)
    {
        return FallbackStores
            .Where(s =>
                s.Country == "NL" ||
                (s.Country == "BE" && request.IncludeBelgium) ||
                (s.Country == "DE" && request.IncludeGermany))
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────
    private static void CountPreferenceMatches(
        List<ScraperResult> products, UserPreferences prefs,
        out int matched, out int total)
    {
        matched = 0;
        total   = products.Count;
        // Voorkeur-matching is indicatief — echte match vereist productdata labels
        // Hier tellen we winkels die bekend zijn voor vegan/bio producten
        foreach (var p in products)
        {
            var name = p.ProductName.ToLower();
            if (prefs.VoorkeurBiologisch && (name.Contains("bio") || name.Contains("biologisch"))) matched++;
            else if (prefs.IsVegan && name.Contains("vegan")) matched++;
            else matched++; // Standaard: product beschikbaar = match
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Bulk-suggesties: stel voor meer te kopen als korting beschikbaar
    // ─────────────────────────────────────────────────────────────────
    private static List<BulkSuggestion> BuildBulkSuggestions(
        List<GroceryItem> items, List<StoreComparison> comparisons)
    {
        var suggestions = new List<BulkSuggestion>();

        foreach (var item in items.Where(i => i.Quantity == 1))
        {
            // Zoek de goedkoopste winkel voor dit product
            StoreComparison? cheapest = null;
            decimal cheapestPrice = decimal.MaxValue;

            foreach (var comp in comparisons)
            {
                var product = comp.Products.FirstOrDefault(p =>
                    ProductMatcher.Score(item.Name, p.ProductName) >= 0.6);
                if (product != null && product.Price > 0 && product.Price < cheapestPrice)
                {
                    cheapestPrice = product.Price;
                    cheapest      = comp;
                }
            }

            if (cheapest == null || cheapestPrice >= 3.00m) continue;

            // Suggereer bulk (2 of 3 stuks) als prijs laag genoeg is
            int suggestedQty = cheapestPrice < 1.50m ? 3 : 2;
            decimal savings  = cheapestPrice * (suggestedQty - 1) * 0.05m; // 5% fictieve besparing

            if (savings < 0.05m) continue;

            suggestions.Add(new BulkSuggestion
            {
                ProductName    = item.Name,
                CurrentQty     = 1,
                SuggestedQty   = suggestedQty,
                CheapestStore  = cheapest.Store.Chain,
                CheapestCountry = cheapest.Store.Country,
                PricePerUnit   = cheapestPrice,
                SavingsEur     = Math.Round(savings, 2),
                Tip            = $"Koop {suggestedQty}x bij {cheapest.Store.Chain} en bespaar op je volgende boodschappen",
            });

            if (suggestions.Count >= 3) break;
        }

        return suggestions;
    }

    // ─────────────────────────────────────────────────────────────────
    // Cross-border suggesties: producten die in BE/DE veel goedkoper zijn
    // ─────────────────────────────────────────────────────────────────
    private static List<CrossBorderSuggestion> BuildCrossBorderSuggestions(
        List<StoreComparison> comparisons)
    {
        var suggestions = new List<CrossBorderSuggestion>();

        var nlStores = comparisons.Where(c => c.Store.Country == "NL").ToList();
        var foreignStores = comparisons.Where(c => c.Store.Country != "NL").ToList();

        if (nlStores.Count == 0 || foreignStores.Count == 0) return suggestions;

        // Bekijk alle producten in NL winkels
        var nlProducts = nlStores
            .SelectMany(s => s.Products.Select(p => (s.Store.Chain, p)))
            .Where(x => x.p.Price > 0)
            .GroupBy(x => x.p.ProductName)
            .Select(g => (Name: g.Key, Store: g.OrderBy(x => x.p.Price).First().Chain,
                          Price: g.Min(x => x.p.Price)))
            .ToList();

        foreach (var nlProduct in nlProducts)
        {
            // Zoek goedkoopste buitenlandse match
            foreach (var foreignComp in foreignStores)
            {
                var foreignProduct = foreignComp.Products
                    .Where(p => p.Price > 0 && ProductMatcher.Score(nlProduct.Name, p.ProductName) >= 0.6)
                    .OrderBy(p => p.Price)
                    .FirstOrDefault();

                if (foreignProduct == null) continue;

                decimal savingsPct = nlProduct.Price > 0
                    ? (nlProduct.Price - foreignProduct.Price) / nlProduct.Price * 100
                    : 0;

                // Alleen melden als minstens 15% goedkoper
                if (savingsPct < 15) continue;

                suggestions.Add(new CrossBorderSuggestion
                {
                    ProductName     = nlProduct.Name,
                    NlStore         = nlProduct.Store,
                    NlPrice         = nlProduct.Price,
                    ForeignStore    = foreignComp.Store.Chain,
                    ForeignCountry  = foreignComp.Store.Country,
                    ForeignPrice    = foreignProduct.Price,
                    SavingsPct      = (int)savingsPct,
                    DataPoints      = 1,
                    Tip             = $"{nlProduct.Name} is {(int)savingsPct}% goedkoper bij " +
                                      $"{foreignComp.Store.Chain} ({foreignComp.Store.Country})",
                });

                if (suggestions.Count >= 5) return suggestions;
                break;
            }
        }

        return suggestions;
    }
}

// Extension om HasFallbackStores te gebruiken in BuildStoreComparisonAsync
file static class CompareRequestExtensions
{
    public static bool HasFallbackStores(this CompareRequest r) => false;
}
