using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Messages;

// Matches MessageLog and MessageSummary in models.ts.
public record MessageDto(
    string Id, string? StudentId, string RecipientName, string Phone, string Type,
    string Text, string Status, string? Error, string CreatedAt, string UpdatedAt);

public record MessageSummary(int Total, Dictionary<string, int> ByStatus);

[ApiController]
[Route("api/v1/messages")]
[Authorize(Roles = "Owner,Staff")]
public class MessagesController(ClassKendraContext db, MessageService messages) : ControllerBase
{
    private static readonly string[] Statuses =
        [MessageStatus.Queued, MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read, MessageStatus.Failed];

    /// <summary>GET /messages?type=&status=&studentId=&search= — newest 200.</summary>
    [HttpGet]
    public async Task<ActionResult<List<MessageDto>>> List([FromQuery] string? type, [FromQuery] string? status,
        [FromQuery] Guid? studentId, [FromQuery] string? search, CancellationToken ct)
    {
        var q = db.Messages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(m => m.Type == type);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(m => m.Status == status);
        if (studentId is Guid s) q = q.Where(m => m.StudentId == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(m => m.RecipientName.Contains(term) || m.Phone.Contains(term) || m.Text.Contains(term));
        }

        var rows = await q.OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>GET /messages/summary?days=30</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<MessageSummary>> Summary([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 366));
        var counts = await db.Messages.AsNoTracking()
            .Where(m => m.CreatedAt >= since)
            .GroupBy(m => m.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var byStatus = Statuses.ToDictionary(st => st, st => counts.GetValueOrDefault(st));
        return new MessageSummary(byStatus.Values.Sum(), byStatus);
    }

    /// <summary>POST /messages/{id}/retry — puts a failed message back in the outbox.</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<ActionResult<MessageDto>> Retry(Guid id, CancellationToken ct)
    {
        var m = await db.Messages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return NotFound(new { message = "Message not found." });
        if (m.Status != MessageStatus.Failed) return ToDto(m);
        if (!await messages.RetryAsync(m, ct))
            return BadRequest(new { message = "This month's WhatsApp messages are used up. Upgrade your plan to send more." });
        return ToDto(m);
    }

    private static string Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O");

    private static MessageDto ToDto(Message m) => new(
        m.Id.ToString(), m.StudentId?.ToString(), m.RecipientName, m.Phone, m.Type,
        m.Text, m.Status, m.Error, Utc(m.CreatedAt), Utc(m.UpdatedAt));
}

/// <summary>
/// Delivery updates from WhatsApp (Meta). Point the app's webhook at /api/v1/webhooks/whatsapp.
/// Settings: WhatsApp:VerifyToken (any string you choose), WhatsApp:AppSecret.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/whatsapp")]
[AllowAnonymous]
public class WhatsAppWebhookController(ClassKendraContext db, IConfiguration config, ILogger<WhatsAppWebhookController> log)
    : ControllerBase
{
    private static readonly string[] Rank = [MessageStatus.Queued, MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read];

    [HttpGet]
    public IActionResult Verify([FromQuery(Name = "hub.mode")] string? mode,
                                [FromQuery(Name = "hub.verify_token")] string? token,
                                [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var expected = config["WhatsApp:VerifyToken"];
        return mode == "subscribe" && !string.IsNullOrEmpty(expected) && token == expected
            ? Content(challenge ?? "", "text/plain")
            : Forbid();
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        if (!ValidSignature(body, Request.Headers["X-Hub-Signature-256"].ToString()))
            return Unauthorized();

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { return Ok(); }

        foreach (var entry in root?["entry"]?.AsArray() ?? [])
        foreach (var change in entry?["changes"]?.AsArray() ?? [])
        foreach (var st in change?["value"]?["statuses"]?.AsArray() ?? [])
        {
            var id = st?["id"]?.GetValue<string>();
            var status = st?["status"]?.GetValue<string>() switch
            {
                "sent" => MessageStatus.Sent,
                "delivered" => MessageStatus.Delivered,
                "read" => MessageStatus.Read,
                "failed" => MessageStatus.Failed,
                _ => null
            };
            if (id is null || status is null) continue;

            var m = await db.Messages.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.ProviderMessageId == id, ct);
            if (m is null) continue;

            // Updates can arrive out of order — never move "read" back to "delivered".
            if (status == MessageStatus.Failed)
            {
                m.Status = MessageStatus.Failed;
                var err = st?["errors"]?[0]?["title"]?.GetValue<string>() ?? "Not delivered";
                m.Error = err.Length > 500 ? err[..500] : err;
            }
            else if (Array.IndexOf(Rank, status) > Array.IndexOf(Rank, m.Status))
            {
                m.Status = status;
            }
            m.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok();
    }

    private bool ValidSignature(string body, string header)
    {
        var secret = config["WhatsApp:AppSecret"];
        if (string.IsNullOrEmpty(secret))
        {
            log.LogWarning("WhatsApp:AppSecret is not set — webhook rejected");
            return false;
        }
        if (!header.StartsWith("sha256=")) return false;

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(header[7..]));
        }
        catch (FormatException) { return false; }
    }
}
