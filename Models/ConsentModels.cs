namespace SmartShopper.API.Models;

// ─── CONSENT LEVELS ───────────────────────────────────────────────
// Wat we ALTIJD mogen tracken (geen toestemming nodig):
//   - App crashes / technische errors
//   - Anonieme sessie-duur
//   - Welk scherm werd geopend (geen persoonsdata)
//
// Wat we mogen met toestemming:
//   - Analytisch: zoekgedrag, welke producten vergeleken, welke winkel gekozen
//   - Personalisatie: aankoophistorie, favoriete winkels, budgetgedrag
//   - Marketing: data delen met partners / verkopen (geanonimiseerd)

public class UserConsent
{
    public string UserId { get; set; } = "";

    // Verplicht — altijd true, gebruiker moet akkoord gaan met basisvoorwaarden
    public bool AcceptedTerms { get; set; } = false;
    public DateTime TermsAcceptedAt { get; set; }
    public string TermsVersion { get; set; } = "1.0";

    // Optioneel — gebruiker kiest zelf
    public bool AllowAnalytics { get; set; } = false;         // zoekgedrag, vergelijkingen, sessies
    public bool AllowPersonalization { get; set; } = false;   // aankoophistorie, aanbevelingen
    public bool AllowDataSharing { get; set; } = false;       // geanonimiseerde data verkopen/delen

    public DateTime ConsentGivenAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConsentUpdatedAt { get; set; }
    public string ConsentVersion { get; set; } = "1.0";
    public string? DeviceType { get; set; }
    public string? AppVersion { get; set; }
    public string? IpHash { get; set; } // gehashed IP voor bewijs, nooit plaintext
}

public class ConsentRequest
{
    public string UserId { get; set; } = "";
    public bool AcceptedTerms { get; set; }
    public bool AllowAnalytics { get; set; }
    public bool AllowPersonalization { get; set; }
    public bool AllowDataSharing { get; set; }
    public string? AppVersion { get; set; }
    public string? DeviceType { get; set; }
    public string ConsentVersion { get; set; } = "1.0";
}

public class ConsentStatusResponse
{
    public bool HasConsent { get; set; }
    public bool AcceptedTerms { get; set; }
    public bool AllowAnalytics { get; set; }
    public bool AllowPersonalization { get; set; }
    public bool AllowDataSharing { get; set; }
    public DateTime? ConsentGivenAt { get; set; }
    public string ConsentVersion { get; set; } = "1.0";
    public bool NeedsUpdate { get; set; } // true als er een nieuwe versie is
}

// Wat we tracken per event — gelinkt aan consent level
public class TrackingEvent
{
    public string UserId { get; set; } = "";
    public string EventType { get; set; } = "";    // search, compare, purchase, screen_view etc.
    public string? Screen { get; set; }
    public string? ProductName { get; set; }
    public string? StoreChain { get; set; }
    public string? Country { get; set; }
    public decimal? Price { get; set; }
    public string? SearchQuery { get; set; }
    public int? ResultCount { get; set; }
    public string? ChosenStore { get; set; }
    public decimal? SavingsAmount { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public DateTime EventAt { get; set; } = DateTime.UtcNow;
    public string? SessionId { get; set; }

    // Consent level waarmee dit event is opgeslagen
    public string ConsentLevel { get; set; } = "none"; // none | analytics | personalization | full
}
