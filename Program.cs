using SmartShopper.API.Services;
using SmartShopper.API.Services.Scrapers;
using SmartShopper.API.Services.Routing;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

// ─── Railway environment variabelen direct injecteren ────────────
// ASP.NET Core leest Railway env vars via GoogleMaps__ApiKey notatie
// maar Railway gebruikt GOOGLE_MAPS_API_KEY — we mappen ze hier expliciet
var googleMapsKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY")
                 ?? Environment.GetEnvironmentVariable("GoogleMaps__ApiKey")
                 ?? builder.Configuration["GoogleMaps:ApiKey"]
                 ?? "";

var supabaseAnonKey = Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY")
                   ?? Environment.GetEnvironmentVariable("Supabase__AnonKey")
                   ?? builder.Configuration["Supabase:AnonKey"]
                   ?? "";

var supabaseServiceKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_KEY")
                      ?? Environment.GetEnvironmentVariable("Supabase__ServiceKey")
                      ?? builder.Configuration["Supabase:ServiceKey"]
                      ?? "";

// Injecteer als in-memory config zodat alle services ze kunnen lezen
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["GoogleMaps:ApiKey"]  = googleMapsKey,
    ["Supabase:AnonKey"]   = supabaseAnonKey,
    ["Supabase:ServiceKey"] = supabaseServiceKey,
});

Console.WriteLine($"🗺️  Google Maps key geladen: {(string.IsNullOrEmpty(googleMapsKey) ? "❌ NIET GEVONDEN" : $"✅ ({googleMapsKey[..Math.Min(8, googleMapsKey.Length)]}...)")}");
Console.WriteLine($"🗄️  Supabase key geladen: {(string.IsNullOrEmpty(supabaseAnonKey) ? "❌ NIET GEVONDEN" : "✅")}");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SmartShopper API", Version = "v1" });
});

// ─── CORS: alleen bekende origins toestaan ────────────────────────
// Voeg extra origins toe via de env var CORS_ALLOWED_ORIGINS (kommagescheiden).
// Standaard: Expo dev tools + productie app scheme.
var allowedOrigins = new List<string>
{
    "https://smartshopper.app",
    "http://localhost:8081",
    "http://localhost:19006",
    "exp://localhost:8081",
};

var extraOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
if (!string.IsNullOrWhiteSpace(extraOrigins))
    allowedOrigins.AddRange(extraOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

builder.Services.AddCors(o => o.AddPolicy("AllowApp",
    p => p
        .WithOrigins([.. allowedOrigins])
        .WithMethods("GET", "POST", "PATCH", "DELETE", "OPTIONS")
        .WithHeaders("Content-Type", "Authorization", "apikey", "x-client-info")
        .AllowCredentials()));

// ─── Helpers voor Polly retry + circuit breaker ────────────────────
static void AddScraperResilience(IHttpClientBuilder b) =>
    b.AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 3;
        o.Retry.Delay = TimeSpan.FromSeconds(1);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
        o.CircuitBreaker.FailureRatio = 0.5;
        o.CircuitBreaker.MinimumThroughput = 5;
        o.CircuitBreaker.BreakDuration = TimeSpan.FromMinutes(2);
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(25);
    });

static void AddJumboResilience(IHttpClientBuilder b) =>
    b.AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 1;
        o.Retry.Delay = TimeSpan.FromSeconds(1);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
        o.CircuitBreaker.FailureRatio = 0.9;
        o.CircuitBreaker.MinimumThroughput = 20;
        o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(6);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
    });

// ─── HTTP Clients per scraper MET Polly ───────────────────────────
AddScraperResilience(builder.Services.AddHttpClient<AlbertHeijnScraper>());
AddJumboResilience(builder.Services.AddHttpClient<JumboScraper>());
AddScraperResilience(builder.Services.AddHttpClient<LidlScraper>());
AddScraperResilience(builder.Services.AddHttpClient<AldiScraper>());
AddScraperResilience(builder.Services.AddHttpClient<DmScraper>());
AddScraperResilience(builder.Services.AddHttpClient<ReweScraper>());
AddScraperResilience(builder.Services.AddHttpClient<EdekaScraper>());
AddScraperResilience(builder.Services.AddHttpClient<ColruytScraper>());
AddScraperResilience(builder.Services.AddHttpClient<DelhaizeScraper>());

