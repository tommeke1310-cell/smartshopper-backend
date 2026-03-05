using Microsoft.AspNetCore.Mvc;
using SmartShopper.API.Models;
using SmartShopper.API.Services;

namespace SmartShopper.API.Controllers;

[ApiController]
[Route("api/consent")]
public class ConsentController : ControllerBase
{
    private readonly ConsentService _consent;
    private readonly ILogger<ConsentController> _logger;

    public ConsentController(ConsentService consent, ILogger<ConsentController> logger)
    {
        _consent = consent;
        _logger = logger;
    }

    // GET /api/consent/{userId}
    // App checkt dit bij opstarten — toont popup als HasConsent = false
    [HttpGet("{userId}")]
    public async Task<IActionResult> GetStatus(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return BadRequest("userId is verplicht");
        var status = await _consent.GetConsentStatusAsync(userId);
        return Ok(status);
    }

    // POST /api/consent
    // Gebruiker geeft toestemming via popup
    [HttpPost]
    public async Task<IActionResult> SaveConsent([FromBody] ConsentRequest req)
    {
        if (string.IsNullOrEmpty(req.UserId))
            return BadRequest("userId is verplicht");

        if (!req.AcceptedTerms)
            return BadRequest("Gebruiksvoorwaarden moeten geaccepteerd worden om de app te gebruiken");

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var success = await _consent.SaveConsentAsync(req, ipAddress);

        if (!success)
            return StatusCode(500, "Consent opslaan mislukt");

        return Ok(new { success = true, message = "Consent opgeslagen" });
    }

    // PATCH /api/consent/{userId}
    // Gebruiker past voorkeuren aan in instellingen
    [HttpPatch("{userId}")]
    public async Task<IActionResult> UpdateConsent(string userId, [FromBody] ConsentRequest req)
    {
        req.UserId = userId;
        if (!req.AcceptedTerms)
            return BadRequest("Gebruiksvoorwaarden kunnen niet worden ingetrokken — verwijder dan je account");

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var success = await _consent.SaveConsentAsync(req, ipAddress);
        return success ? Ok(new { success = true }) : StatusCode(500, "Update mislukt");
    }

    // DELETE /api/consent/{userId}
    // GDPR: recht op intrekking — verwijdert tracking data
    [HttpDelete("{userId}")]
    public async Task<IActionResult> RevokeConsent(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return BadRequest();
        var success = await _consent.RevokeConsentAsync(userId);
        return success ? Ok(new { success = true, message = "Alle toestemming ingetrokken en data verwijderd" })
                       : StatusCode(500, "Intrekken mislukt");
    }

    // POST /api/consent/track
    // Frontend stuurt tracking events
    [HttpPost("track")]
    public async Task<IActionResult> Track([FromBody] TrackingEvent evt)
    {
        if (string.IsNullOrEmpty(evt.UserId) || string.IsNullOrEmpty(evt.EventType))
            return BadRequest("userId en eventType zijn verplicht");

        await _consent.TrackEventAsync(evt);
        return Ok(new { success = true });
    }
}
