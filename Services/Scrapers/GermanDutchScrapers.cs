using SmartShopper.API.Models;
using System.Text.Json;
using System.Globalization;
using HtmlAgilityPack;

namespace SmartShopper.API.Services.Scrapers;

// ─────────────────────────────────────────────────────────────────
//  LIDL  (NL / BE / DE)
// ─────────────────────────────────────────────────────────────────
public class LidlScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<LidlScraper> _logger;

    public LidlScraper(HttpClient http, ILogger<LidlScraper> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/html, */*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "nl-NL,nl;q=0.9,de;q=0.8");
    }

    public async Task<ScraperResult> SearchProductAsync(string query, string country = "NL")
    {
        // Probeer eerst de JSON API, daarna HTML fallback
        var result = await TryLidlApi(query, country);
        if (result.Success) return result;

        return await TryLidlHtml(query, country);
    }

    private async Task<ScraperResult> TryLidlApi(string query, string country)
    {
        try
        {
            // Lidl heeft een interne zoek-API die per land verschilt
            var (domain, locale) = country switch
            {
                "DE" => ("lidl.de",  "DE/de"),
                "BE" => ("lidl.be",  "BE/nl"),
                _    => ("lidl.nl",  "NL/nl")
            };

            var url = $"https://www.{domain}/p/api/gridboxes/{locale}/" +
                      $"?max=5&search={Uri.EscapeDataString(query)}";

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new ScraperResult(query, 0, false);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new ScraperResult(query, 0, false);

            foreach (var box in doc.RootElement.EnumerateArray())
            {
                if (!box.TryGetProperty("gridList", out var list)) continue;

                foreach (var item in list.EnumerateArray())
                {
                    decimal price = ExtractLidlPrice(item);
                    if (price <= 0) continue;

                    string title = item.TryGetProperty("keyfacts", out var kf) &&
                                   kf.TryGetProperty("name", out var n)
                                   ? n.GetString() ?? query : query;

                    _logger.LogInformation("Lidl {Country}: {Product} → €{Price}", country, title, price);
                    return new ScraperResult(title, price, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lidl API mislukt voor {Product} ({Country})", query, country);
        }

        return new ScraperResult(query, 0, false);
    }

    private static decimal ExtractLidlPrice(JsonElement item)
    {
        // Lidl heeft meerdere mogelijke prijsvelden
        if (item.TryGetProperty("price", out var priceObj))
        {
            if (priceObj.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number)
                return p.GetDecimal();
            if (priceObj.TryGetProperty("regularPrice", out var rp) && rp.ValueKind == JsonValueKind.Number)
                return rp.GetDecimal();
        }
        // Soms staat prijs als string "1.99"
        if (item.TryGetProperty("priceString", out var ps))
        {
            string priceStr = ps.GetString()?.Replace(",", ".") ?? "";
            if (decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
                return parsed;
        }
        return 0;
    }

    private async Task<ScraperResult> TryLidlHtml(string query, string country)
    {
        try
        {
            var domain = country switch { "DE" => "lidl.de", "BE" => "lidl.be", _ => "lidl.nl" };
            var url = $"https://www.{domain}/zoeken/?q={Uri.EscapeDataString(query)}";
            if (country == "DE") url = $"https://www.lidl.de/suche?query={Uri.EscapeDataString(query)}";

            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Lidl NL/BE
            var priceNode = doc.DocumentNode.SelectSingleNode(
                "//span[contains(@class,'m-price__price')] | " +
                "//span[contains(@class,'pricebox__price')] | " +
                "//div[contains(@class,'product-grid-box')]//span[contains(@class,'price')]"
            );

            if (priceNode != null)
            {
                string priceText = priceNode.InnerText.Trim()
                    .Replace("€", "").Replace(" ", "").Replace(",", ".");
                // Verwijder eventuele tekst als "per stuk"
                priceText = System.Text.RegularExpressions.Regex.Match(priceText, @"\d+\.\d{2}").Value;

                if (decimal.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price) && price > 0)
                {
                    _logger.LogInformation("Lidl HTML {Country}: {Product} → €{Price}", country, query, price);
                    return new ScraperResult(query, price, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lidl HTML scraper fout voor {Product}", query);
        }

        return new ScraperResult(query, 0, false);
    }
}

// ─────────────────────────────────────────────────────────────────
//  ALDI  (NL / BE / DE via Aldi Süd)
// ─────────────────────────────────────────────────────────────────
public class AldiScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<AldiScraper> _logger;

    public AldiScraper(HttpClient http, ILogger<AldiScraper> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/html, */*");
    }

    public async Task<ScraperResult> SearchProductAsync(string query, string country = "NL")
    {
        return country == "DE"
            ? await ScrapeAldiSued(query)
            : await ScrapeAldiNlBe(query, country);
    }

    // Aldi NL en BE hebben dezelfde website structuur
    private async Task<ScraperResult> ScrapeAldiNlBe(string query, string country)
    {
        try
        {
            var domain = country == "BE" ? "aldi.be" : "aldi.nl";
            var url = $"https://www.{domain}/nl/zoeken.html?q={Uri.EscapeDataString(query)}";

            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            return ExtractAldiPrice(doc, query, country == "BE" ? "Aldi BE" : "Aldi NL");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aldi NL/BE scraper fout voor {Product}", query);
            return new ScraperResult(query, 0, false);
        }
    }

    // Aldi Süd Duitsland heeft een eigen API
    private async Task<ScraperResult> ScrapeAldiSued(string query)
    {
        try
        {
            // Probeer eerst de Aldi Süd zoek-API
            var apiUrl = $"https://api.aldi-sued.de/v1/search?term={Uri.EscapeDataString(query)}&page=1&pageSize=5&country=DE";

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("x-api-key", "public"); // Publieke key
            request.Headers.Add("Accept", "application/json");

            var response = await _http.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        decimal price = 0;

                        if (item.TryGetProperty("pricing", out var pricing))
                        {
                            if (pricing.TryGetProperty("currentPrice", out var cp))
                                price = cp.GetDecimal();
                        }

                        if (price <= 0) continue;

                        string title = item.TryGetProperty("name", out var n) ? n.GetString() ?? query : query;
                        _logger.LogInformation("Aldi Süd DE: {Product} → €{Price}", title, price);
                        return new ScraperResult(title, price, true);
                    }
                }
            }

            // Fallback: HTML scrapen van aldi-sued.de
            return await ScrapeAldiSuedHtml(query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aldi Süd API mislukt, probeer HTML voor {Product}", query);
            return await ScrapeAldiSuedHtml(query);
        }
    }

    private async Task<ScraperResult> ScrapeAldiSuedHtml(string query)
    {
        try
        {
            var url = $"https://www.aldi-sued.de/de/sortiment/suche.html?q={Uri.EscapeDataString(query)}";
            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            return ExtractAldiPrice(doc, query, "Aldi Süd DE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aldi Süd HTML scraper fout voor {Product}", query);
            return new ScraperResult(query, 0, false);
        }
    }

    private ScraperResult ExtractAldiPrice(HtmlDocument doc, string query, string storeName)
    {
        // Aldi gebruikt verschillende price selectors
        string[] selectors = [
            "//span[contains(@class,'price__wrapper')]",
            "//span[contains(@class,'mod-article-tile__price')]",
            "//div[contains(@class,'price-box')]//span[contains(@class,'price')]",
            "//span[contains(@class,'product-price')]",
            "//meta[@itemprop='price']",
            "//span[@class='price']"
        ];

        foreach (var selector in selectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            if (node == null) continue;

            string raw = node.Name == "meta"
                ? node.GetAttributeValue("content", "")
                : node.InnerText;

            // Extraheer getal uit tekst (bv. "1,99 *" of "€ 2.49")
            var match = System.Text.RegularExpressions.Regex.Match(
                raw.Replace(",", "."), @"\d+\.\d{2}"
            );

            if (match.Success &&
                decimal.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price) &&
                price > 0)
            {
                _logger.LogInformation("{Store}: {Product} → €{Price}", storeName, query, price);
                return new ScraperResult(query, price, true);
            }
        }

        return new ScraperResult(query, 0, false);
    }
}

