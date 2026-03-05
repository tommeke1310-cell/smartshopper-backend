using Microsoft.AspNetCore.Mvc;

namespace SmartShopper.API.Controllers;

/// <summary>
/// Utility endpoints die de frontend aanroept maar nog geen volledige
/// implementatie hebben. Geeft een nette 200-respons zodat de app
/// niet crasht op een 404.
/// </summary>
[ApiController]
[Route("api")]
public class UtilityController : ControllerBase
{
    private readonly ILogger<UtilityController> _logger;

    public UtilityController(ILogger<UtilityController> logger)
    {
        _logger = logger;
    }

    // POST /api/receipt/ocr
    // Frontend stuurt base64-afbeelding van een kassabon op.
    // TODO: echte OCR implementatie (bijv. Google Vision API of Azure OCR).
    // Nu: geeft success=false terug zodat de app de on-device fallback gebruikt.
    [HttpPost("receipt/ocr")]
    public IActionResult ReceiptOcr([FromBody] ReceiptOcrRequest req)
    {
        _logger.LogInformation("Receipt OCR aangevraagd — nog niet geïmplementeerd, app gebruikt on-device fallback");
        return Ok(new { success = false, text = (string?)null });
    }

    // POST /api/push/register
    // Frontend registreert een Expo push token voor notificaties.
    // TODO: token opslaan in Supabase + push-notificaties versturen.
    // Nu: accepteert het token en logt het, stuurt 200 terug.
    [HttpPost("push/register")]
    public IActionResult RegisterPushToken([FromBody] PushRegisterRequest req)
    {
        if (string.IsNullOrEmpty(req.Token))
            return BadRequest("Token is verplicht");

        _logger.LogInformation("Push token geregistreerd voor gebruiker {UserId}: {Token}",
            req.UserId ?? "anoniem", req.Token[..Math.Min(20, req.Token.Length)] + "…");

        return Ok(new { success = true });
    }
}

public class ReceiptOcrRequest
{
    public string? Image    { get; set; }
    public string? MimeType { get; set; }
}

public class PushRegisterRequest
{
    public string? Token  { get; set; }
    public string? UserId { get; set; }
}
