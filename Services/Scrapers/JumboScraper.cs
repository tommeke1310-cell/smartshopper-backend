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
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,text/html,*/*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.jumbo.com/");
    }

    public async Task<List<ProductMatch>> SearchProductAsync(GroceryItem item)
    {
        // Stap 1: Jumbo publieke REST search API (meest betrouwbaar)
        var results = await TryJumboApiV2(item);
        if (results.Count > 0) return results;

        // Stap 2: Legacy webshop JSON API
        results = await TryWebshopApi(item);
        if (results.Count > 0) return results;

        // Stap 3: HTML scraping fallback
        results = await TryHtmlScrape(item);
        if (results.Count > 0) return results;

        _logger.LogWarning("Jumbo: geen resultaten voor '{Product}'", item.Name);
        return [];
    }

    // ─── Jumbo publieke API v2 ────────────────────────────────────
    private async Task<List<ProductMatch>> TryJumboApiV2(GroceryItem item)
    {
        try
        {
            // Jumbo's publieke REST API (stabielste endpoint)
            var url = $"https://www.jumbo.com/api/products" +
                      $"?q={Uri.EscapeDataString(item.Name)}&pageSize=10&currentPage=0&suggestionsAllowed=true";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Jumbo API v2: {Status}", resp.StatusCode);
                return [];
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Structuur: { products: { items: [...] } } of { data: { products: [...] } }
            JsonElement items = default;
            bool found =
                (doc.RootElement.TryGetProperty("products", out var pr1) && pr1.TryGetProperty("items", out items)) ||
                (doc.RootElement.TryGetProperty("data",     out var d)   && d.TryGetProperty("products", out items)) ||
                doc.RootElement.TryGetProperty("products",  out items);

            if (!found || items.ValueKind != JsonValueKind.Array) return [];

            return ParseItemsWithBestMatch(items, item.Name, "Jumbo API v2");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo API v2 mislukt voor '{Product}'", item.Name);
            return [];
        }
    }

    // ─── Jumbo webshop JSON API (legacy) ─────────────────────────
    private async Task<List<ProductMatch>> TryWebshopApi(GroceryItem item)
    {
        try
        {
            // Twee bekende Jumbo search endpoints uitproberen
            var urls = new[]
            {
                $"https://www.jumbo.com/api/search-page/v1?searchType=keyword&searchTerms={Uri.EscapeDataString(item.Name)}&pageSize=10&currentPage=0",
                $"https://www.jumbo.com/zoeken/?q={Uri.EscapeDataString(item.Name)}&format=json",
            };

            foreach (var url in urls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
                    req.Headers.TryAddWithoutValidation("Accept", "application/json");

                    var resp = await _http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;

                    var json = await resp.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '{') continue;

                    using var doc = JsonDocument.Parse(json);

                    JsonElement items = default;
                    bool found =
                        (doc.RootElement.TryGetProperty("products", out var pr) && pr.TryGetProperty("items", out items)) ||
                        doc.RootElement.TryGetProperty("items", out items);

                    if (!found || items.ValueKind != JsonValueKind.Array) continue;

                    var results = ParseItemsWithBestMatch(items, item.Name, "Jumbo webshop");
                    if (results.Count > 0) return results;
                }
                catch { /* probeer volgende URL */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jumbo webshop API mislukt voor '{Product}'", item.Name);
        }
        return [];
    }

    // ─── HTML scraping fallback ───────────────────────────────────
    private async Task<List<ProductMatch>> TryHtmlScrape(GroceryItem item)
    {
        try
        {
            var url  = $"https://www.jumbo.com/zoeken/?q={Uri.EscapeDataString(item.Name)}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return [];

            var html = await resp.Content.ReadAsStringAsync();

            // Probeer JSON-LD structured data (meest betrouwbaar in HTML)
            var jsonLdMatches = System.Text.RegularExpressions.Regex.Matches(
                html, @"<script[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            foreach (System.Text.RegularExpressions.Match jsonLdMatch in jsonLdMatches)
            {
                try
                {
                    var jsonLd = jsonLdMatch.Groups[1].Value.Trim();
                    using var doc = JsonDocument.Parse(jsonLd);

                    // Zoek naar ItemList of individuele producten
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("@type", out var type) &&
                        type.GetString() == "ItemList" &&
                        root.TryGetProperty("itemListElement", out var listEl))
                    {
                        foreach (var el in listEl.EnumerateArray())
                        {
                            if (el.TryGetProperty("item", out var productEl))
                            {
                                var match = ParseJsonLdProduct(productEl, item.Name);
                                if (match != null) return [match];
                            }
                        }
                    }
                    else if (root.TryGetProperty("offers", out var offers))
                    {
                        var match = ParseJsonLdProduct(root, item.Name);
                        if (match != null) return [match];
                    }
                }
                catch { /* probeer volgende JSON-LD blok */ }
            }

            // HTML prijs selectors als last resort
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Probeer meerdere product-tegels te vinden
            var productNodes = htmlDoc.DocumentNode.SelectNodes(
                "//article[contains(@class,'product')] | //div[contains(@class,'product-tile')]");

            if (productNodes != null)
            {
                foreach (var productNode in productNodes.Take(5))
                {
                    var nameNode = productNode.SelectSingleNode(
                        ".//h2 | .//h3 | .//*[contains(@class,'title')] | .//*[contains(@class,'name')]");
                    var priceNode = productNode.SelectSingleNode(
                        ".//*[contains(@class,'price')] | .//*[@data-testid='price-amount']");

                    if (priceNode == null) continue;

                    var raw = System.Text.RegularExpressions.Regex.Match(
                        priceNode.InnerText.Replace(",", "."), @"\d+[\.,]\d{2}").Value;

                    if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) || p <= 0)
                        continue;

                    var name = nameNode?.InnerText?.Trim() ?? item.Name;
                    var score = ProductMatcher.Score(item.Name, name);

                    _logger.LogInformation("Jumbo HTML: '{Name}' €{Price} (score {Score:F2})", name, p, score);
                    return [new ProductMatch
                    {
                        StoreName       = "Jumbo",
                        Country         = "NL",
                        ProductName     = name,
                        Price           = p,
                        IsEstimated     = false,
                        MatchConfidence = score,
                        LastUpdated     = DateTime.UtcNow
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

    private ProductMatch? ParseJsonLdProduct(JsonElement el, string query)
    {
        var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return null;

        decimal price = 0;
        if (el.TryGetProperty("offers", out var offers))
        {
            var priceEl = offers.ValueKind == JsonValueKind.Array
                ? offers.EnumerateArray().FirstOrDefault()
                : offers;
            if (priceEl.TryGetProperty("price", out var p))
                decimal.TryParse(p.GetString()?.Replace(",", ".") ?? p.ToString(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out price);
        }
        if (price <= 0) return null;

        var score = ProductMatcher.Score(query, name);
        _logger.LogInformation("Jumbo JSON-LD: '{Name}' €{Price} (score {Score:F2})", name, price, score);

        return new ProductMatch
        {
            StoreName       = "Jumbo",
            Country         = "NL",
            ProductName     = name,
            Price           = price,
            IsEstimated     = false,
            MatchConfidence = score,
            LastUpdated     = DateTime.UtcNow
        };
    }

    private List<ProductMatch> ParseItemsWithBestMatch(JsonElement items, string query, string source)
    {
        var candidates = new List<ProductMatch>();

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
                    isPromo = true;
                    promoText = "Jumbo aanbieding";
                    if (promo.TryGetProperty("amount", out var pa)) price = pa.GetDecimal() / 100m;
                }
            }
            else if (p.TryGetProperty("price", out var dp))
                price = dp.ValueKind == JsonValueKind.Number ? dp.GetDecimal() : 0;

            if (price <= 0) continue;

            var name  = p.TryGetProperty("title", out var t) ? t.GetString() ?? query :
                        p.TryGetProperty("name",  out var n) ? n.GetString() ?? query : query;
            var brand = p.TryGetProperty("brand", out var b) ? b.GetString() ?? "" : "";

            candidates.Add(new ProductMatch
            {
                StoreName       = "Jumbo",
                Country         = "NL",
                ProductName     = name,
                Price           = price,
                IsPromo         = isPromo,
                PromoText       = promoText,
                IsEstimated     = false,
                MatchConfidence = ProductMatcher.Score(query, name),
                IsBiologisch    = name.Contains("biologisch", StringComparison.OrdinalIgnoreCase),
                IsVegan         = name.Contains("vegan", StringComparison.OrdinalIgnoreCase),
                IsHuisMerk      = IsHuisMerk(brand, name),
                IsAMerk         = !IsHuisMerk(brand, name),
                LastUpdated     = DateTime.UtcNow,
            });
            if (candidates.Count >= 10) break;
        }

        if (candidates.Count == 0) return [];

        var best = candidates.OrderByDescending(c => c.MatchConfidence).First();
        _logger.LogInformation("{Source}: beste match '{Name}' €{Price} (score {Score:F2}) voor '{Query}'",
            source, best.ProductName, best.Price, best.MatchConfidence, query);
        return [best];
    }

    private static bool IsHuisMerk(string brand, string name) =>
        HuisMerkPrefixes.Contains(brand) ||
        HuisMerkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
