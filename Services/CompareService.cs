using System.Text.Json;
using System.Net.Http.Json;
using System.Globalization;
using System.Collections.Concurrent;
using SmartShopper.API.Models;
using SmartShopper.API.Services.Scrapers;
using SmartShopper.API.Services.Routing;

namespace SmartShopper.API.Services;

public class CompareService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<CompareService> _logger;
    private readonly AlbertHeijnScraper _ahScraper;
    private readonly JumboScraper _jumboScraper;
    private readonly LidlScraper _lidlScraper;
    private readonly AldiScraper _aldiScraper;
    private readonly ReweScraper _reweScraper;
    private readonly EdekaScraper _edekaScraper;
    private readonly ColruytScraper _colruytScraper;
    private readonly DelhaizeScraper _delhaizeScraper;

    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private readonly string? _mapsKey;

    // Prijscache: sleutel = "productnaam|winkel|land"
    private static readonly ConcurrentDictionary<string, (List<ProductMatch> matches, DateTime cachedAt, bool isPromo)> _cache = new();
    private static readonly TimeSpan CACHE_LIVE = TimeSpan.FromHours(2);
    private static readonly TimeSpan CACHE_PROMO = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CACHE_ESTIMATED = TimeSpan.FromHours(24);

    public CompareService(
        HttpClient http, IConfiguration config, ILogger<CompareService> logger,
        AlbertHeijnScraper ah, JumboScraper jumbo, LidlScraper lidl,
        AldiScraper aldi, ReweScraper rewe, EdekaScraper edeka,
        ColruytScraper colruyt, DelhaizeScraper delhaize)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _ahScraper = ah;
        _jumboScraper = jumbo;
        _lidlScraper = lidl;
        _aldiScraper = aldi;
        _reweScraper = rewe;
        _edekaScraper = edeka;
        _colruytScraper = colruyt;
        _delhaizeScraper = delhaize;

        _supabaseUrl = config["Supabase:Url"] ?? "";
        _supabaseKey = config["Supabase:ServiceKey"] ?? config["Supabase:AnonKey"] ?? "";
        _mapsKey = config["GoogleMaps:ApiKey"];
    }

    public async Task<CompareResult> ComparePricesAsync(CompareRequest request)
    {
        var result = new CompareResult();

        // ─── 1. Winkels ophalen ──────────────────────────────────────
        List<StoreTemplate> nearbyStores;
        bool hasMapsKey = !string.IsNullOrEmpty(_mapsKey) && !_mapsKey.StartsWith("ZET_HIER");

        if (hasMapsKey)
        {
            nearbyStores = await GetNearbyStoresFromGoogle(
                request.UserLatitude, request.UserLongitude,
                request.MaxDistanceKm, request.IncludeBelgium, request.IncludeGermany);
        }
        else
        {
            // Fallback: vaste winkels — duidelijk markeren in resultaat
            _logger.LogWarning("Geen Google Maps key — fallback stores gebruikt");
            nearbyStores = GetFallbackStores(request.UserLatitude, request.UserLongitude,
                request.IncludeBelgium, request.IncludeGermany);
            result.HasFallbackStores = true;
        }

        if (!nearbyStores.Any())
        {
            _logger.LogWarning("Geen winkels gevonden voor {Lat},{Lng}",
                request.UserLatitude, request.UserLongitude);
            return result;
        }

        // Preferred stores eerst
        if (request.PreferredStores.Any())
            nearbyStores = nearbyStores
                .OrderBy(s => request.PreferredStores.Contains(s.Chain) ? 0 : 1)
                .ThenBy(s => s.DistanceKm)
                .ToList();

        // ─── 2. Per winkel producten ophalen (parallel) ──────────────
        var comparisonTasks = nearbyStores.Select(store => ScrapeSingleStoreAsync(store, request));
        var allComparisons = await Task.WhenAll(comparisonTasks);
        var validComparisons = allComparisons.Where(c => c.GroceryTotal > 0).ToList();

        if (!validComparisons.Any()) return result;

        // ─── 3. Sorteren + winnaar bepalen ───────────────────────────
        decimal referenceTotal = validComparisons.Max(c => c.TotalCost);

        result.Stores = request.Preferences?.PrijsPrioriteit == "kwaliteit"
            ? validComparisons
                .OrderByDescending(c => c.PreferenceMatchCount)
                .ThenBy(c => c.TotalCost)
                .ToList()
            : validComparisons
                .OrderBy(c => c.TotalCost)
                .ToList();

        foreach (var store in result.Stores)
            store.SavingsVsReference = Math.Max(0, referenceTotal - store.TotalCost);

        result.Stores.First().IsBestDeal = true;
        result.BestDeal = result.Stores.First();
        result.MaxSavings = result.Stores.First().SavingsVsReference;

        // ─── 4. Budget waarschuwing ──────────────────────────────────
        if (request.Preferences?.Weekbudget.HasValue == true && request.Preferences.BudgetWaarschuwing)
        {
            var budget = request.Preferences.Weekbudget.Value;
            var best = result.BestDeal.TotalCost;
            if (best > budget)
                result.Budget = new BudgetWarning
                {
                    OverWeekBudget = true,
                    WeekBudget = budget,
                    BestDealTotal = best,
                    Overshoot = Math.Round(best - budget, 2)
                };
        }

        // ─── 5. Suggesties genereren ─────────────────────────────────
        result.BulkSuggestions = GenerateBulkSuggestions(validComparisons, request.Items);
        result.CrossBorderSuggestions = await GetCrossBorderSuggestionsAsync(request.Items);

        // ─── 6. Prijzen opslaan in Supabase (fire-and-forget) ────────
        foreach (var store in result.Stores.Take(5))
            foreach (var (product, item) in store.Products.Zip(request.Items))
                if (product.Success && !product.IsEstimated)
                    _ = Task.Run(() => SavePriceToSupabase(
                        item.Name, product.ProductName,
                        store.Store.Chain, store.Store.Country, product.Price, product.IsPromo));

        return result;
    }

    // ─── Per winkel scrapen ──────────────────────────────────────────
    private async Task<StoreComparison> ScrapeSingleStoreAsync(StoreTemplate store, CompareRequest request)
    {
        var comparison = new StoreComparison { Store = store };
        decimal groceryTotal = 0;
        int preferenceMatches = 0;

        foreach (var item in request.Items)
        {
            var matches = await GetMatchesWithCache(item, store.Chain, store.Country, request.AhBearerToken);
            var best = SelectBestMatch(matches, item, request.Preferences);

            var scraperResult = new ScraperResult(best.ProductName, best.Price, best.Price > 0)
            {
                IsPromo = best.IsPromo,
                IsEstimated = best.IsEstimated,
            };
            comparison.Products.Add(scraperResult);

            if (scraperResult.Success)
            {
                groceryTotal += scraperResult.Price * item.Quantity;
                if (request.Preferences != null && MatchesPreferences(best, request.Preferences))
                    preferenceMatches++;
            }
        }

        comparison.GroceryTotal = groceryTotal;
        comparison.FuelCostEur = CalculateFuelCosts(store.DistanceKm, request.FuelConsumptionLPer100Km,
            store.Country, request);
        comparison.TotalCost = groceryTotal + comparison.FuelCostEur;
        comparison.PreferenceMatchCount = preferenceMatches;
        comparison.PreferenceTotalCount = request.Items.Count;
        return comparison;
    }

    // ─── Cache met onderscheid live/promo/schatting ──────────────────
    private async Task<List<ProductMatch>> GetMatchesWithCache(
        GroceryItem item, string store, string country, string? ahToken)
    {
        string cacheKey = $"{item.Name.ToLower().Trim()}|{store}|{country}";

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            var ttl = cached.matches.Any(m => m.IsEstimated) ? CACHE_ESTIMATED
                    : cached.isPromo ? CACHE_PROMO
                    : CACHE_LIVE;

            if (DateTime.UtcNow - cached.cachedAt < ttl)
            {
                // Markeer als uit cache
                cached.matches.ForEach(m => m.FromCache = true);
                return cached.matches;
            }
        }

        List<ProductMatch> matches;
        try
        {
            matches = await ScrapeForStore(item, store, country, ahToken);
        }
        catch (Exception ex)
        {
            // Circuit breaker open of andere Polly exception — direct naar schatting
            _logger.LogDebug(ex, "Scraper exception voor {Store}/{Product}, gebruik schatting", store, item.Name);
            matches = [];
        }

        // Fallback naar Open Food Facts als leeg of geen prijs
        if (!matches.Any() || matches.All(m => m.Price <= 0))
            matches = [await EstimateViaOFF(item, store, country)];

        bool hasPromo = matches.Any(m => m.IsPromo);
        if (matches.Any(m => m.Price > 0))
            _cache[cacheKey] = (matches, DateTime.UtcNow, hasPromo);

        return matches;
    }

    private async Task<List<ProductMatch>> ScrapeForStore(
        GroceryItem item, string store, string country, string? ahToken)
    {
        return store switch
        {
            "Albert Heijn"              => await _ahScraper.SearchProductAsync(item, ahToken),
            "Jumbo"                     => await _jumboScraper.SearchProductAsync(item),
            "Lidl"                      => await _lidlScraper.SearchProductAsync(item, country),
            "Aldi"                      => await _aldiScraper.SearchProductAsync(item, country == "BE" ? "BE" : "NL"),
            "Aldi Süd"                  => await _aldiScraper.SearchProductAsync(item, "DE"),
            "Rewe"                      => await _reweScraper.SearchProductAsync(item),
            "Edeka"                     => await _edekaScraper.SearchProductAsync(item),
            "Colruyt"                   => await _colruytScraper.SearchProductAsync(item),
            "Delhaize"                  => await _delhaizeScraper.SearchProductAsync(item),
            // Ketens zonder eigen scraper: gebruik Aldi/Lidl als proxy (vergelijkbaar prijsniveau)
            "Netto"                     => await _aldiScraper.SearchProductAsync(item, "DE"),
            "Kaufland"                  => await _aldiScraper.SearchProductAsync(item, "DE"),
            "Carrefour"                 => await _colruytScraper.SearchProductAsync(item),
            "Plus" or "Dirk"
                or "Spar" or "Hoogvliet"
                or "Coop"               => await _jumboScraper.SearchProductAsync(item),
            _                           => []
        };
    }

    // ─── Voorkeuren matching ─────────────────────────────────────────
    private static ProductMatch SelectBestMatch(List<ProductMatch> matches, GroceryItem item, UserPreferences? prefs)
    {
        if (!matches.Any())
            return new ProductMatch { ProductName = item.Name, Price = 0, IsEstimated = true };

        if (prefs == null) return matches.OrderByDescending(m => m.MatchConfidence).First();

        return matches
            .OrderByDescending(m => PreferenceScore(m, item, prefs))
            .ThenBy(m => m.Price)
            .First();
    }

    private static int PreferenceScore(ProductMatch m, GroceryItem item, UserPreferences prefs)
    {
        int score = 0;
        if (prefs.IsVegan && m.IsVegan) score += 10;
        if (prefs.VoorkeurBiologisch && m.IsBiologisch) score += 8;

        var category = GuessCategory(item.Name);
        if (prefs.Merkvoorkeur.TryGetValue(category, out var voorkeur))
        {
            if (voorkeur == "a-merk" && m.IsAMerk) score += 6;
            if (voorkeur == "huismerk" && m.IsHuisMerk) score += 6;
            if (voorkeur == "maakt-niet-uit") score += 3;
        }
        if (prefs.FavorieteWinkels.Contains(m.StoreName)) score += 4;
        return score;
    }

    private static bool MatchesPreferences(ProductMatch m, UserPreferences prefs)
    {
        if (prefs.IsVegan && !m.IsVegan) return false;
        if (prefs.VoorkeurBiologisch && !m.IsBiologisch) return false;
        return true;
    }

    private static string GuessCategory(string productName)
    {
        var n = productName.ToLower();
        if (n.Contains("cola") || n.Contains("fanta") || n.Contains("frisdrank") || n.Contains("sap")) return "frisdrank";
        if (n.Contains("wasmiddel") || n.Contains("waspoeder") || n.Contains("ariel") || n.Contains("robijn")) return "wasmiddel";
        if (n.Contains("melk") || n.Contains("yoghurt") || n.Contains("kaas") || n.Contains("boter")) return "zuivel";
        if (n.Contains("brood") || n.Contains("boterham")) return "brood";
        if (n.Contains("vlees") || n.Contains("gehakt") || n.Contains("kipfilet")) return "vlees";
        if (n.Contains("chips") || n.Contains("koek") || n.Contains("nootjes")) return "snacks";
        if (n.Contains("koffie") || n.Contains("espresso")) return "koffie";
        if (n.Contains("schoonmaak") || n.Contains("reiniger")) return "schoonmaak";
        if (n.Contains("tandpasta") || n.Contains("shampoo") || n.Contains("douchegel")) return "personal";
        return "overig";
    }

    // ─── Bulk suggesties ─────────────────────────────────────────────
    private static List<BulkSuggestion> GenerateBulkSuggestions(
        List<StoreComparison> stores, List<GroceryItem> items)
    {
        var suggestions = new List<BulkSuggestion>();

        foreach (var item in items)
        {
            // Sla over als hoeveelheid al groot is
            if (item.Quantity >= 6) continue;

            // Goedkoopste winkel voor dit product
            var storeWithPrice = stores
                .Select(s =>
                {
                    var product = s.Products
                        .Zip(items)
                        .FirstOrDefault(p => p.Second.Id == item.Id || p.Second.Name == item.Name);
                    return (store: s, price: product.First?.Price ?? 0);
                })
                .Where(x => x.price > 0)
                .OrderBy(x => x.price)
                .ToList();

            if (storeWithPrice.Count < 2) continue;

            var cheapest = storeWithPrice.First();
            var nearest = stores.OrderBy(s => s.Store.DistanceKm).First();

            // Is de goedkoopste winkel niet de dichtstbijzijnde?
            if (cheapest.store.Store.Chain == nearest.Store.Chain) continue;

            decimal priceDiffPerUnit = (storeWithPrice.Last().price - cheapest.price);
            if (priceDiffPerUnit <= 0.30m) continue; // minder dan 30ct verschil, niet de moeite

            int suggestedQty = Math.Max(6, item.Quantity * 3);
            decimal totalSavings = priceDiffPerUnit * suggestedQty;

            if (totalSavings < 2.00m) continue; // alleen tonen als besparing > €2

            var flag = cheapest.store.Store.Country switch
            {
                "DE" => "🇩🇪",
                "BE" => "🇧🇪",
                _ => "🇳🇱"
            };

            suggestions.Add(new BulkSuggestion
            {
                ProductName = item.Name,
                CurrentQty = item.Quantity,
                SuggestedQty = suggestedQty,
                CheapestStore = cheapest.store.Store.Chain,
                CheapestCountry = cheapest.store.Store.Country,
                PricePerUnit = cheapest.price,
                SavingsEur = Math.Round(totalSavings, 2),
                Tip = $"{flag} Koop {suggestedQty}x {item.Name} bij {cheapest.store.Store.Chain} " +
                      $"en bespaar €{totalSavings:F2} t.o.v. je dichtstbijzijnde winkel"
            });
        }

        return suggestions.OrderByDescending(s => s.SavingsEur).Take(3).ToList();
    }

    // ─── Cross-border suggesties uit Supabase historische data ──────
    private async Task<List<CrossBorderSuggestion>> GetCrossBorderSuggestionsAsync(List<GroceryItem> items)
    {
        if (string.IsNullOrEmpty(_supabaseUrl)) return [];

        try
        {
            var suggestions = new List<CrossBorderSuggestion>();

            foreach (var item in items.Take(5))
            {
                var url = $"{_supabaseUrl}/rest/v1/rpc/get_cross_border_savings";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("apikey", _supabaseKey);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_supabaseKey}");
                req.Content = JsonContent.Create(new
                {
                    p_product_name = item.Name,
                    p_min_savings_pct = 10,
                    p_min_data_points = 3
                });

                var response = await _http.SendAsync(req);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in doc.RootElement.EnumerateArray())
                    {
                        int savingsPct = row.TryGetProperty("savings_pct", out var sp) ? sp.GetInt32() : 0;
                        if (savingsPct < 10) continue;

                        string foreignCountry = row.TryGetProperty("cheapest_country", out var cc) ? cc.GetString() ?? "DE" : "DE";
                        string flag = foreignCountry == "DE" ? "🇩🇪" : "🇧🇪";

                        suggestions.Add(new CrossBorderSuggestion
                        {
                            ProductName = item.Name,
                            NlPrice = row.TryGetProperty("nl_avg", out var nl) ? nl.GetDecimal() : 0,
                            ForeignStore = row.TryGetProperty("cheapest_store", out var cs) ? cs.GetString() ?? "" : "",
                            ForeignCountry = foreignCountry,
                            ForeignPrice = row.TryGetProperty("foreign_avg", out var fa) ? fa.GetDecimal() : 0,
                            SavingsPct = savingsPct,
                            DataPoints = row.TryGetProperty("data_points", out var dp) ? dp.GetInt32() : 0,
                            Tip = $"{flag} {item.Name} is gemiddeld {savingsPct}% goedkoper over de grens"
                        });
                    }
                }
            }

            return suggestions.OrderByDescending(s => s.SavingsPct).Take(3).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cross-border suggesties ophalen mislukt");
            return [];
        }
    }

    // ─── Open Food Facts fallback schatting ──────────────────────────
    private async Task<ProductMatch> EstimateViaOFF(GroceryItem item, string store, string country)
    {
        try
        {
            string lang = country == "DE" ? "de" : "nl";
            var url = $"https://world.openfoodfacts.org/cgi/search.pl" +
                      $"?search_terms={Uri.EscapeDataString(item.Name)}&search_simple=1" +
                      $"&action=process&json=1&page_size=3&lc={lang}&cc={country.ToLower()}";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SmartShopper/3.0");
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("products", out var products))
            {
                foreach (var p in products.EnumerateArray())
                {
                    var name = p.TryGetProperty("product_name_nl", out var nl) ? nl.GetString() :
                               p.TryGetProperty("product_name", out var pn) ? pn.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;

                    bool isBio = name.Contains("bio", StringComparison.OrdinalIgnoreCase);
                    bool isVegan = p.TryGetProperty("labels_tags", out var lbt) &&
                                   lbt.EnumerateArray().Any(l => l.GetString()?.Contains("vegan") == true);

                    string category = p.TryGetProperty("categories_tags", out var cats)
                        ? cats.EnumerateArray().FirstOrDefault().GetString() ?? "" : "";
                    decimal price = EstimatePriceFromCategory(category, store, country);

                    return new ProductMatch
                    {
                        StoreName = store, Country = country, ProductName = name,
                        Price = price, IsEstimated = true,
                        IsBiologisch = isBio, IsVegan = isVegan, MatchConfidence = 0.5
                    };
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "OFF fallback fout voor {Product}", item.Name); }

        return new ProductMatch
        {
            StoreName = store, Country = country, ProductName = item.Name,
            Price = EstimatePriceFromCategory("", store, country),
            IsEstimated = true, MatchConfidence = 0.3
        };
    }

    private static decimal EstimatePriceFromCategory(string category, string store, string country)
    {
        decimal basePrice = category.ToLower() switch
        {
            var c when c.Contains("beverages") || c.Contains("drink") || c.Contains("frisdrank") => 1.79m,
            var c when c.Contains("dairy") || c.Contains("zuivel") || c.Contains("milk") => 1.29m,
            var c when c.Contains("bread") || c.Contains("brood") => 2.49m,
            var c when c.Contains("meat") || c.Contains("vlees") => 4.99m,
            var c when c.Contains("snack") || c.Contains("chips") => 2.19m,
            var c when c.Contains("frozen") || c.Contains("diepvries") => 3.49m,
            var c when c.Contains("cleaning") || c.Contains("schoonmaak") => 3.99m,
            _ => 2.49m
        };
        decimal multiplier = (store, country) switch
        {
            ("Aldi", _) or ("Aldi Süd", _) => 0.82m,
            ("Lidl", _) => 0.85m,
            (_, "DE") => 0.88m,
            (_, "BE") => 0.92m,
            ("Albert Heijn", _) => 1.10m,
            _ => 1.00m
        };
        return Math.Round(basePrice * multiplier, 2);
    }

    // ─── Google Places ────────────────────────────────────────────────
    private async Task<List<StoreTemplate>> GetNearbyStoresFromGoogle(
        double lat, double lng, int radiusKm, bool includeBelgium, bool includeGermany)
    {
        string latStr = lat.ToString(CultureInfo.InvariantCulture);
        string lngStr = lng.ToString(CultureInfo.InvariantCulture);
        int radiusM = Math.Min(radiusKm * 1000, 50000);

        var allStores = new ConcurrentBag<StoreTemplate>();

        // NL ketens altijd zoeken; BE/DE ketens alleen als gewenst
        var terms = new List<string> { "Albert Heijn", "Jumbo", "Lidl", "Aldi", "Plus", "Dirk", "Spar" };
        if (includeGermany) terms.AddRange(["Rewe", "Edeka", "Aldi Süd", "Kaufland", "Netto"]);
        if (includeBelgium) terms.AddRange(["Colruyt", "Delhaize", "Carrefour", "Lidl"]);

        await Task.WhenAll(terms.Select(async term =>
        {
            try
            {
                // Gebruik 'name' ipv 'keyword' — preciezer voor winkelketens
                // Gebruik ook groter radius voor grensgebied zoeken
                var url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json" +
                          $"?location={latStr},{lngStr}&radius={radiusM}" +
                          $"&name={Uri.EscapeDataString(term)}&key={_mapsKey}";

                var resp = await _http.GetFromJsonAsync<JsonElement>(url);
                if (!resp.TryGetProperty("results", out var results)) return;

                foreach (var item in results.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var chain = MapGoogleNameToChain(name);
                    if (chain == "Onbekend") continue;

                    if (!item.TryGetProperty("geometry", out var geo)) continue;
                    var storeLat = geo.GetProperty("location").GetProperty("lat").GetDouble();
                    var storeLng = geo.GetProperty("location").GetProperty("lng").GetDouble();
                    var vicinity = item.TryGetProperty("vicinity", out var v) ? v.GetString() ?? "" : "";

                    var countryCode = DetectCountry(storeLat, storeLng);
                    if (countryCode == "BE" && !includeBelgium) continue;
                    if (countryCode == "DE" && !includeGermany) continue;

                    double dist = CalculateHaversineDistance(lat, lng, storeLat, storeLng);
                    if (dist > radiusKm) continue;

                    // Gebruik echte rijafstand van Google als beschikbaar (via distancematrix)
                    bool openNow = item.TryGetProperty("opening_hours", out var oh) &&
                                   oh.TryGetProperty("open_now", out var on) && on.GetBoolean();

                    allStores.Add(new StoreTemplate
                    {
                        Chain = chain,
                        City = ExtractCity(vicinity),
                        Address = vicinity,
                        Latitude = storeLat,
                        Longitude = storeLng,
                        DistanceKm = Math.Round(dist, 1),
                        DriveTimeMinutes = EstimateDriveTime(dist),
                        Country = countryCode,
                        OpenNow = openNow,
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Places fout voor {Term}", term); }
        }));

        return allStores
            // Dedup: zelfde keten op nagenoeg dezelfde locatie (100m radius)
            .GroupBy(s => $"{s.Chain}|{Math.Round(s.Latitude, 3)}|{Math.Round(s.Longitude, 3)}")
            .Select(g => g.OrderBy(s => s.DistanceKm).First())
            .OrderBy(s => s.DistanceKm)
            .Take(20)
            .ToList();
    }

    // ─── Fallback stores (geen Google Maps key) ────────────────────
    // Gebruikt wanneer er geen Google Maps API key is geconfigureerd.
    // Genereert realistische winkels rondom de gebruikerslocatie.
    private static List<StoreTemplate> GetFallbackStores(
        double lat, double lng, bool includeBE, bool includeDE)
    {
        var userCountry = DetectCountry(lat, lng);

        // NL winkels: altijd tonen, ongeacht of gebruiker in BE/NL zit
        var stores = new List<StoreTemplate>
        {
            new() { Chain="Albert Heijn", City="Dichtbij (NL)", Country="NL",
                    DistanceKm=2.5, DriveTimeMinutes=7, OpenNow=true,
                    Latitude=lat + 0.015, Longitude=lng + 0.010 },
            new() { Chain="Jumbo", City="Dichtbij (NL)", Country="NL",
                    DistanceKm=3.8, DriveTimeMinutes=10, OpenNow=true,
                    Latitude=lat + 0.020, Longitude=lng - 0.010 },
            new() { Chain="Lidl", City="Dichtbij (NL)", Country="NL",
                    DistanceKm=4.2, DriveTimeMinutes=12, OpenNow=true,
                    Latitude=lat + 0.025, Longitude=lng + 0.005 },
            new() { Chain="Aldi", City="Dichtbij (NL)", Country="NL",
                    DistanceKm=5.1, DriveTimeMinutes=14, OpenNow=true,
                    Latitude=lat + 0.030, Longitude=lng - 0.015 },
        };

        // BE winkels: toon ook als gebruiker in BE woont of includeBE = true
        if (includeBE || userCountry == "BE")
        {
            stores.Add(new() { Chain="Colruyt", City="Dichtbij (BE)", Country="BE",
                                DistanceKm=userCountry == "BE" ? 2.0 : 12.0,
                                DriveTimeMinutes=userCountry == "BE" ? 6 : 18, OpenNow=true,
                                Latitude=lat - 0.010, Longitude=lng - 0.020 });
            stores.Add(new() { Chain="Lidl", City="Dichtbij (BE)", Country="BE",
                                DistanceKm=userCountry == "BE" ? 3.5 : 14.0,
                                DriveTimeMinutes=userCountry == "BE" ? 10 : 20, OpenNow=true,
                                Latitude=lat - 0.015, Longitude=lng + 0.025 });
        }

        if (includeDE || userCountry == "DE")
        {
            stores.Add(new() { Chain="Aldi Süd", City="Dichtbij (DE)", Country="DE",
                                DistanceKm=userCountry == "DE" ? 2.5 : 20.0,
                                DriveTimeMinutes=userCountry == "DE" ? 7 : 25, OpenNow=true,
                                Latitude=lat + 0.005, Longitude=lng + 0.150 });
            stores.Add(new() { Chain="Rewe", City="Dichtbij (DE)", Country="DE",
                                DistanceKm=userCountry == "DE" ? 3.0 : 22.0,
                                DriveTimeMinutes=userCountry == "DE" ? 8 : 28, OpenNow=true,
                                Latitude=lat - 0.005, Longitude=lng + 0.160 });
        }

        return stores.OrderBy(s => s.DistanceKm).ToList();
    }

    // ─── Supabase prijsopslag ─────────────────────────────────────────
    private async Task SavePriceToSupabase(
        string searchQuery, string productName, string store, string country,
        decimal price, bool isPromo)
    {
        if (string.IsNullOrEmpty(_supabaseUrl)) return;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("apikey", _supabaseKey);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_supabaseKey}");
            client.DefaultRequestHeaders.Add("Prefer", "return=representation");

            // Upsert product
            var productResp = await client.PostAsJsonAsync(
                $"{_supabaseUrl}/rest/v1/products?on_conflict=name", new
                {
                    name = productName,
                    search_query = searchQuery,
                    updated_at = DateTime.UtcNow,
                });

            var productJson = await productResp.Content.ReadAsStringAsync();
            using var pdoc = JsonDocument.Parse(productJson);
            if (pdoc.RootElement.ValueKind != JsonValueKind.Array || pdoc.RootElement.GetArrayLength() == 0) return;

            var productId = pdoc.RootElement[0].TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(productId)) return;

            // Upsert prijs (per dag uniek)
            await client.PostAsJsonAsync(
                $"{_supabaseUrl}/rest/v1/prices?on_conflict=product_id,store,country", new
                {
                    product_id = productId,
                    store,
                    country,
                    price,
                    is_promo = isPromo,
                    scraped_at = DateTime.UtcNow,
                    source = "live_scrape",
                });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Supabase opslaan mislukt"); }
    }

    // ─── Helpers ─────────────────────────────────────────────────────
    private static string DetectCountry(double lat, double lng)
    {
        // Nauwkeurige landgrenzen voor NL/BE/DE grensgebied
        // Getest met: Maastricht (50.85,5.69)=NL, Genk (50.96,5.50)=BE,
        //             Aachen (50.77,6.08)=DE, Hasselt (50.93,5.33)=BE,
        //             Amsterdam (52.37,4.89)=NL, Antwerpen (51.22,4.40)=BE

        // Midden/Noord NL
        if (lat >= 52.00 && lng >= 3.36 && lng <= 7.22) return "NL";
        // Zuid-Limburg NL: Maastricht e.o. (lng >= 5.65 is duidelijk NL)
        if (lat >= 50.75 && lat < 52.00 && lng >= 5.65 && lng < 6.00) return "NL";
        // Aachen/Duitsland regio: lng >= 6.00 onder 52N
        if (lat >= 50.75 && lat < 52.00 && lng >= 6.00 && lng <= 7.22) return "DE";
        // Noord-Brabant / Zeeland NL (lat 51.30-52, lng 4.50-5.65)
        if (lat >= 51.30 && lat < 52.00 && lng >= 4.50 && lng < 5.65) return "NL";
        // Maastricht-corridor: smal strookje lng 5.50-5.65, lat 50.75-51.30
        // Maastricht (50.85) = NL, Lanaken (50.88, lng 5.63) = BE
        if (lat >= 50.75 && lat < 51.30 && lng >= 5.50 && lng < 5.65)
            return lat >= 50.84 && lng >= 5.67 ? "NL" : "BE";
        // Alles met lng < 5.50 en lat 50.75-51.30 = BE (Genk 5.50, Hasselt 5.33, Luik 5.57 maar lat 50.63)
        if (lat >= 50.75 && lat < 51.30 && lng >= 3.36 && lng < 5.50) return "BE";
        // Antwerpen regio BE
        if (lat >= 51.10 && lat < 51.30 && lng >= 3.36 && lng < 4.50) return "BE";
        // Diep belgie
        if (lat >= 49.50 && lat < 50.75 && lng >= 2.54 && lng <= 6.40) return "BE";
        return "NL";
    }

    private static int EstimateDriveTime(double distKm) => Math.Max(2, (int)(distKm / 0.6));

    private static string MapGoogleNameToChain(string name)
    {
        var n = name.ToLower();
        if (n.Contains("albert heijn") || n.Contains("ah to go") || n.Contains("ah xl") || n.StartsWith("ah ")) return "Albert Heijn";
        if (n.Contains("jumbo")) return "Jumbo";
        // Aldi Süd check vóór gewone Aldi (anders matcht Aldi Süd op "aldi")
        if (n.Contains("aldi süd") || n.Contains("aldi sued") || n.Contains("aldi sud") || n.Contains("aldi syd")) return "Aldi Süd";
        if (n.Contains("aldi")) return "Aldi";
        if (n.Contains("lidl")) return "Lidl";
        if (n.Contains("rewe")) return "Rewe";
        if (n.Contains("edeka")) return "Edeka";
        if (n.Contains("kaufland")) return "Kaufland";
        if (n.Contains("netto")) return "Netto";
        if (n.Contains("colruyt")) return "Colruyt";
        if (n.Contains("delhaize") || n.Contains("ad delhaize")) return "Delhaize";
        if (n.Contains("carrefour") || n.Contains("carrefour market")) return "Carrefour";
        if (n.Contains("dirk van den broek") || n.Contains("dirk")) return "Dirk";
        if (n.StartsWith("plus ") || n == "plus") return "Plus";
        if (n.Contains("spar")) return "Spar";
        if (n.Contains("hoogvliet")) return "Hoogvliet";
        if (n.Contains("coop")) return "Coop";
        return "Onbekend";
    }

    private static string ExtractCity(string vicinity)
    {
        var parts = vicinity.Split(',');
        return parts.Length > 1 ? parts.Last().Trim() : vicinity.Trim();
    }

    private static decimal CalculateFuelCosts(
        double distKm, decimal consumptionL, string country, CompareRequest request)
    {
        decimal fuelPrice = country switch
        {
            "BE" => request.FuelPriceBe ?? 1.72m,
            "DE" => request.FuelPriceDe ?? 1.61m,
            _ => request.FuelPriceNl ?? 1.89m,
        };
        return Math.Round((decimal)(distKm * 2) * (consumptionL / 100m) * fuelPrice, 2);
    }

    private static double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