// ─────────────────────────────────────────────────────────────────
//  DM  (Drogist — NL / DE)
// ─────────────────────────────────────────────────────────────────
public class DmScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<DmScraper> _logger;

    public DmScraper(HttpClient http, ILogger<DmScraper> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<ScraperResult> SearchProductAsync(string query, string country = "NL")
    {
        // DM heeft een goede JSON API
        try
        {
            var (domain, locale) = country == "DE"
                ? ("dm.de",  "de_DE")
                : ("dm.nl",  "nl_NL");

            var url = $"https://product-search.services.dmtech.com/{locale}/search/crawl" +
                      $"?q={Uri.EscapeDataString(query)}&pageSize=5&currentPage=0";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("dm-app-version", "1.0");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return await TryDmHtml(query, country);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("products", out var products))
                return await TryDmHtml(query, country);

            foreach (var product in products.EnumerateArray())
            {
                decimal price = 0;

                if (product.TryGetProperty("price", out var priceObj))
                {
                    if (priceObj.TryGetProperty("value", out var v))
                        price = v.GetDecimal();
                }

                if (price <= 0) continue;

                string title = product.TryGetProperty("name", out var n) ? n.GetString() ?? query : query;
                _logger.LogInformation("DM {Country}: {Product} → €{Price}", country, title, price);
                return new ScraperResult(title, price, true);
            }

            return await TryDmHtml(query, country);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DM API mislukt voor {Product}", query);
            return await TryDmHtml(query, country);
        }
    }

    private async Task<ScraperResult> TryDmHtml(string query, string country)
    {
        try
        {
            var domain = country == "DE" ? "dm.de" : "dm.nl";
            var url = $"https://www.{domain}/search?query={Uri.EscapeDataString(query)}";

            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // DM gebruikt data-dmid attributes en JSON-LD voor prijzen
            var jsonLd = doc.DocumentNode.SelectSingleNode("//script[@type='application/ld+json']");
            if (jsonLd != null)
            {
                using var ldDoc = JsonDocument.Parse(jsonLd.InnerText);
                if (ldDoc.RootElement.TryGetProperty("offers", out var offers) &&
                    offers.TryGetProperty("price", out var p))
                {
                    if (decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price) && price > 0)
                        return new ScraperResult(query, price, true);
                }
            }

            // Fallback: zoek price span
            var priceNode = doc.DocumentNode.SelectSingleNode(
                "//span[contains(@class,'price') and not(contains(@class,'old'))]"
            );

            if (priceNode != null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    priceNode.InnerText.Replace(",", "."), @"\d+\.\d{2}"
                );
                if (match.Success &&
                    decimal.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0)
                    return new ScraperResult(query, price, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DM HTML scraper fout voor {Product}", query);
        }

        return new ScraperResult(query, 0, false);
    }
}

