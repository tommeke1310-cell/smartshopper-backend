using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services;

public class ConsentService
{
    private readonly HttpClient _http;
    private readonly ILogger<ConsentService> _logger;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private const string CURRENT_CONSENT_VERSION = "1.0";

    public ConsentService(HttpClient http, IConfiguration config, ILogger<ConsentService> logger)
    {
        _http = http;
        _logger = logger;
        _supabaseUrl = config["Supabase:Url"] ?? "";
        _supabaseKey = config["Supabase:ServiceKey"] ?? config["Supabase:AnonKey"] ?? "";

        if (!string.IsNullOrEmpty(_supabaseKey))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("apikey", _supabaseKey);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_supabaseKey}");
        }
    }

    // ─── Consent opslaan ─────────────────────────────────────────
    public async Task<bool> SaveConsentAsync(ConsentRequest req, string? ipAddress)
    {
        if (!req.AcceptedTerms)
        {
            _logger.LogWarning("Consent geweigerd voor {UserId} — terms niet geaccepteerd", req.UserId);
            return false;
        }

        var consent = new
        {
            user_id = req.UserId,
            accepted_terms = req.AcceptedTerms,
            allow_analytics = req.AllowAnalytics,
            allow_personalization = req.AllowPersonalization,
            allow_data_sharing = req.AllowDataSharing,
            consent_version = req.ConsentVersion,
            terms_version = "1.0",
            app_version = req.AppVersion,
            device_type = req.DeviceType,
            ip_hash = HashIp(ipAddress),        // nooit plaintext IP opslaan
            consent_given_at = DateTime.UtcNow,
            consent_updated_at = DateTime.UtcNow,
        };

        try
        {
            var response = await _http.PostAsJsonAsync(
                $"{_supabaseUrl}/rest/v1/user_consent?on_conflict=user_id",
                consent);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("Consent opslaan mislukt voor {UserId}: {Error}", req.UserId, err);
                return false;
            }

            _logger.LogInformation(
                "Consent opgeslagen voor {UserId}: analytics={A}, personalization={P}, sharing={S}",
                req.UserId, req.AllowAnalytics, req.AllowPersonalization, req.AllowDataSharing);

            // Log ook het consent event zelf (audit trail)
            await TrackEventAsync(new TrackingEvent
            {
                UserId = req.UserId,
                EventType = "consent_given",
                ConsentLevel = "none", // consent event zelf heeft geen consent nodig
                Metadata = new()
                {
                    ["analytics"] = req.AllowAnalytics.ToString(),
                    ["personalization"] = req.AllowPersonalization.ToString(),
                    ["data_sharing"] = req.AllowDataSharing.ToString(),
                    ["version"] = req.ConsentVersion,
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent opslaan fout voor {UserId}", req.UserId);
            return false;
        }
    }

    // ─── Consent ophalen ──────────────────────────────────────────
    public async Task<ConsentStatusResponse> GetConsentStatusAsync(string userId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"{_supabaseUrl}/rest/v1/user_consent?user_id=eq.{Uri.EscapeDataString(userId)}&select=*&limit=1");

            if (!response.IsSuccessStatusCode)
                return new ConsentStatusResponse { HasConsent = false };

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return new ConsentStatusResponse { HasConsent = false };

            var row = doc.RootElement[0];
            var version = row.TryGetProperty("consent_version", out var v) ? v.GetString() ?? "1.0" : "1.0";

            return new ConsentStatusResponse
            {
                HasConsent = true,
                AcceptedTerms = row.TryGetProperty("accepted_terms", out var at) && at.GetBoolean(),
                AllowAnalytics = row.TryGetProperty("allow_analytics", out var aa) && aa.GetBoolean(),
                AllowPersonalization = row.TryGetProperty("allow_personalization", out var ap) && ap.GetBoolean(),
                AllowDataSharing = row.TryGetProperty("allow_data_sharing", out var ads) && ads.GetBoolean(),
                ConsentGivenAt = row.TryGetProperty("consent_given_at", out var cga)
                    ? cga.GetDateTime() : null,
                ConsentVersion = version,
                NeedsUpdate = version != CURRENT_CONSENT_VERSION,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Consent ophalen mislukt voor {UserId}", userId);
            return new ConsentStatusResponse { HasConsent = false };
        }
    }

    // ─── Consent intrekken (GDPR recht) ──────────────────────────
    public async Task<bool> RevokeConsentAsync(string userId)
    {
        try
        {
            // Update consent naar alles false (we verwijderen niet — audit trail behouden)
            var response = await _http.PatchAsJsonAsync(
                $"{_supabaseUrl}/rest/v1/user_consent?user_id=eq.{Uri.EscapeDataString(userId)}",
                new
                {
                    allow_analytics = false,
                    allow_personalization = false,
                    allow_data_sharing = false,
                    consent_updated_at = DateTime.UtcNow,
                });

            // Verwijder ook alle persoonlijke tracking data
            await _http.DeleteAsync(
                $"{_supabaseUrl}/rest/v1/tracking_events?user_id=eq.{Uri.EscapeDataString(userId)}&consent_level=neq.none");

            _logger.LogInformation("Consent ingetrokken voor {UserId}", userId);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent intrekken mislukt voor {UserId}", userId);
            return false;
        }
    }

    // ─── Event tracken (consent-aware) ────────────────────────────
    // Dit is het centrale tracking punt — checkt altijd consent niveau
    public async Task TrackEventAsync(TrackingEvent evt)
    {
        try
        {
            // Altijd-toegestaan events (geen consent nodig)
            bool isAlwaysAllowed = evt.EventType is
                "consent_given" or "consent_revoked" or
                "app_open" or "app_crash" or "screen_view";

            if (!isAlwaysAllowed)
            {
                var consent = await GetConsentStatusAsync(evt.UserId);

                // Bepaal welk consent level nodig is
                bool canTrack = evt.ConsentLevel switch
                {
                    "analytics" => consent.AllowAnalytics,
                    "personalization" => consent.AllowPersonalization,
                    "full" => consent.AllowAnalytics && consent.AllowPersonalization,
                    _ => true // "none" events altijd toegestaan
                };

                if (!canTrack)
                {
                    _logger.LogDebug("Event {Type} niet getrackt voor {UserId} — geen consent",
                        evt.EventType, evt.UserId);
                    return;
                }

                // Anonimiseer als geen personalisatie consent
                if (!consent.AllowPersonalization)
                    evt.UserId = HashIp(evt.UserId) ?? evt.UserId; // pseudonimiseer
            }

            var record = new
            {
                user_id = evt.UserId,
                event_type = evt.EventType,
                screen = evt.Screen,
                product_name = evt.ProductName,
                store_chain = evt.StoreChain,
                country = evt.Country,
                price = evt.Price,
                search_query = evt.SearchQuery,
                result_count = evt.ResultCount,
                chosen_store = evt.ChosenStore,
                savings_amount = evt.SavingsAmount,
                metadata = JsonSerializer.Serialize(evt.Metadata),
                event_at = evt.EventAt,
                session_id = evt.SessionId,
                consent_level = evt.ConsentLevel,
            };

            await _http.PostAsJsonAsync($"{_supabaseUrl}/rest/v1/tracking_events", record);
        }
        catch (Exception ex)
        {
            // Tracking mag nooit de app breken
            _logger.LogDebug(ex, "Tracking event mislukt — wordt genegeerd");
        }
    }

    // ─── Helper: IP hashen voor privacy ──────────────────────────
    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip + "smartshopper-salt-2025"));
        return Convert.ToHexString(bytes)[..16]; // eerste 16 chars genoeg voor audit
    }
}
