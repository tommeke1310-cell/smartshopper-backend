using Microsoft.AspNetCore.Mvc;
using SmartShopper.API.Models;
using SmartShopper.API.Services;

namespace SmartShopper.API.Controllers;

[ApiController]
[Route("api/lists")]
public class SharedListController : ControllerBase
{
    private readonly SharedListService           _service;
    private readonly ILogger<SharedListController> _logger;

    public SharedListController(SharedListService service, ILogger<SharedListController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    // GET /api/lists/{userId}
    [HttpGet("{userId}")]
    public async Task<IActionResult> GetListsForUser(string userId)
    {
        var lists = await _service.GetListsForUserAsync(userId);
        return Ok(lists);
    }

    // GET /api/lists/detail/{listId}
    [HttpGet("detail/{listId}")]
    public async Task<IActionResult> GetList(string listId)
    {
        var list = await _service.GetListAsync(listId);
        if (list == null) return NotFound();
        return Ok(list);
    }

    // POST /api/lists
    [HttpPost]
    public async Task<IActionResult> CreateList([FromBody] CreateSharedListRequest request)
    {
        if (string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.OwnerId))
            return BadRequest("Name en OwnerId zijn verplicht");

        var list = await _service.CreateListAsync(request);
        if (list == null) return StatusCode(500, "Lijst aanmaken mislukt");
        return CreatedAtAction(nameof(GetList), new { listId = list.Id }, list);
    }

    // POST /api/lists/{listId}/items
    [HttpPost("{listId}/items")]
    public async Task<IActionResult> AddItem(string listId, [FromBody] AddItemToListRequest request)
    {
        if (string.IsNullOrEmpty(request.Item?.Name))
            return BadRequest("Item naam is verplicht");

        var item = await _service.AddItemAsync(listId, request);
        if (item == null) return StatusCode(500, "Item toevoegen mislukt");
        return Ok(item);
    }

    // PATCH /api/lists/{listId}/items/{itemId}
    [HttpPatch("{listId}/items/{itemId}")]
    public async Task<IActionResult> UpdateItem(string listId, string itemId, [FromBody] UpdateItemRequest request)
    {
        var success = await _service.UpdateItemAsync(listId, itemId, request);
        return success ? Ok() : StatusCode(500, "Item updaten mislukt");
    }

    // DELETE /api/lists/{listId}/items/{itemId}
    [HttpDelete("{listId}/items/{itemId}")]
    public async Task<IActionResult> DeleteItem(string listId, string itemId)
    {
        var success = await _service.DeleteItemAsync(listId, itemId);
        return success ? Ok() : NotFound();
    }

    // DELETE /api/lists/{listId}/items/checked?userId=xxx
    [HttpDelete("{listId}/items/checked")]
    public async Task<IActionResult> ClearChecked(string listId)
    {
        var count = await _service.ClearCheckedItemsAsync(listId);
        return Ok(new { removed = count });
    }

    // POST /api/lists/{listId}/invite
    [HttpPost("{listId}/invite")]
    public async Task<IActionResult> InviteMember(string listId, [FromBody] InviteMemberRequest request)
    {
        if (string.IsNullOrEmpty(request.InviteEmail))
            return BadRequest("Email is verplicht");

        var success = await _service.InviteMemberAsync(listId, request);
        return success ? Ok() : BadRequest("Uitnodiging mislukt — controleer het e-mailadres");
    }

    // DELETE /api/lists/{listId}?userId=xxx
    [HttpDelete("{listId}")]
    public async Task<IActionResult> DeleteList(string listId, [FromQuery] string userId)
    {
        if (string.IsNullOrEmpty(userId)) return BadRequest("userId is verplicht");
        var success = await _service.DeleteListAsync(listId, userId);
        return success ? Ok() : Forbid();
    }
}