// ─────────────────────────────────────────────────────────────────
//  REWE  (DE)
// ─────────────────────────────────────────────────────────────────
public class ReweScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<ReweScraper> _logger;

    public ReweScraper(HttpClient http, ILogger<ReweScraper> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.Add("Accept-Language", "de-DE,de;q=0.9");
    }

    public async Task<ScraperResult> SearchProductAsync(string query)
    {
        // REWE heeft een uitstekende publieke API
        try
        {
            // marketId 562223 = een REWE in Aachen (dichtbij NL grens)
            // In productie: ophalen op basis van dichtstbijzijnde REWE via Google Places
            var url = $"https://shop.rewe.de/api/v7/products" +
                      $"?search={Uri.EscapeDataString(query)}" +
                      $"&page=1&pageSize=5&marketId=562223&sorting=RELEVANCE";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://shop.rewe.de/");
            request.Headers.Add("x-requested-with", "XMLHttpRequest");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return await TryReweHtml(query);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // REWE API geeft products array terug
            JsonElement products;
            if (doc.RootElement.TryGetProperty("products", out products) ||
                doc.RootElement.TryGetProperty("items", out products))
            {
                foreach (var product in products.EnumerateArray())
                {
                    decimal price = ExtractRewePrice(product);
                    if (price <= 0) continue;

                    string title = "";
                    if (product.TryGetProperty("name", out var n)) title = n.GetString() ?? query;
                    else if (product.TryGetProperty("title", out var t)) title = t.GetString() ?? query;

                    _logger.LogInformation("REWE DE: {Product} → €{Price}", title, price);
                    return new ScraperResult(title, price, true);
                }
            }

            return await TryReweHtml(query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REWE API mislukt voor {Product}", query);
            return await TryReweHtml(query);
        }
    }

    private static decimal ExtractRewePrice(JsonElement product)
    {
        // REWE prijsstructuur is genest
        if (product.TryGetProperty("pricing", out var pricing))
        {
            if (pricing.TryGetProperty("currentRetailPrice", out var crp))
                return crp.GetDecimal();
            if (pricing.TryGetProperty("price", out var p))
                return p.GetDecimal();
        }
        if (product.TryGetProperty("price", out var directPrice))
        {
            if (directPrice.ValueKind == JsonValueKind.Number)
                return directPrice.GetDecimal();
            if (directPrice.TryGetProperty("value", out var v))
                return v.GetDecimal();
        }
        return 0;
    }

    private async Task<ScraperResult> TryReweHtml(string query)
    {
        try
        {
            var url = $"https://www.rewe.de/suche/?search={Uri.EscapeDataString(query)}";
            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // REWE gebruikt data-testid attributes
            var priceNode = doc.DocumentNode.SelectSingleNode(
                "//*[@data-testid='product-price'] | " +
                "//span[contains(@class,'price__value')] | " +
                "//div[contains(@class,'product-price')]"
            );

            if (priceNode != null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    priceNode.InnerText.Replace(",", "."), @"\d+\.\d{2}"
                );
                if (match.Success &&
                    decimal.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0)
                {
                    _logger.LogInformation("REWE HTML: {Product} → €{Price}", query, price);
                    return new ScraperResult(query, price, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "REWE HTML scraper fout voor {Product}", query);
        }

        return new ScraperResult(query, 0, false);
    }
}

// ─────────────────────────────────────────────────────────────────
//  EDEKA  (DE)
// ─────────────────────────────────────────────────────────────────
public class EdekaScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<EdekaScraper> _logger;

    public EdekaScraper(HttpClient http, ILogger<EdekaScraper> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/html, */*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "de-DE,de;q=0.9");
    }

    public async Task<ScraperResult> SearchProductAsync(string query)
    {
        // Edeka heeft een eigen zoek-API
        try
        {
            var url = $"https://www.edeka.de/api/search/v1/products" +
                      $"?q={Uri.EscapeDataString(query)}&page=0&size=5";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.edeka.de/");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return await TryEdekaHtml(query);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            JsonElement items;
            if (!doc.RootElement.TryGetProperty("products", out items) &&
                !doc.RootElement.TryGetProperty("items", out items) &&
                !doc.RootElement.TryGetProperty("content", out items))
                return await TryEdekaHtml(query);

            foreach (var item in items.EnumerateArray())
            {
                decimal price = 0;

                if (item.TryGetProperty("price", out var priceObj))
                {
                    if (priceObj.ValueKind == JsonValueKind.Number)
                        price = priceObj.GetDecimal();
                    else if (priceObj.TryGetProperty("value", out var v))
                        price = v.GetDecimal();
                    else if (priceObj.TryGetProperty("currentPrice", out var cp))
                        price = cp.GetDecimal();
                }

                if (price <= 0) continue;

                string title = "";
                if (item.TryGetProperty("title", out var t)) title = t.GetString() ?? query;
                else if (item.TryGetProperty("name", out var n)) title = n.GetString() ?? query;

                _logger.LogInformation("Edeka DE: {Product} → €{Price}", title, price);
                return new ScraperResult(title, price, true);
            }

            return await TryEdekaHtml(query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Edeka API mislukt voor {Product}", query);
            return await TryEdekaHtml(query);
        }
    }

    private async Task<ScraperResult> TryEdekaHtml(string query)
    {
        try
        {
            var url = $"https://www.edeka.de/produkte/suchergebnis.jsp?query={Uri.EscapeDataString(query)}";
            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Edeka prijsstructuur in HTML
            var priceNode = doc.DocumentNode.SelectSingleNode(
                "//span[contains(@class,'product-detail__price')] | " +
                "//p[contains(@class,'product-tile__price')] | " +
                "//span[contains(@class,'price-tag')]"
            );

            if (priceNode != null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    priceNode.InnerText.Replace(",", "."), @"\d+\.\d{2}"
                );
                if (match.Success &&
                    decimal.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0)
                {
                    _logger.LogInformation("Edeka HTML: {Product} → €{Price}", query, price);
                    return new ScraperResult(query, price, true);
                }
            }

            // Probeer JSON-LD structured data
            var jsonLd = doc.DocumentNode.SelectSingleNode("//script[@type='application/ld+json']");
            if (jsonLd != null)
            {
                using var ldDoc = JsonDocument.Parse(jsonLd.InnerText);
                if (ldDoc.RootElement.TryGetProperty("offers", out var offers))
                {
                    JsonElement priceEl;
                    if (offers.TryGetProperty("price", out priceEl) ||
                        (offers.ValueKind == JsonValueKind.Array &&
                         offers[0].TryGetProperty("price", out priceEl)))
                    {
                        string? priceStr = priceEl.ValueKind == JsonValueKind.String
                            ? priceEl.GetString()
                            : priceEl.GetDecimal().ToString(CultureInfo.InvariantCulture);

                        if (decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal p) && p > 0)
                            return new ScraperResult(query, p, true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Edeka HTML scraper fout voor {Product}", query);
        }

        return new ScraperResult(query, 0, false);
    }
}
