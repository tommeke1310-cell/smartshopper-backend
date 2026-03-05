using System.Net.Http.Json;
using System.Text.Json;
using SmartShopper.API.Models;

namespace SmartShopper.API.Services;

/// <summary>
/// Beheert gedeelde boodschappenlijsten voor gezinsaccounts.
/// Opslag: Supabase tabel 'shared_lists' + 'shared_list_items'.
/// Real-time updates worden afgehandeld via Supabase Realtime (in de app).
/// </summary>
public class SharedListService
{
    private readonly HttpClient               _http;
    private readonly IConfiguration          _config;
    private readonly ILogger<SharedListService> _logger;

    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;

    public SharedListService(HttpClient http, IConfiguration config, ILogger<SharedListService> logger)
    {
        _http        = http;
        _config      = config;
        _logger      = logger;
        _supabaseUrl = config["Supabase:Url"]        ?? "";
        _supabaseKey = config["Supabase:ServiceKey"] ?? config["Supabase:AnonKey"] ?? "";

        _http.DefaultRequestHeaders.TryAddWithoutValidation("apikey",         _supabaseKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",  $"Bearer {_supabaseKey}");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Prefer",         "return=representation");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type",   "application/json");
    }

    // ─── LIJST AANMAKEN ──────────────────────────────────────────

