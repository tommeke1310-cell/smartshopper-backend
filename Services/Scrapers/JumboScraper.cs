using HtmlAgilityPack;
using SmartShopper.API.Models;
using System.Net;

namespace SmartShopper.API.Services.Scrapers;

public class JumboScraper
{
    private readonly HttpClient _http;
    private readonly ILogger<JumboScraper> _logger;

    public JumboScraper(HttpClient http, ILogger<JumboScraper> logger)
    {
        _http = http;
        _logger = logger;

        // Jumbo blokkeert alles wat niet op een browser lijkt
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    public async Task<ScraperResult> GetPriceAsync(string productName)
    {
        try
        {
            // Zoek URL van Jumbo
            string url = $"https://www.jumbo.com/zoeken?searchTerms={Uri.EscapeDataString(productName)}";

            var html = await _http.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // We zoeken naar het eerste product-item op de pagina
            // Jumbo gebruikt specifieke data-attributes voor hun prijzen
            var productNode = doc.DocumentNode.SelectSingleNode("//article[contains(@class, 'product-container')]");

            if (productNode == null)
            {
                _logger.LogWarning("Jumbo: Niets gevonden voor {Product}", productName);
                return new ScraperResult(productName, 0, false);
            }

            // Naam van het product
            var nameNode = productNode.SelectSingleNode(".//h3/a") ?? productNode.SelectSingleNode(".//a[contains(@class, 'link')]");
            string title = WebUtility.HtmlDecode(nameNode?.InnerText.Trim() ?? productName);

            // Prijs bij Jumbo staat vaak in een 'whole' en 'fractional' (centen) deel
            var euroNode = productNode.SelectSingleNode(".//span[@class='whole']");
            var centNode = productNode.SelectSingleNode(".//span[@class='fractional']");

            if (euroNode != null)
            {
                string priceString = euroNode.InnerText.Trim();
                if (centNode != null) priceString += "," + centNode.InnerText.Trim();

                if (decimal.TryParse(priceString, out decimal price))
                {
                    _logger.LogInformation("Jumbo: {Product} gevonden voor €{Price}", title, price);
                    return new ScraperResult(title, price, true);
                }
            }

            return new ScraperResult(productName, 0, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fout bij scrapen Jumbo voor {Product}", productName);
            return new ScraperResult(productName, 0, false);
        }
    }
}
