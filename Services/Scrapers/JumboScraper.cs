using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

/// <summary>
/// Jumbo scraper via de mobiele API (v17).
/// Dit is de ENIGE JumboScraper — de oude HTML-versie en de duplicate in
/// OtherScrapers.cs zijn verwijderd.
/// </summary>
public class JumboScraper
{
    private readonly HttpClient           _http;
    private readonly ILogger<JumboScraper> _logger;

    private static readonly HashSet<string> HuisMerkPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jumbo", "jumbo biologisch", "jumbo puur & lekker", "jumbo fairtrade",
        "jumbo lactosevrij", "jumbo vegan", "jumbo economy"
    };

    public JumboScraper(HttpClient http, ILogger<JumboScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Linux; Android 12; Pixel 6) AppleWebKit/537.36 Mobile Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/json");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        try
        {
            var url = $"https://mobileapi.jumbo.com/v17/search" +
                      $"?q={Uri.EscapeDataString(item.Name)}&offset=0&limit=5";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            // Structuur: { "products": { "data": [ { ... } ] } }
            if (!doc.RootElement.TryGetProperty("products", out var productsRoot)) return [];
            if (!productsRoot.TryGetProperty("data", out var data)) return [];

            var results = new List<ProductMatch>();

            foreach (var p in data.EnumerateArray())
            {
                decimal price   = 0;
                bool    isPromo = false;

                if (p.TryGetProperty("prices", out var prices))
                {
                    // Jumbo geeft prijzen in centen: { "price": { "amount": 149 } }
                    if (prices.TryGetProperty("price", out var priceObj) &&
                        priceObj.TryGetProperty("amount", out var amtEl))
                        price = amtEl.GetDecimal() / 100m;

                    // Promotieprijs aanwezig?
                    isPromo = prices.TryGetProperty("promotionalPrice", out _);
                }

                if (price <= 0) continue;

                var name  = p.TryGetProperty("title",  out var t) ? t.GetString() ?? "" : item.Name;
                var brand = p.TryGetProperty("brand",  out var b) ? b.GetString() ?? "" : "";
                var desc  = p.TryGetProperty("detailsText", out var d) ? d.GetString() ?? "" : "";

                bool isHuisMerk = IsHuisMerk(brand, name);
                bool isAMerk    = !isHuisMerk;
                bool isBio      = ContainsBio(name, desc, p);
                bool isVegan    = ContainsVegan(name, desc, p);

                results.Add(new ProductMatch
                {
                    StoreName       = "Jumbo",
                    Country         = "NL",
                    ProductName     = name,
                    Price           = price,
                    IsPromo         = isPromo,
                    IsEstimated     = false,
                    MatchConfidence = WordScore(item.Name, name),
                    IsBiologisch    = isBio,
                    IsAMerk         = isAMerk,
                    IsHuisMerk      = isHuisMerk,
                    IsVegan         = isVegan,
                });

                if (results.Count >= 3) break;
            }

            return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jumbo fout voor {Product}", item.Name);
            return [];
        }
    }

    private static double WordScore(string query, string product)
    {
        var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        var lower = product.ToLower();
        return (double)words.Count(w => lower.Contains(w)) / words.Length;
    }

    private static bool IsHuisMerk(string brand, string name) =>
        HuisMerkPrefixes.Contains(brand) ||
        HuisMerkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsBio(string name, string desc, JsonElement p)
    {
        if (name.Contains("biologisch", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(" bio ", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("biologisch", StringComparison.OrdinalIgnoreCase)) return true;

        if (p.TryGetProperty("tags", out var tags))
            foreach (var tag in tags.EnumerateArray())
            {
                var txt = tag.GetString() ?? "";
                if (txt.Contains("bio", StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }

    private static bool ContainsVegan(string name, string desc, JsonElement p)
    {
        if (name.Contains("vegan", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("vegan", StringComparison.OrdinalIgnoreCase)) return true;

        if (p.TryGetProperty("tags", out var tags))
            foreach (var tag in tags.EnumerateArray())
            {
                var txt = tag.GetString() ?? "";
                if (txt.Contains("vegan", StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }
}