    public async Task<SharedList?> CreateListAsync(CreateSharedListRequest request)
    {
        try
        {
            var listId = Guid.NewGuid().ToString();
            var resp   = await _http.PostAsJsonAsync($"{_supabaseUrl}/rest/v1/shared_lists", new
            {
                id         = listId,
                name       = request.Name,
                owner_id   = request.OwnerId,
                member_ids = request.MemberIds,
                created_at = DateTime.UtcNow,
                updated_at = DateTime.UtcNow,
            });

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Lijst aanmaken mislukt: {Status}", resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            return ParseListFromJson(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateListAsync fout");
            return null;
        }
    }

    // ─── LIJSTEN VAN EEN GEBRUIKER ───────────────────────────────

    public async Task<List<SharedList>> GetListsForUserAsync(string userId)
    {
        try
        {
            // Haal lijsten op waarbij de gebruiker eigenaar of lid is
            var url  = $"{_supabaseUrl}/rest/v1/shared_lists" +
                       $"?or=(owner_id.eq.{userId},member_ids.cs.{{\"\\\"{ userId}\\\"\"}})" +
                       $"&order=updated_at.desc";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return [];

            var json  = await resp.Content.ReadAsStringAsync();
            var lists = ParseListsFromJson(json);

            // Haal items op voor elke lijst
            foreach (var list in lists)
                list.Items = await GetItemsForListAsync(list.Id);

            return lists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetListsForUser fout voor {UserId}", userId);
            return [];
        }
    }

    // ─── LIJST OPHALEN ───────────────────────────────────────────

    public async Task<SharedList?> GetListAsync(string listId)
    {
        try
        {
            var resp = await _http.GetAsync($"{_supabaseUrl}/rest/v1/shared_lists?id=eq.{listId}");
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            var list = ParseListFromJson(json);
            if (list == null) return null;

            list.Items = await GetItemsForListAsync(listId);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetListAsync fout voor {ListId}", listId);
            return null;
        }
    }

    // ─── ITEM TOEVOEGEN ──────────────────────────────────────────

    public async Task<SharedListItem?> AddItemAsync(string listId, AddItemToListRequest request)
    {
        try
        {
            var item = request.Item;
            item.Id          = Guid.NewGuid().ToString();
            item.AddedById   = request.UserId;
            item.AddedByName = request.UserName;
            item.AddedAt     = DateTime.UtcNow;
            item.Checked     = false;

            var resp = await _http.PostAsJsonAsync($"{_supabaseUrl}/rest/v1/shared_list_items", new
            {
                id            = item.Id,
                list_id       = listId,
                name          = item.Name,
                quantity      = item.Quantity,
                unit          = item.Unit,
                @checked      = false,
                added_by_id   = item.AddedById,
                added_by_name = item.AddedByName,
                image_url     = item.ImageUrl,
                category      = item.Category,
                emoji         = item.Emoji,
                added_at      = item.AddedAt,
            });

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Item toevoegen mislukt: {Status}", resp.StatusCode);
                return null;
            }

            // Bijwerken van updated_at op de lijst
            await TouchListAsync(listId);

            return item;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddItemAsync fout");
            return null;
        }
    }

    // ─── ITEM UPDATEN (aanvinken / hoeveelheid) ──────────────────

    public async Task<bool> UpdateItemAsync(string listId, string itemId, UpdateItemRequest request)
    {
        try
        {
            var patch = new Dictionary<string, object?>();
            if (request.Checked.HasValue)  patch["checked"]  = request.Checked.Value;
            if (request.Quantity.HasValue) patch["quantity"] = request.Quantity.Value;

            if (!patch.Any()) return true;

            var resp = await _http.PatchAsJsonAsync(
                $"{_supabaseUrl}/rest/v1/shared_list_items?id=eq.{itemId}&list_id=eq.{listId}",
                patch);

            if (resp.IsSuccessStatusCode)
                await TouchListAsync(listId);

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateItemAsync fout");
            return false;
        }
    }

    // ─── ITEM VERWIJDEREN ────────────────────────────────────────

    public async Task<bool> DeleteItemAsync(string listId, string itemId)
    {
        try
        {
            var resp = await _http.DeleteAsync(
                $"{_supabaseUrl}/rest/v1/shared_list_items?id=eq.{itemId}&list_id=eq.{listId}");
            if (resp.IsSuccessStatusCode) await TouchListAsync(listId);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteItemAsync fout");
            return false;
        }
    }

    // ─── LIJST LEEGMAKEN (aangevinkte items verwijderen) ─────────

    public async Task<int> ClearCheckedItemsAsync(string listId)
    {
        try
        {
            // Tellen eerst
            var countResp = await _http.GetAsync(
                $"{_supabaseUrl}/rest/v1/shared_list_items?list_id=eq.{listId}&checked=eq.true&select=id");
            if (!countResp.IsSuccessStatusCode) return 0;

            var countJson = await countResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(countJson);
            int count = doc.RootElement.GetArrayLength();

            // Dan verwijderen
            var delResp = await _http.DeleteAsync(
                $"{_supabaseUrl}/rest/v1/shared_list_items?list_id=eq.{listId}&checked=eq.true");

            if (delResp.IsSuccessStatusCode) await TouchListAsync(listId);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClearCheckedItemsAsync fout");
            return 0;
        }
    }

    // ─── LID UITNODIGEN ──────────────────────────────────────────

    public async Task<bool> InviteMemberAsync(string listId, InviteMemberRequest request)
    {
        try
        {
            // Lookup userId via email in Supabase auth
            var emailResp = await _http.GetAsync(
                $"{_supabaseUrl}/rest/v1/profiles?email=eq.{Uri.EscapeDataString(request.InviteEmail)}&select=id");
            if (!emailResp.IsSuccessStatusCode) return false;

            var emailJson = await emailResp.Content.ReadAsStringAsync();
            using var emailDoc = JsonDocument.Parse(emailJson);
            if (emailDoc.RootElement.GetArrayLength() == 0) return false;

            var newMemberId = emailDoc.RootElement[0].GetProperty("id").GetString() ?? "";
            if (string.IsNullOrEmpty(newMemberId)) return false;

            // Append userId to member_ids array via Supabase RPC
            var rpcResp = await _http.PostAsJsonAsync($"{_supabaseUrl}/rest/v1/rpc/add_list_member", new
            {
                p_list_id  = listId,
                p_user_id  = newMemberId,
            });

            return rpcResp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InviteMemberAsync fout");
            return false;
        }
    }

    // ─── LIJST VERWIJDEREN ───────────────────────────────────────

    public async Task<bool> DeleteListAsync(string listId, string requestingUserId)
    {
        try
        {
            // Alleen eigenaar mag verwijderen
            var list = await GetListAsync(listId);
            if (list == null || list.OwnerId != requestingUserId) return false;

            await _http.DeleteAsync($"{_supabaseUrl}/rest/v1/shared_list_items?list_id=eq.{listId}");
            var resp = await _http.DeleteAsync($"{_supabaseUrl}/rest/v1/shared_lists?id=eq.{listId}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteListAsync fout");
            return false;
        }
    }

    // ─── HELPERS ─────────────────────────────────────────────────

    private async Task<List<SharedListItem>> GetItemsForListAsync(string listId)
    {
        try
        {
            var resp = await _http.GetAsync(
                $"{_supabaseUrl}/rest/v1/shared_list_items?list_id=eq.{listId}&order=added_at.asc");
            if (!resp.IsSuccessStatusCode) return [];

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.EnumerateArray().Select(item => new SharedListItem
            {
                Id          = item.TryGetProperty("id",            out var id)   ? id.GetString()   ?? "" : "",
                Name        = item.TryGetProperty("name",          out var n)    ? n.GetString()    ?? "" : "",
                Quantity    = item.TryGetProperty("quantity",      out var q)    ? q.GetDecimal()        : 1,
                Unit        = item.TryGetProperty("unit",          out var u)    ? u.GetString()    ?? "stuk" : "stuk",
                Checked     = item.TryGetProperty("checked",       out var c)    && c.GetBoolean(),
                AddedById   = item.TryGetProperty("added_by_id",   out var abId) ? abId.GetString() ?? "" : "",
                AddedByName = item.TryGetProperty("added_by_name", out var abn)  ? abn.GetString()  ?? "" : "",
                ImageUrl    = item.TryGetProperty("image_url",     out var img)  ? img.GetString()       : null,
                Category    = item.TryGetProperty("category",      out var cat)  ? cat.GetString()       : null,
                Emoji       = item.TryGetProperty("emoji",         out var em)   ? em.GetString()   ?? "🛒" : "🛒",
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetItemsForListAsync fout");
            return [];
        }
    }

    private async Task TouchListAsync(string listId)
    {
        try
        {
            await _http.PatchAsJsonAsync(
                $"{_supabaseUrl}/rest/v1/shared_lists?id=eq.{listId}",
                new { updated_at = DateTime.UtcNow });
        }
        catch { /* niet kritiek */ }
    }

    private static SharedList? ParseListFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : (JsonElement?)null
                : doc.RootElement;

            if (root == null) return null;
            var el = root.Value;

            return new SharedList
            {
                Id       = el.TryGetProperty("id",        out var id)  ? id.GetString()  ?? "" : "",
                Name     = el.TryGetProperty("name",      out var n)   ? n.GetString()   ?? "" : "",
                OwnerId  = el.TryGetProperty("owner_id",  out var oid) ? oid.GetString() ?? "" : "",
                MemberIds = el.TryGetProperty("member_ids", out var mid) && mid.ValueKind == JsonValueKind.Array
                    ? mid.EnumerateArray().Select(m => m.GetString() ?? "").Where(s => s != "").ToList()
                    : new List<string>(),
            };
        }
        catch { return null; }
    }

    private static List<SharedList> ParseListsFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Select(el => new SharedList
                {
                    Id       = el.TryGetProperty("id",        out var id)  ? id.GetString()  ?? "" : "",
                    Name     = el.TryGetProperty("name",      out var n)   ? n.GetString()   ?? "" : "",
                    OwnerId  = el.TryGetProperty("owner_id",  out var oid) ? oid.GetString() ?? "" : "",
                    MemberIds = el.TryGetProperty("member_ids", out var mid) && mid.ValueKind == JsonValueKind.Array
                        ? mid.EnumerateArray().Select(m => m.GetString() ?? "").Where(s => s != "").ToList()
                        : new List<string>(),
                }).ToList();
        }
        catch { return []; }
    }
}
