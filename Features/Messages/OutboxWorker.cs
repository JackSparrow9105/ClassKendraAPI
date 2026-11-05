using System.Text.Json;
using ClassKendraAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Features.Messages;

/// <summary>
/// Sends queued WhatsApp messages every few seconds. Failed sends are retried up to 3 times
/// (after 1, then 2 minutes). Run one copy of the API (one App Service instance) so a message
/// isn't picked up twice.
/// </summary>
public class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var sent = await SendBatch(stop);
                if (sent > 0) continue;   // more may be waiting
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.LogError(ex, "Outbox run failed");
            }
            await Task.Delay(Every, stop);
        }
    }

    private async Task<int> SendBatch(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClassKendraContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IWhatsAppSender>();

        var now = DateTime.UtcNow;
        var batch = (await db.Messages.IgnoreQueryFilters()
                .Where(m => m.Status == MessageStatus.Queued)
                .OrderBy(m => m.CreatedAt)
                .Take(100)
                .ToListAsync(ct))
            .Where(m => m.Attempts == 0 || m.UpdatedAt <= now.AddMinutes(-m.Attempts))
            .Take(25)
            .ToList();

        foreach (var m in batch)
        {
            var parameters = JsonSerializer.Deserialize<string[]>(m.TemplateParams ?? "[]") ?? [];
            var result = await sender.SendTemplateAsync(m.Phone, m.TemplateName ?? "", m.Language.Trim(), parameters, ct);

            m.Attempts++;
            m.UpdatedAt = DateTime.UtcNow;
            if (result.Ok)
            {
                m.Status = MessageStatus.Sent;
                m.ProviderMessageId = result.ProviderMessageId;
                m.Error = null;
            }
            else
            {
                m.Error = result.Error is { Length: > 500 } e ? e[..500] : result.Error;
                if (result.Permanent || m.Attempts >= MaxAttempts) m.Status = MessageStatus.Failed;
            }
            await db.SaveChangesAsync(ct);
        }
        return batch.Count;
    }
}
