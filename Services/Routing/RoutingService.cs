using SmartShopper.API.Models;
using System.Text.Json;

namespace SmartShopper.API.Services.Routing;

public class RoutingService
{
    private readonly HttpClient _http;
    private readonly ILogger<RoutingService> _logger;
    private readonly string? _mapsKey;

    public RoutingService(HttpClient http, ILogger<RoutingService> logger, IConfiguration config)
    {
        _http = http;
        _logger = logger;
        _mapsKey = config["GoogleMaps:ApiKey"];
    }

    public async Task<List<StoreTemplate>> FindNearbyStoresAsync(double lat, double lng, int radiusMeter = 15000)
    {
        if (string.IsNullOrEmpty(_mapsKey) || _mapsKey.Contains("JOUW")) return new List<StoreTemplate>();

        var stores = new List<StoreTemplate>();
        string[] chains = { "Albert Heijn", "Jumbo", "Aldi", "Lidl" };

        foreach (var chain in chains)
        {
            try
            {
                var url = $"https://maps.googleapis.com/maps/api/place/nearbysearch/json" +
                          $"?location={lat},{lng}&radius={radiusMeter}&keyword={Uri.EscapeDataString(chain)}&key={_mapsKey}";

                var json = await _http.GetStringAsync(url);
                var doc = JsonDocument.Parse(json);

                foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var loc = result.GetProperty("geometry").GetProperty("location");
                    var address = result.TryGetProperty("vicinity", out var v) ? v.GetString() : "";

                    // Land herkenning op basis van coordinaten (betrouwbaarder dan adrestekst)
                    double sLat = loc.GetProperty("lat").GetDouble();
                    double sLng = loc.GetProperty("lng").GetDouble();
                    string country = "NL";
                    if (sLat >= 50.75 && sLat <= 51.80 && sLng >= 5.55 && sLng <= 6.25) country = "NL";
                    else if (sLat >= 47.0 && sLat <= 55.0 && sLng >= 6.0 && sLng <= 15.0) country = "DE";
                    else if (sLat >= 49.5 && sLat <= 51.5 && sLng >= 2.5 && sLng <= 6.4) country = "BE";

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
        // Groepeer op keten en locatie om dubbele resultaten te voorkomen
        return stores.GroupBy(s => s.Chain + s.City).Select(g => g.First()).Take(12).ToList();
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
}
