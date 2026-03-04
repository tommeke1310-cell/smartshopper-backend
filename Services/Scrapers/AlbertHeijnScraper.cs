using System.Text.Json;
using System.Globalization;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

public class AlbertHeijnScraper
{
    private readonly HttpClient                  _http;
    private readonly ILogger<AlbertHeijnScraper> _logger;

    private static readonly HashSet<string> HuisMerkPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ah", "albert heijn", "ah biologisch", "ah terra", "ah excellente",
        "ah basic", "ah puur&eerlijk", "euro shopper"
    };

    public AlbertHeijnScraper(HttpClient http, ILogger<AlbertHeijnScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-application", "AHWEBSHOP");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        try
        {
            var url = $"https://www.ah.nl/zoeken/api/v1/search?query={Uri.EscapeDataString(item.Name)}&size=5&page=0";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AH: HTTP {Code} voor {Product}", (int)response.StatusCode, item.Name);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("cards", out var cards)) return [];

            var results = new List<ProductMatch>();

            foreach (var card in cards.EnumerateArray())
            {
                if (!card.TryGetProperty("products", out var products)) continue;
                foreach (var p in products.EnumerateArray())
                {
                    decimal price   = 0;
                    bool    isPromo = false;

                    // Correcte JSON structuur: price.now (decimal), price.was (optioneel)
                    if (p.TryGetProperty("price", out var priceObj))
                    {
                        if (priceObj.TryGetProperty("now", out var nowEl))
                        {
                            var raw = nowEl.GetRawText().Trim('"');
                            decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                        }
                        isPromo = priceObj.TryGetProperty("was", out _);
                    }

                    if (price <= 0) continue;

                    var name    = p.TryGetProperty("title",   out var t) ? t.GetString() ?? "" : item.Name;
                    var brand   = p.TryGetProperty("brand",   out var b) ? b.GetString() ?? "" : "";
                    var summary = p.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";

                    bool isHuisMerk  = IsHuisMerk(brand, name);
                    bool isAMerk     = !isHuisMerk;
                    bool isBio       = ContainsBio(name, summary, p);
                    bool isVegan     = ContainsVegan(name, summary, p);

                    results.Add(new ProductMatch
                    {
                        StoreName       = "Albert Heijn",
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
                if (results.Count >= 3) break;
            }

            return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AH fout voor {Product}", item.Name);
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

    private static bool IsHuisMerk(string brand, string name)
    {
        return HuisMerkPrefixes.Contains(brand) ||
               HuisMerkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsBio(string name, string summary, JsonElement p)
    {
        if (name.Contains("biologisch", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(" bio ", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("biologisch", StringComparison.OrdinalIgnoreCase)) return true;

        if (p.TryGetProperty("labels", out var labels))
            foreach (var lbl in labels.EnumerateArray())
            {
                var txt = lbl.ValueKind == JsonValueKind.String
                    ? lbl.GetString() ?? ""
                    : (lbl.TryGetProperty("text", out var lt) ? lt.GetString() ?? "" : "");
                if (txt.Contains("bio", StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }

    private static bool ContainsVegan(string name, string summary, JsonElement p)
    {
        if (name.Contains("vegan", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("vegan", StringComparison.OrdinalIgnoreCase)) return true;

        if (p.TryGetProperty("labels", out var labels))
            foreach (var lbl in labels.EnumerateArray())
            {
                var txt = lbl.ValueKind == JsonValueKind.String
                    ? lbl.GetString() ?? ""
                    : (lbl.TryGetProperty("text", out var lt) ? lt.GetString() ?? "" : "");
                if (txt.Contains("vegan", StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }
}
