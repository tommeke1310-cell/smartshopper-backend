using SmartShopper.API.Models;
using System.Text.Json;

namespace SmartShopper.API.Services.Scrapers;

public class JumboScraper {
    private readonly HttpClient _http;
    private readonly ILogger<JumboScraper> _logger;

    public JumboScraper(HttpClient http, ILogger<JumboScraper> logger) {
        _http = http; _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item) {
        try {
            var url = $"https://mobileapi.jumbo.com/v17/search?q={Uri.EscapeDataString(item.Name)}&offset=0&limit=3";
            var json = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);
            var results = new List<ProductMatch>();
            foreach (var p in doc.RootElement.GetProperty("products").GetProperty("data").EnumerateArray()) {
                decimal price = 0;
                if (p.TryGetProperty("prices", out var pr) && pr.TryGetProperty("price", out var pv)
                    && pv.TryGetProperty("amount", out var amt))
                    price = amt.GetDecimal() / 100m;
                var name = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (price > 0) results.Add(new ProductMatch {
                    StoreName = "Jumbo", Country = "NL", ProductName = name, Price = price,
                    MatchConfidence = Score(item.Name, name)
                });
            }
            return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
        } catch (Exception ex) { _logger.LogError(ex, "Jumbo fout"); return []; }
    }
    private static double Score(string q, string p) {
        var w = q.ToLower().Split(' ');
        return (double)w.Count(x => p.ToLower().Contains(x)) / w.Length;
    }
}

public class LidlScraper {
    private readonly HttpClient _http;
    private readonly ILogger<LidlScraper> _logger;
    public LidlScraper(HttpClient http, ILogger<LidlScraper> logger) { _http = http; _logger = logger; }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string country = "NL") {
        // Lidl heeft geen stabiele publieke API - geeft demo data terug
        // TODO: implementeer Playwright scraper voor productie
        await Task.Delay(50);
        return new List<ProductMatch> {
            new() { StoreName = "Lidl", Country = country,
                    ProductName = item.Name, Price = GetEstimatedPrice(item.Name, "lidl"),
                    MatchConfidence = 0.7 }
        };
    }
    private static decimal GetEstimatedPrice(string name, string store) => 2.49m; // placeholder
}

public class AldiScraper {
    private readonly HttpClient _http;
    private readonly ILogger<AldiScraper> _logger;
    public AldiScraper(HttpClient http, ILogger<AldiScraper> logger) { _http = http; _logger = logger; }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item, string country = "NL") {
        await Task.Delay(50);
        // TODO: Playwright scraper voor aldi.nl / aldi-sued.de
        return new List<ProductMatch> {
            new() { StoreName = country == "DE" ? "Aldi Süd" : "Aldi", Country = country,
                    ProductName = item.Name, Price = GetEstimatedPrice(item.Name, country),
                    MatchConfidence = 0.65 }
        };
    }
    private static decimal GetEstimatedPrice(string name, string country) =>
        country == "DE" ? 1.89m : 2.19m; // placeholder - lager in DE
}
