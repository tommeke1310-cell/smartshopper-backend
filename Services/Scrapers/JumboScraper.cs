using System.Text.Json;
using System.Globalization;
using HtmlAgilityPack;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

public class JumboScraper
{
    private readonly HttpClient            _http;
    private readonly ILogger<JumboScraper> _logger;

    private static readonly HashSet<string> HuisMerkPrefixes = new(StringComparer.OrdinalIgnoreCase)
        { "jumbo", "jumbo biologisch", "jumbo puur & lekker", "jumbo fairtrade" };

    public JumboScraper(HttpClient http, ILogger<JumboScraper> logger)
    {
        _http   = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/json,*/*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.jumbo.com/");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        // Normaliseer zoekterm
        item = item with { Name = ProductMatcher.NormalizeQueryForSearch(item.Name) };
        // Stap 1: webshop JSON API
        var results = await TryWebshopApi(item);
        if (results.Count > 0) return results;

        // Stap 2: HTML pagina parsen
        results = await TryHtmlScrape(item);
        if (results.Count > 0) return results;

        _logger.LogWarning("Jumbo: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    // ─── Jumbo mobile API (mobileapi.jumbo.com/v17) — meest betrouwbaar ────
    private async Task<List<ProductMatch>> TryWebshopApi(GroceryItem item)
    {
        try
        {
            // Primair: officiele Jumbo mobile API (geen auth nodig)
            var url = $"https://mobileapi.jumbo.com/v17/search" +
                      $"?q={Uri.EscapeDataString(item.Name)}&offset=0&limit=5&type=PRODUCTS";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-jumbo-token", "");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jumbo webshop API: {Status}", resp.StatusCode);
                return [];
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Twee mogelijke structuren: { products: { items: [...] } } of { data: [...] }
            JsonElement items;
            if (doc.RootElement.TryGetProperty("products", out var pr) &&
                pr.TryGetProperty("items", out items))
            {
                return ParseItems(items, item.Name);
            }

            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo webshop API mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    // ─── HTML scraping fallback ───────────────────────────────────
    private async Task<List<ProductMatch>> TryHtmlScrape(GroceryItem item)
    {
        try
        {
            var url  = $"https://www.jumbo.com/producten/?searchTerms={Uri.EscapeDataString(item.Name)}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return [];

            var html = await resp.Content.ReadAsStringAsync();

            // Probeer JSON-LD data uit de HTML te halen
            var jsonLdMatch = System.Text.RegularExpressions.Regex.Match(
                html, @"<script[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (jsonLdMatch.Success)
            {
                try
                {
                    var jsonLd = jsonLdMatch.Groups[1].Value.Trim();
                    using var doc = JsonDocument.Parse(jsonLd);
                    if (doc.RootElement.TryGetProperty("offers", out var offers))
                    {
                        decimal price = 0;
                        if (offers.TryGetProperty("price", out var p))
                            decimal.TryParse(p.GetString()?.Replace(",", "."),
                                NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? item.Name : item.Name;
                        if (price > 0)
                        {
                            _logger.LogInformation("Jumbo JSON-LD: {Product} → €{Price}", name, price);
                            return [new ProductMatch
                            {
                                StoreName = "Jumbo", Country = "NL", ProductName = name,
                                Price = price, IsEstimated = false, MatchConfidence = ProductMatcher.MatchScore(item.Name, name),
                                LastUpdated = DateTime.UtcNow
                            }];
                        }
                    }
                }
                catch { /* JSON-LD parsing mislukt, ga door naar HTML */ }
            }

            // HTML prijs selectors
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            var priceNode = htmlDoc.DocumentNode.SelectSingleNode(
                "//span[contains(@class,'jum-price')]//span[contains(@class,'integer')] | " +
                "//div[contains(@class,'product-price')] | " +
                "//*[@data-testid='price-amount'] | " +
                "//span[contains(@class,'price-amount')]");

            if (priceNode != null)
            {
                var raw = System.Text.RegularExpressions.Regex.Match(
                    priceNode.InnerText.Replace(",", "."), @"\d+[\.,]\d{2}").Value;
                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                {
                    // Probeer ook productnaam te vinden
                    var nameNode = htmlDoc.DocumentNode.SelectSingleNode(
                        "//h2[contains(@class,'product-title')] | //h3[contains(@class,'product-title')] | " +
                        "//*[@data-testid='product-title']");
                    var name = nameNode?.InnerText?.Trim() ?? item.Name;

                    _logger.LogInformation("Jumbo HTML: {Product} → €{Price}", name, p);
                    return [new ProductMatch
                    {
                        StoreName = "Jumbo", Country = "NL", ProductName = name,
                        Price = p, IsEstimated = false, MatchConfidence = ProductMatcher.MatchScore(item.Name, name),
                        LastUpdated = DateTime.UtcNow
                    }];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo HTML scraping mislukt voor '{Product}'", item.Name);
        }
        return [];
    }

    private List<ProductMatch> ParseItems(JsonElement items, string query)
    {
        var results = new List<ProductMatch>();
        foreach (var p in items.EnumerateArray())
        {
            decimal price = 0;
            bool isPromo = false;
            string promoText = "";

            if (p.TryGetProperty("prices", out var prices))
            {
                if (prices.TryGetProperty("price", out var po))
                    price = po.TryGetProperty("amount", out var a) ? a.GetDecimal() / 100m :
                            po.ValueKind == JsonValueKind.Number ? po.GetDecimal() : 0;
                if (prices.TryGetProperty("promotionalPrice", out var promo) &&
                    promo.ValueKind != JsonValueKind.Null)
                {
                    isPromo = true; promoText = "Jumbo aanbieding";
                    if (promo.TryGetProperty("amount", out var pa)) price = pa.GetDecimal() / 100m;
                }
            }
            else if (p.TryGetProperty("price", out var dp))
                price = dp.ValueKind == JsonValueKind.Number ? dp.GetDecimal() : 0;

            if (price <= 0) continue;

            var name  = p.TryGetProperty("title", out var t) ? t.GetString() ?? query :
                        p.TryGetProperty("name",  out var n) ? n.GetString() ?? query : query;
            var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";

            results.Add(new ProductMatch
            {
                StoreName = "Jumbo", Country = "NL", ProductName = name, Price = price,
                IsPromo = isPromo, PromoText = promoText, IsEstimated = false,
                MatchConfidence = ProductMatcher.MatchScore(query, name),
                IsBiologisch = name.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                IsVegan      = name.Contains("vegan", StringComparison.OrdinalIgnoreCase),
                IsHuisMerk   = IsHuisMerk(brand, name), IsAMerk = !IsHuisMerk(brand, name),
                LastUpdated  = DateTime.UtcNow,
            });
            if (results.Count >= 3) break;
        }

        if (results.Count > 0)
            _logger.LogInformation("Jumbo webshop: {Count} voor '{Product}'", results.Count, query);

        return results.OrderByDescending(r => r.MatchConfidence).Take(1).ToList();
    }

    // WordScore vervangen door ProductMatcher.MatchScore

    private static bool IsHuisMerk(string brand, string name) =>
        HuisMerkPrefixes.Contains(brand) ||
        HuisMerkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
