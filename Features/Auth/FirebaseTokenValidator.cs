using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ClassKendraAPI.Features.Auth;

/// <summary>
/// Checks a Firebase ID token sent by the app after Firebase verified the SMS code.
/// The token is signed by Google; we check the signature with Google's public keys and that it was
/// issued for our Firebase project. No Firebase service account or extra package is needed.
/// Turned on by setting Firebase:ProjectId (empty = off).
/// </summary>
public class FirebaseTokenValidator
{
    private readonly string? projectId;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? keys;
    private readonly JsonWebTokenHandler handler = new();

    public FirebaseTokenValidator(IConfiguration config)
    {
        projectId = config["Firebase:ProjectId"];
        if (string.IsNullOrWhiteSpace(projectId)) return;

        // Google publishes the signing keys here; they are cached and refreshed automatically.
        keys = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://securetoken.google.com/{projectId}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public bool Enabled => keys is not null;

    /// <summary>The verified phone number from the token ("+919876543210"), or null if the token isn't valid.</summary>
    public Task<string?> VerifiedPhoneAsync(string idToken, CancellationToken ct) => CheckAsync(idToken, retried: false, ct);

    private async Task<string?> CheckAsync(string idToken, bool retried, CancellationToken ct)
    {
        if (keys is null || string.IsNullOrWhiteSpace(idToken)) return null;

        var oidc = await keys.GetConfigurationAsync(ct);
        var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = $"https://securetoken.google.com/{projectId}",
            ValidAudience = projectId,
            IssuerSigningKeys = oidc.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });

        if (!result.IsValid)
        {
            // Keys may have rotated since we cached them: refresh once and try again.
            if (!retried && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                keys.RequestRefresh();
                return await CheckAsync(idToken, retried: true, ct);
            }
            return null;
        }

        // Firebase puts the number it sent the SMS to in "phone_number".
        return result.Claims.TryGetValue("phone_number", out var phone) ? phone as string : null;
    }
}
