using SmartShopper.API.Services;
using SmartShopper.API.Services.Scrapers;
using SmartShopper.API.Services.Routing;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SmartShopper API", Version = "v1" });
});

builder.Services.AddCors(o => o.AddPolicy("AllowAll",
    p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// ─── Helpers voor Polly retry + circuit breaker ────────────────────
static IHttpClientBuilder AddScraperResilience(IHttpClientBuilder b) =>
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

// ─── HTTP Clients per scraper MET Polly ───────────────────────────
AddScraperResilience(builder.Services.AddHttpClient<AlbertHeijnScraper>());
AddScraperResilience(builder.Services.AddHttpClient<JumboScraper>());
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

// ─── Health service (Singleton — houdt status bij) ───────────────
builder.Services.AddSingleton<ScraperHealthService>();

// ─── Achtergrond scraper (elke 6 uur alle producten scrapen) ─────
// Let op: BackgroundScraperService gebruikt zijn eigen HttpClients
// en heeft geen afhankelijkheid op de Scoped scrapers hierboven.
builder.Services.AddHostedService<BackgroundScraperService>();

// ─── Cache + Memory ──────────────────────────────────────────────
builder.Services.AddMemoryCache();

// ─── Logging ─────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(
    builder.Environment.IsProduction() ? LogLevel.Information : LogLevel.Debug);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartShopper API v1"));

app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

// ─── Health endpoints ─────────────────────────────────────────────
app.MapGet("/health", () => new
{
    status = "ok",
    time = DateTime.UtcNow,
    version = "3.0.0",
});

app.MapGet("/", () => new
{
    message = "SmartShopper API v3 draait!",
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

// ─── Start scraper health check op achtergrond ────────────────────
var healthService = app.Services.GetRequiredService<ScraperHealthService>();
_ = Task.Run(() => healthService.RunHealthChecksAsync());

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
Console.WriteLine($"🛒 SmartShopper API v3.0 gestart op poort {port}");

app.Run();
