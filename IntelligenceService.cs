using System.Text.Json;
using System.Net.Http.Json;
using System.Globalization;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services;

// ================================================================
//  IntelligenceService — slim winkeladvies op basis van gedrag
// ================================================================
public class IntelligenceService
{
    private readonly HttpClient _http;
    private readonly ILogger<IntelligenceService> _logger;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;

    public IntelligenceService(HttpClient http, ILogger<IntelligenceService> logger, IConfiguration config)
    {
        _http = http;
        _logger = logger;
        _supabaseUrl = config["Supabase:Url"] ?? "";
        _supabaseKey = config["Supabase:ServiceKey"] ?? config["Supabase:AnonKey"] ?? "";
    }

    // ── Gedrag opslaan ────────────────────────────────────────────
    // Wordt aangeroepen vanuit de app bij: scannen, toevoegen, vergelijken, kopen
    public async Task TrackBehaviorAsync(string userId, BehaviorEvent evt)
    {
        try
        {
            // Probeer eerst product_id op te zoeken via barcode of naam
            string? productId = null;
            if (!string.IsNullOrEmpty(evt.Barcode))
                productId = await GetProductIdByBarcode(evt.Barcode);
            if (productId == null && !string.IsNullOrEmpty(evt.ProductName))
                productId = await GetProductIdByName(evt.ProductName);

            var url = $"{_supabaseUrl}/rest/v1/user_behavior";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            AddHeaders(req);
            req.Content = JsonContent.Create(new
            {
                user_id      = userId,
                product_id   = productId,
                product_name = evt.ProductName,
                action       = evt.Action,        // 'scanned','added','purchased','compared'
                store_chain  = evt.StoreChain,
                country      = evt.Country,
                price_paid   = evt.PricePaid,
                quantity     = evt.Quantity,
                session_id   = evt.SessionId,
            });
            await _http.SendAsync(req);
            _logger.LogInformation("[Behavior] {User} → {Action} {Product}", userId, evt.Action, evt.ProductName);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "TrackBehavior mislukt"); }
    }

    // ── Aankoop registreren ───────────────────────────────────────
    public async Task<string?> SavePurchaseAsync(string userId, PurchaseRecord purchase)
    {
        try
        {
            // 1. Sla de purchase op
            var url = $"{_supabaseUrl}/rest/v1/purchase_history";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            AddHeaders(req);
            req.Headers.Add("Prefer", "return=representation");
            req.Content = JsonContent.Create(new
            {
                user_id     = userId,
                store_chain = purchase.StoreChain,
                country     = purchase.Country,
                city        = purchase.City,
                total_spent = purchase.TotalSpent,
                items_count = purchase.Items.Count,
                savings     = purchase.Savings,
            });

            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            string purchaseId = doc.RootElement[0].GetProperty("id").GetString() ?? "";

            // 2. Sla items op
            foreach (var item in purchase.Items)
            {
                var itemUrl = $"{_supabaseUrl}/rest/v1/purchase_items";
                using var itemReq = new HttpRequestMessage(HttpMethod.Post, itemUrl);
                AddHeaders(itemReq);
                string? productId = await GetProductIdByName(item.ProductName);
                itemReq.Content = JsonContent.Create(new
                {
                    purchase_id    = purchaseId,
                    product_id     = productId,
                    product_name   = item.ProductName,
                    quantity       = item.Quantity,
                    price_paid     = item.PricePaid,
                    cheapest_price = item.CheapestPrice,
                    cheapest_store = item.CheapestStore,
                });
                await _http.SendAsync(itemReq);

                // 3. Track als 'purchased' gedrag
                await TrackBehaviorAsync(userId, new BehaviorEvent
                {
                    ProductName = item.ProductName,
                    Action      = "purchased",
                    StoreChain  = purchase.StoreChain,
                    Country     = purchase.Country,
                    PricePaid   = item.PricePaid,
                    Quantity    = item.Quantity,
                    SessionId   = purchaseId,
                });
            }

            return purchaseId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SavePurchase mislukt voor {User}", userId);
            return null;
        }
    }

    // ── Aanbevelingen ophalen ─────────────────────────────────────
    public async Task<List<ProductRecommendation>> GetRecommendationsAsync(
        string userId, double lat, double lng)
    {
        try
        {
            var url = $"{_supabaseUrl}/rest/v1/rpc/get_recommendations";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            AddHeaders(req);
            req.Content = JsonContent.Create(new
            {
                p_user_id = userId,
                p_lat     = lat,
                p_lng     = lng,
                p_limit   = 10,
            });
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var recs = new List<ProductRecommendation>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                recs.Add(new ProductRecommendation
                {
                    ProductName  = item.TryGetProperty("product_name",  out var n)  ? n.GetString()  ?? "" : "",
                    ImageUrl     = item.TryGetProperty("image_url",     out var img) ? img.GetString() ?? "" : "",
                    BestChain    = item.TryGetProperty("best_chain",    out var c)  ? c.GetString()  ?? "" : "",
                    BestCountry  = item.TryGetProperty("best_country",  out var co) ? co.GetString() ?? "" : "",
                    BestPrice    = item.TryGetProperty("best_price",    out var bp) ? bp.GetDecimal() : 0,
                    NormalPrice  = item.TryGetProperty("normal_price",  out var np) ? np.GetDecimal() : 0,
                    SavingsPct   = item.TryGetProperty("savings_pct",   out var sp) ? sp.GetInt32()   : 0,
                    IsPromo      = item.TryGetProperty("is_promo",      out var ip) && ip.GetBoolean(),
                    TimesBought  = item.TryGetProperty("times_bought",  out var tb) ? tb.GetInt64()   : 0,
                    Reason       = item.TryGetProperty("reason",        out var r)  ? r.GetString()  ?? "" : "",
                });
            }

            // Vul aan met slim advies als gebruiker weinig geschiedenis heeft
            if (recs.Count < 5)
            {
                var popular = await GetPopularProductDeals(lat, lng);
                recs.AddRange(popular.Take(10 - recs.Count));
            }

            return recs;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRecommendations mislukt voor {User}", userId);
            return await GetPopularProductDeals(lat, lng);
        }
    }

    // ── Shoppatroon analyse ───────────────────────────────────────
    public async Task<ShoppingPattern> AnalyzePatternAsync(string userId)
    {
        try
        {
            // Haal aankopen van afgelopen 90 dagen op
            var url = $"{_supabaseUrl}/rest/v1/purchase_history" +
                      $"?user_id=eq.{userId}" +
                      $"&visited_at=gte.{DateTime.UtcNow.AddDays(-90):yyyy-MM-dd}" +
                      $"&order=visited_at.desc&limit=50";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(req);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var purchases = doc.RootElement.EnumerateArray().ToList();
            if (!purchases.Any()) return new ShoppingPattern();

            decimal totalSpent    = purchases.Sum(p => p.TryGetProperty("total_spent", out var ts) ? ts.GetDecimal() : 0);
            decimal totalSavings  = purchases.Sum(p => p.TryGetProperty("savings",     out var s)  ? s.GetDecimal()  : 0);
            var storeCounts = purchases
                .GroupBy(p => p.TryGetProperty("store_chain", out var c) ? c.GetString() ?? "" : "")
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());

            return new ShoppingPattern
            {
                TotalSpent90Days   = totalSpent,
                TotalSavings90Days = totalSavings,
                TotalVisits90Days  = purchases.Count,
                FavoriteStore      = storeCounts.FirstOrDefault().Key ?? "",
                StoreVisitCounts   = storeCounts,
                WeeklyBudget       = totalSpent / 13,  // 90 dagen ≈ 13 weken
                AvgSavingsPerTrip  = purchases.Count > 0 ? totalSavings / purchases.Count : 0,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnalyzePattern mislukt voor {User}", userId);
            return new ShoppingPattern();
        }
    }

    // ── Gemiste deals detecteren ──────────────────────────────────
    // Vergelijk wat gebruiker betaalde met wat goedkoopste was
    public async Task DetectMissedDealsAsync(string userId, string purchaseId)
    {
        try
        {
            // Haal items van deze aankoop op
            var url = $"{_supabaseUrl}/rest/v1/purchase_items?purchase_id=eq.{purchaseId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(req);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                decimal pricePaid     = item.TryGetProperty("price_paid",     out var pp) ? pp.GetDecimal() : 0;
                decimal cheapestPrice = item.TryGetProperty("cheapest_price", out var cp) ? cp.GetDecimal() : 0;
                string  cheapestStore = item.TryGetProperty("cheapest_store", out var cs) ? cs.GetString() ?? "" : "";
                string  productName   = item.TryGetProperty("product_name",   out var pn) ? pn.GetString() ?? "" : "";

                if (cheapestPrice <= 0 || pricePaid <= cheapestPrice * 1.05m) continue;

                decimal savingsPct = (pricePaid - cheapestPrice) / pricePaid * 100;
                if (savingsPct < 10) continue; // Alleen als >10% goedkoper

                // Sla op als gemiste deal
                var dealUrl = $"{_supabaseUrl}/rest/v1/missed_deals";
                using var dealReq = new HttpRequestMessage(HttpMethod.Post, dealUrl);
                AddHeaders(dealReq);
                string? productId = await GetProductIdByName(productName);
                dealReq.Content = JsonContent.Create(new
                {
                    user_id      = userId,
                    product_id   = productId,
                    product_name = productName,
                    store_chain  = cheapestStore,
                    country      = "NL",
                    deal_price   = cheapestPrice,
                    normal_price = pricePaid,
                    savings_pct  = (int)savingsPct,
                });
                await _http.SendAsync(dealReq);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DetectMissedDeals mislukt"); }
    }

    // ── Populaire deals (voor nieuwe gebruikers) ──────────────────
    private async Task<List<ProductRecommendation>> GetPopularProductDeals(double lat, double lng)
    {
        try
        {
            // Haal producten op die nu in de aanbieding zijn met grootste korting
            var url = $"{_supabaseUrl}/rest/v1/prices" +
                      "?is_promo=eq.true" +
                      "&order=scraped_at.desc&limit=20" +
                      "&select=*,products(name,image_url,category)";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(req);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var recs = new List<ProductRecommendation>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var product = item.TryGetProperty("products", out var p) ? p : default;
                recs.Add(new ProductRecommendation
                {
                    ProductName = product.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    ImageUrl    = product.TryGetProperty("image_url", out var img) ? img.GetString() ?? "" : "",
                    BestChain   = item.TryGetProperty("chain",   out var c)  ? c.GetString()   ?? "" : "",
                    BestCountry = item.TryGetProperty("country", out var co) ? co.GetString()  ?? "" : "",
                    BestPrice   = item.TryGetProperty("price",   out var bp) ? bp.GetDecimal()      : 0,
                    IsPromo     = true,
                    Reason      = "Nu in de aanbieding",
                });
            }
            return recs;
        }
        catch { return new List<ProductRecommendation>(); }
    }

    // ── Helpers ───────────────────────────────────────────────────
    private async Task<string?> GetProductIdByBarcode(string barcode)
    {
        try
        {
            var url = $"{_supabaseUrl}/rest/v1/products?barcode=eq.{barcode}&select=id&limit=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(req);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() > 0)
                return doc.RootElement[0].GetProperty("id").GetString();
        }
        catch { }
        return null;
    }

    private async Task<string?> GetProductIdByName(string name)
    {
        try
        {
            var url = $"{_supabaseUrl}/rest/v1/products?name=ilike.{Uri.EscapeDataString(name)}&select=id&limit=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(req);
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() > 0)
                return doc.RootElement[0].GetProperty("id").GetString();
        }
        catch { }
        return null;
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("apikey", _supabaseKey);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_supabaseKey}");
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
    }
}

