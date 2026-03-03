using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

public class AlbertHeijnScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<AlbertHeijnScraper> _logger;

    public AlbertHeijnScraper(HttpClient http, ILogger<AlbertHeijnScraper> logger)
    {
        _http = http;
        _logger = logger;
        // AH blokkeert soms standaard scripts, dus we doen alsof we een browser zijn
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
    }

    public async Task<ScraperResult> GetPriceAsync(string productName)
    {
        try
        {
            // We gebruiken de officiële zoek-URL van AH
            var searchUrl = $"https://www.ah.nl/zoeken/api/v1/search?query={Uri.EscapeDataString(productName)}";

            var response = await _http.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode) return new ScraperResult(productName, 0, false);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // We duiken in de JSON-structuur van AH om de prijs van het eerste product te vinden
            var lane = doc.RootElement.GetProperty("navigation"); // AH noemt categorieën 'lanes'
            var firstProduct = doc.RootElement
                .GetProperty("cards")[0]
                .GetProperty("products")[0];

            var price = firstProduct.GetProperty("price").GetProperty("now").GetDecimal();
            var title = firstProduct.GetProperty("title").GetString();

            _logger.LogInformation("AH: {Product} gevonden voor €{Price}", title, price);

            return new ScraperResult(title ?? productName, price, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij scrapen AH voor {Product}", productName);
            return new ScraperResult(productName, 0, false);
        }
    }
}