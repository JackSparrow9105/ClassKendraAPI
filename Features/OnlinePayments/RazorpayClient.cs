using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClassKendraAPI.Features.OnlinePayments;

public record PaymentLink(string Id, string ShortUrl);

/// <summary>
/// Razorpay Orders API + signature checks. Settings:
///   Razorpay:KeyId, Razorpay:KeySecret, Razorpay:WebhookSecret
/// </summary>
public class RazorpayClient(HttpClient http, IConfiguration config)
{
    public string KeyId => config["Razorpay:KeyId"] ?? "";
    public bool IsConfigured => !string.IsNullOrEmpty(config["Razorpay:KeyId"]) && !string.IsNullOrEmpty(config["Razorpay:KeySecret"]);

    /// <summary>Creates an order. Amount is in rupees; Razorpay works in paise.</summary>
    public async Task<string> CreateOrderAsync(int amountRupees, string receipt, Dictionary<string, string> notes, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["amount"] = amountRupees * 100,
            ["currency"] = "INR",
            ["receipt"] = receipt.Length > 40 ? receipt[..40] : receipt,
            ["notes"] = new JsonObject(notes.Select(n => new KeyValuePair<string, JsonNode?>(n.Key, n.Value)))
        };

        var json = await PostAsync("https://api.razorpay.com/v1/orders", payload, ct);
        return json["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Razorpay order has no id");
    }

    private async Task<JsonNode> PostAsync(string url, JsonObject payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config["Razorpay:KeyId"]}:{config["Razorpay:KeySecret"]}")));

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Razorpay call failed ({(int)res.StatusCode}): {text}");
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty Razorpay response");
    }

    /// <summary>Payment Link (used for the centre's own plan). Returns its id and the URL to send the owner to.</summary>
    public async Task<PaymentLink> CreatePaymentLinkAsync(int amountRupees, string description, string? phone,
        string callbackUrl, Dictionary<string, string> notes, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["amount"] = amountRupees * 100,
            ["currency"] = "INR",
            ["description"] = description.Length > 2048 ? description[..2048] : description,
            ["notes"] = new JsonObject(notes.Select(n => new KeyValuePair<string, JsonNode?>(n.Key, n.Value))),
            ["callback_url"] = callbackUrl,
            ["callback_method"] = "get"
        };
        if (!string.IsNullOrEmpty(phone))
            payload["customer"] = new JsonObject { ["contact"] = "+91" + phone };

        var json = await PostAsync("https://api.razorpay.com/v1/payment_links", payload, ct);
        return new PaymentLink(
            json["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Razorpay link has no id"),
            json["short_url"]?.GetValue<string>() ?? throw new InvalidOperationException("Razorpay link has no URL"));
    }

    /// <summary>Checkout success callback: signature = HMAC(order_id|payment_id, key secret).</summary>
    public bool CheckoutSignatureValid(string orderId, string paymentId, string signature) =>
        Matches($"{orderId}|{paymentId}", config["Razorpay:KeySecret"], signature);

    /// <summary>Webhook: X-Razorpay-Signature = HMAC(raw body, webhook secret).</summary>
    public bool WebhookSignatureValid(string body, string signature) =>
        Matches(body, config["Razorpay:WebhookSecret"], signature);

    private static bool Matches(string data, string? secret, string? signatureHex)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHex)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signatureHex)); }
        catch (FormatException) { return false; }
    }
}
