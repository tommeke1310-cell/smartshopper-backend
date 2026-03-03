using System.Text.Json;
using System.Net.Http.Json;
using SmartShopper.API.Models;
using System.Globalization;

namespace SmartShopper.API.Services;

public class CompareService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<CompareService> _logger;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;


    public CompareService(HttpClient http, IConfiguration config, ILogger<CompareService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _supabaseUrl = config["Supabase:Url"] ?? "";
        _supabaseKey = config["Supabase:AnonKey"] ?? "";

        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("User-Agent", "SmartShopper-API/1.0");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<CompareResult> ComparePricesAsync(CompareRequest request)
    {
        var result = new CompareResult();

        var nearbyStores = await GetNearbyStoresFromGoogle(
            request.UserLatitude, request.UserLongitude,
            request.MaxDistanceKm, request.IncludeBelgium, request.IncludeGermany
        );

        if (!nearbyStores.Any())
        {
            _logger.LogWarning("Geen winkels gevonden voor {Lat},{Lng}", request.UserLatitude, request.UserLongitude);
            return result;
        }

        _logger.LogInformation("Gevonden winkels: {Stores}",
            string.Join(", ", nearbyStores.Select(s => $"{s.Chain} ({s.Country})")));

        var comparisonTasks = nearbyStores.Select(async store =>
        {
            var comparison = new StoreComparison { Store = store };
            decimal groceryTotal = 0;

            foreach (var item in request.Items)
            {
                var priceResult = await GetPriceFromDatabase(item.Name, store.Chain, store.Country);
                comparison.Products.Add(priceResult);
                if (priceResult.Success)
                    groceryTotal += priceResult.Price * item.Quantity;
            }

            comparison.GroceryTotal = groceryTotal;
            comparison.FuelCostEur = CalculateFuelCosts(store.DistanceKm, request.FuelConsumptionLPer100Km, store.Country);
            comparison.TotalCost = comparison.GroceryTotal + comparison.FuelCostEur;
            return comparison;
        });

        var allComparisons = await Task.WhenAll(comparisonTasks);

        // Referentie = duurste optie (zodat spaarbedragen altijd logisch zijn)
        decimal referenceTotal = allComparisons
            .Where(c => c.GroceryTotal > 0)
            .Max(c => c.TotalCost);

        result.Stores = allComparisons
            .Where(c => c.GroceryTotal > 0)
            .OrderBy(c => c.TotalCost)
            .ToList();

        foreach (var store in result.Stores)
            store.SavingsVsReference = Math.Max(0, referenceTotal - store.TotalCost);

        if (result.Stores.Any())
        {
            result.Stores.First().IsBestDeal = true;
            result.BestDeal = result.Stores.First();
            result.MaxSavings = result.Stores.First().SavingsVsReference;
        }

        return result;
    }

    // ─── SUPABASE PRIJSOPZOEKING ──────────────────────────────────

    private async Task<ScraperResult> GetPriceFromDatabase(string productQuery, string store, string country)
    {
        try
        {
            var url = $"{_supabaseUrl}/rest/v1/rpc/search_product_price";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("apikey", _supabaseKey);
            request.Headers.Add("Authorization", $"Bearer {_supabaseKey}");
            request.Content = JsonContent.Create(new
            {
                search_query = productQuery,
                store_name   = store,
                country_code = country
            });

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return await ScrapeLiveFallback(productQuery, store, country);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                decimal price = first.GetProperty("price").GetDecimal();
                string name   = first.TryGetProperty("product_name", out var n) ? n.GetString() ?? productQuery : productQuery;
                bool isPromo  = first.TryGetProperty("is_promo", out var p) && p.GetBoolean();

                _logger.LogInformation("DB: {Product} @ {Store} {Country} = €{Price}", name, store, country, price);
                return new ScraperResult(name, price, true) { IsPromo = isPromo };
            }

            // Niet in database → live scrapen
            return await ScrapeLiveFallback(productQuery, store, country);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB fout voor {Product} @ {Store}", productQuery, store);
            return new ScraperResult(productQuery, 0, false);
        }
    }

    // ─── LIVE SCRAPING FALLBACK ───────────────────────────────────

    private async Task<ScraperResult> ScrapeLiveFallback(string query, string store, string country)
    {
        _logger.LogInformation("Live fallback: {Product} @ {Store} {Country}", query, store, country);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");

            var result = store switch
            {
                "Albert Heijn" => await ScrapeAH(client, query),
                "Jumbo"        => await ScrapeJumbo(client, query),
                _              => new ScraperResult(query, 0, false)
            };

            // Als live scrape succesvol is, sla op in database
            if (result.Success)
                _ = Task.Run(() => SavePriceToDatabase(query, result.ProductName, store, country, result.Price));

            return result;
        }
        catch { return new ScraperResult(query, 0, false); }
    }

    private async Task<ScraperResult> ScrapeAH(HttpClient client, string query)
    {
        try
        {
            var url = $"https://www.ah.nl/zoeken/api/v1/search?query={Uri.EscapeDataString(query)}&page=0&size=3";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", "https://www.ah.nl/");
            req.Headers.Add("x-application", "ah-storelocator");

            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new ScraperResult(query, 0, false);

            var data = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!data.TryGetProperty("cards", out var cards)) return new ScraperResult(query, 0, false);

            foreach (var card in cards.EnumerateArray())
            {
                if (!card.TryGetProperty("products", out var products)) continue;
                foreach (var product in products.EnumerateArray())
                {
                    if (!product.TryGetProperty("price", out var priceObj)) continue;
                    decimal price = 0;
                    if (priceObj.TryGetProperty("now", out var now)) price = now.GetDecimal();
                    if (price <= 0) continue;
                    string title = product.TryGetProperty("title", out var t) ? t.GetString() ?? query : query;
                    return new ScraperResult(title, price, true);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "AH live scrape fout"); }
        return new ScraperResult(query, 0, false);
    }

    private async Task<ScraperResult> ScrapeJumbo(HttpClient client, string query)
    {
        try
        {
            var url = $"https://mobileapi.jumbo.com/v17/search?q={Uri.EscapeDataString(query)}&offset=0&limit=3";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-jumbo-client", "mobile-app");

            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new ScraperResult(query, 0, false);

            var data = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!data.TryGetProperty("products", out var pw) ||
                !pw.TryGetProperty("data", out var products)) return new ScraperResult(query, 0, false);

            foreach (var product in products.EnumerateArray())
            {
                decimal price = 0;
                if (product.TryGetProperty("prices", out var prices))
                {
                    if (prices.TryGetProperty("promotionalPrice", out var promo) &&
                        promo.TryGetProperty("amount", out var pa))
                        price = pa.GetDecimal() / 100m;
                    else if (prices.TryGetProperty("price", out var p) &&
                             p.TryGetProperty("amount", out var a))
                        price = a.GetDecimal() / 100m;
                }
                if (price <= 0) continue;
                string title = product.TryGetProperty("title", out var t) ? t.GetString() ?? query : query;
                return new ScraperResult(title, price, true);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Jumbo live scrape fout"); }
        return new ScraperResult(query, 0, false);
    }

    // ─── PRIJS OPSLAAN IN DATABASE ────────────────────────────────

    private async Task SavePriceToDatabase(string searchQuery, string productName, string store, string country, decimal price)
    {
        try
        {
            // Stap 1: Upsert product (op naam)
            var productUrl = $"{_supabaseUrl}/rest/v1/products?on_conflict=name";
            using var productReq = new HttpRequestMessage(HttpMethod.Post, productUrl);
            productReq.Headers.Add("apikey", _supabaseKey);
            productReq.Headers.Add("Authorization", $"Bearer {_supabaseKey}");
            productReq.Headers.Add("Prefer", "return=representation,resolution=merge-duplicates");
            productReq.Content = JsonContent.Create(new { name = productName, category = "Overig" });

            var productResp = await _http.SendAsync(productReq);
            var productJson = await productResp.Content.ReadAsStringAsync();
            using var productDoc = JsonDocument.Parse(productJson);

            string productId = "";
            if (productDoc.RootElement.ValueKind == JsonValueKind.Array && productDoc.RootElement.GetArrayLength() > 0)
                productId = productDoc.RootElement[0].GetProperty("id").GetString() ?? "";

            if (string.IsNullOrEmpty(productId)) return;

            // Stap 2: Upsert prijs
            var priceUrl = $"{_supabaseUrl}/rest/v1/prices?on_conflict=product_id,store,country";
            using var priceReq = new HttpRequestMessage(HttpMethod.Post, priceUrl);
            priceReq.Headers.Add("apikey", _supabaseKey);
            priceReq.Headers.Add("Authorization", $"Bearer {_supabaseKey}");
            priceReq.Headers.Add("Prefer", "resolution=merge-duplicates");
            priceReq.Content = JsonContent.Create(new
            {
                product_id = productId,
                store,
                country,
                price,
                scraped_at = DateTime.UtcNow
            });

            await _http.SendAsync(priceReq);
            _logger.LogInformation("Prijs opgeslagen: {Product} @ {Store} {Country} = €{Price}", productName, store, country, price);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prijs opslaan mislukt voor {Product}", productName);
        }
    }

    // ─── GOOGLE PLACES ────────────────────────────────────────────

    private async Task<List<StoreTemplate>> GetNearbyStoresFromGoogle(
        double lat, double lng, int radiusKm, bool includeBelgium, bool includeGermany)
    {
        var apiKey = _config["GoogleMaps:ApiKey"];
        string latStr = lat.ToString(CultureInfo.InvariantCulture);
        string lngStr = lng.ToString(CultureInfo.InvariantCulture);
        int radiusMeter = Math.Min(radiusKm * 1000, 50000); // max 50km voor Places API

        var allStores = new List<StoreTemplate>();

        // Zoek per keten apart — anders mist Google kleine vestigingen
        // want bij rankby=distance pakt hij maar 20 resultaten en mist hij Bunde/Meerssen
        string[] searchTerms = { "Albert Heijn", "Jumbo supermarkt", "Lidl", "Aldi", "DM drogerie" };

        try
        {
            using var mapsClient = new HttpClient();

            foreach (var term in searchTerms)
            {
                var url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json" +
                          $"?location={latStr},{lngStr}" +
                          $"&radius={radiusMeter}" +        // radius ipv rankby=distance → geeft meer resultaten
                          $"&keyword={Uri.EscapeDataString(term)}" +
                          $"&type=supermarket&key={apiKey}";

                try
                {
                    var response = await mapsClient.GetFromJsonAsync<JsonElement>(url);
                    if (!response.TryGetProperty("results", out var results)) continue;

                    foreach (var item in results.EnumerateArray())
                    {
                        string googleName = item.GetProperty("name").GetString() ?? "";
                        var storeLat = item.GetProperty("geometry").GetProperty("location").GetProperty("lat").GetDouble();
                        var storeLng = item.GetProperty("geometry").GetProperty("location").GetProperty("lng").GetDouble();
                        string vicinity = item.TryGetProperty("vicinity", out var v) ? v.GetString() ?? "" : "";

                        var chain = MapGoogleNameToChain(googleName);
                        if (chain == "Onbekend") continue;

                        string country = DetectCountry(storeLat, storeLng);
                        if (country == "BE" && !includeBelgium) continue;
                        if (country == "DE" && !includeGermany) continue;

                        double distanceKm = CalculateHaversineDistance(lat, lng, storeLat, storeLng);
                        if (distanceKm > radiusKm) continue;

                        allStores.Add(new StoreTemplate
                        {
                            Chain            = chain,
                            City             = ExtractCity(vicinity),
                            Address          = vicinity,
                            Latitude         = storeLat,
                            Longitude        = storeLng,
                            DistanceKm       = distanceKm,
                            DriveTimeMinutes = EstimateDriveTime(distanceKm),
                            Country          = country
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Places zoekopdracht mislukt voor {Term}", term);
                }
            }

            // Dedupliceer alleen op GPS (afronden op 3 decimalen = ~100m nauwkeurig)
            // Zo blijven Aldi Bunde, Aldi Maastricht etc. allemaal apart staan
            return allStores
                .GroupBy(s => $"{Math.Round(s.Latitude, 3)}|{Math.Round(s.Longitude, 3)}")
                .Select(g => g.OrderBy(s => s.DistanceKm).First())
                .OrderBy(s => s.DistanceKm)
                .Take(20)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google Places API fout");
            return new List<StoreTemplate>();
        }
    }

    // ─── HELPERS ──────────────────────────────────────────────────

    private static string DetectCountry(double lat, double lng)
    {
        if (lat >= 50.75 && lat <= 53.55 && lng >= 3.36 && lng <= 7.23) return "NL";
        if (lat >= 49.50 && lat <= 51.50 && lng >= 2.54 && lng <= 6.41) return "BE";
        if (lat >= 47.27 && lat <= 55.10 && lng >= 5.87 && lng <= 15.04) return "DE";
        return "NL";
    }

    private static int EstimateDriveTime(double distKm) => (int)(distKm / 0.6);

    private string MapGoogleNameToChain(string name)
    {
        name = name.ToLower();
        if (name.Contains("albert heijn") || name.Contains("ah to go")) return "Albert Heijn";
        if (name.Contains("jumbo"))          return "Jumbo";
        if (name.Contains("lidl"))           return "Lidl";
        if (name.Contains("aldi süd") || name.Contains("aldi sued")) return "Aldi Süd";
        if (name.Contains("aldi"))           return "Aldi";
        if (name.Contains("dm-drogerie") || name.Contains("dm drogerie") || name == "dm") return "DM";
        if (name.Contains("rewe"))           return "Rewe";
        if (name.Contains("edeka"))          return "Edeka";
        if (name.Contains("kaufland"))       return "Kaufland";
        if (name.Contains("netto"))          return "Netto";
        if (name.Contains("penny"))          return "Penny";
        if (name.Contains("kruidvat"))       return "Kruidvat";
        if (name.Contains("action"))         return "Action";
        if (name.Contains("dirk"))           return "Dirk";
        if (name.Contains("plus"))           return "Plus";
        if (name.Contains("hoogvliet"))      return "Hoogvliet";
        if (name.Contains("colruyt"))        return "Colruyt";
        if (name.Contains("delhaize"))       return "Delhaize";
        return "Onbekend";
    }

    private static string ExtractCity(string vicinity)
    {
        // Pakt het laatste deel na de laatste komma (stad)
        // Bijv. "Molenstraat 12, Beek" → "Beek"
        var parts = vicinity.Split(',');
        return parts.Length > 1 ? parts.Last().Trim() : vicinity.Trim();
    }

    private static decimal CalculateFuelCosts(double distKm, decimal consumptionL, string country)
    {
        decimal fuelPrice = country switch { "BE" => 1.72m, "DE" => 1.61m, _ => 1.89m };
        return (decimal)(distKm * 2) * (consumptionL / 100) * fuelPrice;
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
