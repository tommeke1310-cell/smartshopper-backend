using SmartShopper.API.Services;
using SmartShopper.API.Services.Scrapers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => {
    c.SwaggerDoc("v1", new() { Title = "SmartShopper API", Version = "v1" });
});

builder.Services.AddCors(o => o.AddPolicy("AllowAll",
    p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// HTTP clients met timeout per scraper
builder.Services.AddHttpClient<AlbertHeijnScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<JumboScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<LidlScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<AldiScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<DmScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<ReweScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<EdekaScraper>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<RoutingService>(c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient<FuelPriceService>(c => c.Timeout = TimeSpan.FromSeconds(5));

// CompareService krijgt eigen HttpClient voor Google Places/Distance Matrix
builder.Services.AddHttpClient<CompareService>(c => c.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddScoped<CompareService>();
builder.Services.AddScoped<LidlScraper>();
builder.Services.AddScoped<AldiScraper>();
builder.Services.AddScoped<DmScraper>();
builder.Services.AddScoped<ReweScraper>();
builder.Services.AddScoped<EdekaScraper>();
builder.Services.AddSingleton<FuelPriceService>();
builder.Services.AddMemoryCache();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartShopper API v1"));

app.UseCors("AllowAll");

app.UseAuthorization();
app.MapControllers();

app.MapGet("/health", () => new { status = "ok", time = DateTime.UtcNow });
app.MapGet("/", () => new { message = "SmartShopper API draait!", time = DateTime.UtcNow });

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
Console.WriteLine($"SmartShopper API gestart op poort {port}");

app.Run();
