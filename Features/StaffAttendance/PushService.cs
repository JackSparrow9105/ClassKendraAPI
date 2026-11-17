using System.Text.Json;
using ClassKendraAPI.Data;
using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;
using WebPush;

namespace ClassKendraAPI.Features.StaffAttendance;

/// <summary>
/// App notifications (Web Push) to the ClassKendra PWA. Free, no per-message cost.
/// Needs Push:PublicKey and Push:PrivateKey (VAPID); without the private key nothing is sent.
/// The payload is in the shape Angular's service worker shows by itself, and a tap opens the given page.
/// </summary>
public class PushService(ClassKendraContext db, IConfiguration config, ILogger<PushService> log)
{
    public string? PublicKey => config["Push:PublicKey"];
    private bool Enabled => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(config["Push:PrivateKey"]);

    /// <summary>Sends to every phone of this person. Returns how many phones got it (0 = none allowed, or push not set up).</summary>
    public async Task<int> SendAsync(Guid userId, string title, string body, string path, CancellationToken ct)
    {
        if (!Enabled) return 0;
        var subs = await db.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
        if (subs.Count == 0) return 0;

        var appUrl = (config["App:PublicUrl"] ?? "").TrimEnd('/');
        var payload = JsonSerializer.Serialize(new
        {
            notification = new
            {
                title,
                body,
                icon = $"{appUrl}/icons/icon-192x192.png",
                badge = $"{appUrl}/icons/icon-96x96.png",
                tag = path,
                data = new { onActionClick = new { @default = new { operation = "navigateLastFocusedOrOpen", url = appUrl + path } } }
            }
        });
        var vapid = new VapidDetails(config["Push:Subject"] ?? "mailto:support@example.com", PublicKey, config["Push:PrivateKey"]);
        var client = new WebPushClient();
        var sent = 0;
        foreach (var s in subs)
        {
            try
            {
                await client.SendNotificationAsync(new PushSubscription(s.Endpoint, s.P256dh, s.Auth), payload, vapid);
                sent++;
            }
            catch (WebPushException ex) when ((int)ex.StatusCode is 404 or 410)
            {
                db.PushSubscriptions.Remove(s);   // the phone uninstalled or turned notifications off
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Push to {User} failed", userId);
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Saves this phone's subscription for the signed-in person (replacing an older one with the same endpoint).</summary>
    public async Task SaveAsync(Guid tenantId, Guid userId, string endpoint, string p256dh, string auth, CancellationToken ct)
    {
        var existing = await db.PushSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is null)
            db.PushSubscriptions.Add(new PushSubscriptionRow { TenantId = tenantId, UserId = userId, Endpoint = endpoint, P256dh = p256dh, Auth = auth });
        else
        {
            existing.TenantId = tenantId;
            existing.UserId = userId;
            existing.P256dh = p256dh;
            existing.Auth = auth;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string endpoint, CancellationToken ct) =>
        await db.PushSubscriptions.Where(s => s.Endpoint == endpoint).ExecuteDeleteAsync(ct);
}