// ─── Business services ───────────────────────────────────────────
builder.Services.AddHttpClient<CompareService>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<IntelligenceService>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<SharedListService>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<RoutingService>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<ScraperHealthService>(c => c.Timeout = TimeSpan.FromSeconds(12));
builder.Services.AddHttpClient<ConsentService>(c => c.Timeout = TimeSpan.FromSeconds(10));

// ─── Scraper registraties (Scoped) ───────────────────────────────
builder.Services.AddScoped<AlbertHeijnScraper>();
builder.Services.AddScoped<JumboScraper>();
builder.Services.AddScoped<LidlScraper>();
builder.Services.AddScoped<AldiScraper>();
builder.Services.AddScoped<DmScraper>();
builder.Services.AddScoped<ReweScraper>();
builder.Services.AddScoped<EdekaScraper>();
builder.Services.AddScoped<ColruytScraper>();
builder.Services.AddScoped<DelhaizeScraper>();

// ─── Business services (Scoped) ──────────────────────────────────
builder.Services.AddScoped<CompareService>();
builder.Services.AddScoped<IntelligenceService>();
builder.Services.AddScoped<SharedListService>();
builder.Services.AddScoped<RoutingService>();
builder.Services.AddScoped<ConsentService>();

// ─── Health service (Singleton) ──────────────────────────────────
builder.Services.AddSingleton<ScraperHealthService>();

// ─── Achtergrond scraper ─────────────────────────────────────────
builder.Services.AddHostedService<BackgroundScraperService>();
builder.Services.AddHttpClient<BackgroundScraperService>(c => c.Timeout = TimeSpan.FromSeconds(30));

// ─── Cache + Memory ──────────────────────────────────────────────
builder.Services.AddMemoryCache();

// ─── Rate Limiting (10 compare-requests/minuut per IP) ───────────
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("compare", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = 10;
        o.QueueLimit = 0;
    });
    options.RejectionStatusCode = 429;
});

// ─── Logging ─────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(
    builder.Environment.IsProduction() ? LogLevel.Information : LogLevel.Debug);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartShopper API v1"));

app.UseCors("AllowApp");

// ─── API-key verificatie (minimale beveiliging voor compare/behavior) ─
// Stel in als Railway env var: API_SECRET_KEY=<willekeurige lange string>
// De app stuurt deze mee als header: X-Api-Key: <waarde>
// Als de env var leeg is → open toegang (achterwaarts compatibel voor dev)
var apiSecretKey = Environment.GetEnvironmentVariable("API_SECRET_KEY") ?? "";
if (!string.IsNullOrWhiteSpace(apiSecretKey))
{
    app.Use(async (context, next) =>
    {
        // Swagger en health endpoints zijn altijd vrij
        var path = context.Request.Path.Value ?? "";
        bool isPublic = path.StartsWith("/swagger") || path == "/health" || path == "/";
        if (!isPublic)
        {
            if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key) || key != apiSecretKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
        }
        await next();
    });
}

app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.MapGet("/health", () => new
{
    status = "ok",
    time = DateTime.UtcNow,
    version = "3.1.0",
    googleMaps = !string.IsNullOrEmpty(googleMapsKey) ? "configured" : "missing",
});

app.MapGet("/", () => new
{
    message = "SmartShopper API v3.1 draait!",
    time = DateTime.UtcNow,
    endpoints = new[]
    {
        "POST /api/compare",
        "GET  /api/health/scrapers",
        "POST /api/behavior",
        "POST /api/purchase",
        "GET  /api/recommendations",
        "GET  /api/lists/{userId}",
        "POST /api/lists",
        "POST /api/lists/{listId}/items",
        "PATCH /api/lists/{listId}/items/{itemId}",
        "DELETE /api/lists/{listId}/items/{itemId}",
        "POST /api/lists/{listId}/invite",
    }
});

var healthService = app.Services.GetRequiredService<ScraperHealthService>();
_ = Task.Run(() => healthService.RunHealthChecksAsync());

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
Console.WriteLine($"🛒 SmartShopper API v3.1 gestart op poort {port}");

app.Run($"http://0.0.0.0:{port}");