// ================================================================
//  Models
// ================================================================
public class BehaviorEvent
{
    public string  ProductName { get; set; } = "";
    public string? Barcode     { get; set; }
    public string  Action      { get; set; } = "added";  // scanned/added/purchased/compared
    public string? StoreChain  { get; set; }
    public string? Country     { get; set; }
    public decimal PricePaid   { get; set; }
    public int     Quantity    { get; set; } = 1;
    public string? SessionId   { get; set; }
}

public class PurchaseRecord
{
    public string StoreChain { get; set; } = "";
    public string Country    { get; set; } = "NL";
    public string City       { get; set; } = "";
    public decimal TotalSpent  { get; set; }
    public decimal Savings     { get; set; }
    public List<PurchaseItemRecord> Items { get; set; } = new();
}

public class PurchaseItemRecord
{
    public string  ProductName   { get; set; } = "";
    public int     Quantity      { get; set; } = 1;
    public decimal PricePaid     { get; set; }
    public decimal CheapestPrice { get; set; }
    public string  CheapestStore { get; set; } = "";
}

public class ProductRecommendation
{
    public string  ProductName  { get; set; } = "";
    public string  ImageUrl     { get; set; } = "";
    public string  BestChain    { get; set; } = "";
    public string  BestCountry  { get; set; } = "";
    public decimal BestPrice    { get; set; }
    public decimal NormalPrice  { get; set; }
    public int     SavingsPct   { get; set; }
    public bool    IsPromo      { get; set; }
    public long    TimesBought  { get; set; }
    public string  Reason       { get; set; } = "";
}

public class ShoppingPattern
{
    public decimal TotalSpent90Days   { get; set; }
    public decimal TotalSavings90Days { get; set; }
    public int     TotalVisits90Days  { get; set; }
    public string  FavoriteStore      { get; set; } = "";
    public decimal WeeklyBudget       { get; set; }
    public decimal AvgSavingsPerTrip  { get; set; }
    public Dictionary<string, int> StoreVisitCounts { get; set; } = new();
}
