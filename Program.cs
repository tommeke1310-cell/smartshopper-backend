using SmartShopper.API.Services;
using SmartShopper.API.Services.Scrapers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SmartShopper API", Version = "v1" });
});

builder.Services.AddCors(o => o.AddPolicy("AllowAll",
    p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// ─── HTTP Clients per scraper ─────────────────────────────────────
builder.Services.AddHttpClient<AlbertHeijnScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<JumboScraper>(      c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<LidlScraper>(       c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<AldiScraper>(       c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<DmScraper>(         c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<ReweScraper>(       c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<EdekaScraper>(      c => c.Timeout = TimeSpan.FromSeconds(8));

// ─── CompareService krijgt eigen HttpClient voor Google Places ────
builder.Services.AddHttpClient<CompareService>(    c => c.Timeout = TimeSpan.FromSeconds(10));

// ─── IntelligenceService ──────────────────────────────────────────
builder.Services.AddHttpClient<IntelligenceService>(c => c.Timeout = TimeSpan.FromSeconds(8));

// ─── SharedListService ────────────────────────────────────────────
builder.Services.AddHttpClient<SharedListService>( c => c.Timeout = TimeSpan.FromSeconds(8));

// ─── Scraper registraties (Scoped) ───────────────────────────────
builder.Services.AddScoped<AlbertHeijnScraper>();
builder.Services.AddScoped<JumboScraper>();
builder.Services.AddScoped<LidlScraper>();
builder.Services.AddScoped<AldiScraper>();
builder.Services.AddScoped<DmScraper>();
builder.Services.AddScoped<ReweScraper>();
builder.Services.AddScoped<EdekaScraper>();

// ─── Business services ───────────────────────────────────────────
builder.Services.AddScoped<CompareService>();
builder.Services.AddScoped<IntelligenceService>();
builder.Services.AddScoped<SharedListService>();

// ─── Cache ───────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

// ─── Logging ─────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (builder.Environment.IsProduction())
    builder.Logging.SetMinimumLevel(LogLevel.Information);
else
    builder.Logging.SetMinimumLevel(LogLevel.Debug);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartShopper API v1"));

app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

// Health / root endpoints
app.MapGet("/health", () => new
{
    status  = "ok",
    time    = DateTime.UtcNow,
    version = "2.0.0",
});

app.MapGet("/", () => new
{
    message = "SmartShopper API draait!",
    time    = DateTime.UtcNow,
    endpoints = new[]
    {
        "POST /api/compare",
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

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
Console.WriteLine($"🛒 SmartShopper API v2.0 gestart op poort {port}");

app.Run();
