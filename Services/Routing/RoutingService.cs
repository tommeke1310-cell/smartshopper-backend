using SmartShopper.API.Models;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace SmartShopper.API.Services.Routing;

public class RoutingService
{
    private readonly HttpClient _http;
    private readonly ILogger<RoutingService> _logger;
    private readonly string? _mapsKey;
    private readonly IMemoryCache _cache;

    // Cache Maps-resultaten 30 min per locatie (bespaart quota)
    private static readonly TimeSpan MapsCacheTtl = TimeSpan.FromMinutes(30);

    public RoutingService(HttpClient http, ILogger<RoutingService> logger, IConfiguration config, IMemoryCache cache)
    {
        _http = http;
        _logger = logger;
        _mapsKey = config["GoogleMaps:ApiKey"];
        _cache = cache;
    }

    // ── Winkels ophalen met aparte quota per land ──────────────────
    // Quota: 20 NL + 10 BE + 10 DE = max 40 winkels
    // Radius: minstens 30km, of de MaxDistance van de gebruiker (zodat
    // bv. Maasmechelen op ~20km altijd gevonden wordt)
    public async Task<List<StoreTemplate>> FindNearbyStoresAsync(double lat, double lng, int radiusMeter = 30000)
    {
        if (string.IsNullOrEmpty(_mapsKey) || _mapsKey.Contains("JOUW")) return new List<StoreTemplate>();

        // Gebruik minimaal 30km zodat grensplaatsen altijd gevonden worden
        int effectiveRadius = Math.Max(radiusMeter, 30000);

        // Cache-sleutel afgerond op ~500m
        var cacheKey = $"maps:{Math.Round(lat, 3)}:{Math.Round(lng, 3)}:{effectiveRadius}";
        if (_cache.TryGetValue(cacheKey, out List<StoreTemplate>? cached) && cached != null)
        {
            _logger.LogInformation("Maps cache hit voor locatie {Lat},{Lng}", Math.Round(lat, 3), Math.Round(lng, 3));
            return cached;
        }

        var stores = new List<StoreTemplate>();
        string[] nlChains = { "Albert Heijn", "Jumbo", "Lidl", "Aldi", "Plus", "Dirk", "Vomar", "Hoogvliet", "Coop", "Dekamarkt" };
        string[] beChains = { "Colruyt", "Delhaize", "Lidl", "Aldi", "Carrefour", "Albert Heijn" };
        string[] deChains = { "Rewe", "Edeka", "Lidl", "Aldi", "Kaufland", "Penny" };

        async Task SearchChains(string[] chains)
        {
            foreach (var chain in chains)
            {
                try
                {
                    var url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json" +
                              $"?location={lat},{lng}&radius={effectiveRadius}&name={Uri.EscapeDataString(chain)}&key={_mapsKey}";

                    var json = await _http.GetStringAsync(url);
                    var doc = JsonDocument.Parse(json);

                    foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
                    {
                        var loc = result.GetProperty("geometry").GetProperty("location");
                        var address = result.TryGetProperty("vicinity", out var v) ? v.GetString() : "";

                        double sLat = loc.GetProperty("lat").GetDouble();
                        double sLng = loc.GetProperty("lng").GetDouble();
                        string country = DetectCountry(sLat, sLng);

                        stores.Add(new StoreTemplate
                        {
                            Chain = chain,
                            Country = country,
                            Latitude = sLat,
                            Longitude = sLng,
                            City = address ?? "Onbekende locatie"
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fout bij zoeken naar winkelketen {Chain}", chain);
                }
            }
        }

        // ── Parallel zoeken per land (was sequentieel — nu 3x sneller) ──
        await Task.WhenAll(
            SearchChains(nlChains),
            SearchChains(beChains),
            SearchChains(deChains));

        // Deduplicate en pas aparte quota toe per land: 20 NL + 10 BE + 10 DE
        var deduped = stores.GroupBy(s => $"{s.Chain}|{Math.Round(s.Latitude, 3)}|{Math.Round(s.Longitude, 3)}")
                            .Select(g => g.First())
                            .ToList();

        var nlStores = deduped.Where(s => s.Country == "NL")
                              .OrderBy(s => HaversineKm(lat, lng, s.Latitude, s.Longitude))
                              .Take(20).ToList();

        var beStores = deduped.Where(s => s.Country == "BE")
                              .OrderBy(s => HaversineKm(lat, lng, s.Latitude, s.Longitude))
                              .Take(10).ToList();

        var deStores = deduped.Where(s => s.Country == "DE")
                              .OrderBy(s => HaversineKm(lat, lng, s.Latitude, s.Longitude))
                              .Take(10).ToList();

        _logger.LogInformation(
            "Winkels gevonden: {NL} NL, {BE} BE, {DE} DE (radius {Radius}km)",
            nlStores.Count, beStores.Count, deStores.Count, effectiveRadius / 1000);

        var allStores = nlStores.Concat(beStores).Concat(deStores).ToList();

        // Sla op in cache (30 min)
        _cache.Set(cacheKey, allStores, MapsCacheTtl);

        return allStores;
    }

    // Haversine afstand in km (voor sortering dichtstbijzijnde winkels)
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public async Task<(double km, int min, decimal fuel)> CalculateTripAsync(
        double fromLat, double fromLng, double toLat, double toLng,
        decimal fuelPrice, double consumption = 7.0)
    {
        try
        {
            if (!string.IsNullOrEmpty(_mapsKey) && !_mapsKey.Contains("JOUW"))
            {
                var url = $"https://maps.googleapis.com/maps/api/distancematrix/json" +
                          $"?origins={fromLat},{fromLng}&destinations={toLat},{toLng}&mode=driving&key={_mapsKey}";

                var json = await _http.GetStringAsync(url);
                var doc = JsonDocument.Parse(json);
                var el = doc.RootElement.GetProperty("rows")[0].GetProperty("elements")[0];

                if (el.GetProperty("status").GetString() == "OK")
                {
                    var km = el.GetProperty("distance").GetProperty("value").GetDouble() / 1000;
                    var min = el.GetProperty("duration").GetProperty("value").GetInt32() / 60;
                    // Kosten berekening: (Afstand * 2 voor retour) * (verbruik/100) * brandstofprijs
                    var fuelCost = (decimal)(km * 2) * (decimal)(consumption / 100) * fuelPrice;
                    return (Math.Round(km, 1), min, Math.Round(fuelCost, 2));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Distance Matrix fout");
        }

        // Fallback: schatting op basis van hemelsbreede afstand
        double distKm = Math.Sqrt(
            Math.Pow((toLat - fromLat) * 111, 2) +
            Math.Pow((toLng - fromLng) * 111 * Math.Cos(fromLat * Math.PI / 180), 2));
        int estMin = Math.Max(2, (int)(distKm / 0.6));
        decimal estFuel = (decimal)(distKm * 2) * (decimal)(consumption / 100) * fuelPrice;
        return (Math.Round(distKm, 1), estMin, Math.Round(estFuel, 2));
    }

    // Verbeterde landdetectie op basis van nauwkeurige bounding boxes
    // Volgorde: specifieke grenscases eerst, dan algemene gebieden
    private static string DetectCountry(double lat, double lng)
    {
        // ── Duitsland (DE) — rechts van NL grenslijn ──────────────────
        // Ruwweg: oost van ~6.10-6.20 en bepaalde lat-banden
        if (lat >= 51.05 && lat <= 52.55 && lng >= 6.85) return "DE"; // Achterhoek-grens en noordelijker
        if (lat >= 50.70 && lat <  51.05 && lng >= 6.20) return "DE"; // Limburg-grens / Aken-regio

        // ── België (BE) ────────────────────────────────────────────────
        // Globale BE box: lat 49.50-51.50, lng 2.54-6.40
        // Maar Zeeuws-Vlaanderen (NL) zit ook laag — check expliciet
        if (lat >= 51.25 && lat <= 51.50 && lng >= 3.36 && lng <= 4.25) return "NL"; // Zeeuws-Vlaanderen
        if (lat >= 49.50 && lat <  51.50 && lng >= 2.54 && lng <= 6.40) return "BE";

        // ── Nederland (NL) — alles daarboven ──────────────────────────
        if (lat >= 50.75 && lat <= 53.55 && lng >= 3.36 && lng <= 7.22) return "NL";

        // Fallback
        return "NL";
    }
}