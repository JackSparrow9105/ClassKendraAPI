using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassKendraAPI.Features.Messages;

public record SendResult(bool Ok, string? ProviderMessageId, string? Error, bool Permanent = false);

public interface IWhatsAppSender
{
    Task<SendResult> SendTemplateAsync(string phone10, string template, string language,
                                       IReadOnlyList<string> parameters, CancellationToken ct);

    /// <summary>Login code. Uses an "Authentication" template with a copy-code button.</summary>
    Task<SendResult> SendOtpAsync(string phone10, string code, CancellationToken ct);
}

/// <summary>Development: nothing is sent, the message is written to the log.</summary>
public class LogWhatsAppSender(ILogger<LogWhatsAppSender> log) : IWhatsAppSender
{
    public Task<SendResult> SendTemplateAsync(string phone10, string template, string language,
                                              IReadOnlyList<string> parameters, CancellationToken ct)
    {
        log.LogInformation("WhatsApp (not sent) to {Phone}: {Template}/{Lang} [{Params}]",
            phone10, template, language, string.Join(" | ", parameters));
        return Task.FromResult(new SendResult(true, "dev-" + Guid.NewGuid().ToString("N"), null));
    }

    public Task<SendResult> SendOtpAsync(string phone10, string code, CancellationToken ct)
    {
        log.LogWarning("DEV OTP for {Phone}: {Code}", phone10, code);
        return Task.FromResult(new SendResult(true, null, null));
    }
}

/// <summary>
/// WhatsApp Cloud API (Meta). Settings:
///   WhatsApp:Provider = "Meta", WhatsApp:PhoneNumberId, WhatsApp:AccessToken,
///   WhatsApp:ApiVersion (default v21.0), WhatsApp:OtpTemplate (default login_otp)
/// </summary>
public class MetaWhatsAppSender(HttpClient http, IConfiguration config, ILogger<MetaWhatsAppSender> log) : IWhatsAppSender
{
    public Task<SendResult> SendTemplateAsync(string phone10, string template, string language,
                                              IReadOnlyList<string> parameters, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["type"] = "body",
            ["parameters"] = new JsonArray(parameters.Select(p => (JsonNode)new JsonObject
            {
                ["type"] = "text",
                ["text"] = p
            }).ToArray())
        };
        return PostAsync(phone10, template, language, [body], ct);
    }

    public Task<SendResult> SendOtpAsync(string phone10, string code, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["type"] = "body",
            ["parameters"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = code })
        };
        var button = new JsonObject
        {
            ["type"] = "button",
            ["sub_type"] = "url",
            ["index"] = "0",
            ["parameters"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = code })
        };
        return PostAsync(phone10, config["WhatsApp:OtpTemplate"] ?? "login_otp", "en", [body, button], ct);
    }

    private async Task<SendResult> PostAsync(string phone10, string template, string language,
                                             JsonNode[] components, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["to"] = "91" + phone10,
            ["type"] = "template",
            ["template"] = new JsonObject
            {
                ["name"] = template,
                ["language"] = new JsonObject { ["code"] = language },
                ["components"] = new JsonArray(components)
            }
        };

        var version = config["WhatsApp:ApiVersion"] ?? "v21.0";
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{version}/{config["WhatsApp:PhoneNumberId"]}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["WhatsApp:AccessToken"]);

        try
        {
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                var id = JsonNode.Parse(text)?["messages"]?[0]?["id"]?.GetValue<string>();
                return new SendResult(true, id, null);
            }

            log.LogWarning("WhatsApp send failed {Status}: {Body}", (int)res.StatusCode, text);
            var error = TryReadError(text) ?? $"HTTP {(int)res.StatusCode}";
            // 4xx (bad number, template not approved...) won't fix itself; 429/5xx may.
            var permanent = (int)res.StatusCode is >= 400 and < 500 and not 429;
            return new SendResult(false, null, error, permanent);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new SendResult(false, null, ex.Message);
        }
    }

    private static string? TryReadError(string json)
    {
        try { return JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>(); }
        catch (JsonException) { return null; }
    }
}
