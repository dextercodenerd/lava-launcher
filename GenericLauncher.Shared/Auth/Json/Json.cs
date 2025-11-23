using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GenericLauncher.Misc;

namespace GenericLauncher.Auth.Json;

public record MicrosoftTokenResponse(
    string IdToken,
    string AccessToken,
    string TokenType,
    long ExpiresIn, // How long the access token is valid, in seconds.
    string Scope,
    string RefreshToken
);

public record MicrosoftUserResponse(
    [property: JsonPropertyName("id")] string Id);

public record XboxLiveAuthRequest(XboxLiveAuthProperties Properties, string RelyingParty, string TokenType);

public record XboxLiveAuthProperties(string AuthMethod, string SiteName, string RpsTicket);

public record XboxLiveAuthResponse(
    UtcInstant IssueInstant,
    UtcInstant NotAfter,
    string Token,
    DisplayClaims DisplayClaims);

public record XstsAuthRequest(XstsAuthProperties Properties, string RelyingParty, string TokenType);

public record XstsAuthProperties(string SandboxId, string[] UserTokens);

public record XstsAuthResponse(
    UtcInstant IssueInstant,
    UtcInstant NotAfter,
    string Token,
    DisplayClaims DisplayClaims);

public record XstsAuthErrorResponse(
    string Identity,
    long XErr,
    string Message,
    string Redirect);

public record DisplayClaims(
    [property: JsonPropertyName("xui")] Xui[] Xui
);

public record Xui(
    [property: JsonPropertyName("uhs")] string Uhs
);

public record MinecraftAuthRequest(
    [property: JsonPropertyName("identityToken")]
    string IdentityToken
);

public record MinecraftAuthResponse(
    string Username,
    string AccessToken,
    string TokenType,
    int ExpiresIn
);

public record MinecraftProfile(
    string Id, // UUID
    string Name, // Username
    Skin[] Skins,
    Cape[] Capes
);

public record Skin(
    string Id,
    string State,
    string Url,
    string Variant = "CLASSIC" // CLASSIC for Steve, and SLIM for Alex
);

public record Cape(
    string Id,
    string State,
    string Url,
    string Alias);

public record EntitlementsResponse(
    EntitlementItem[] Items,
    string Signature,
    [property: JsonPropertyName("keyId")] string KeyId
)
{
    // Computed property that extracts signerId from the signature
    public string? SignerId
    {
        get
        {
            try
            {
                var json = ExtractSecondTokenAsJson();
                return json?.RootElement.GetProperty("signerId").GetString();
            }
            catch
            {
                return null;
            }
        }
    }

    // Helper method to extract the second token as JsonDocument
    private JsonDocument? ExtractSecondTokenAsJson()
    {
        if (string.IsNullOrEmpty(Signature))
        {
            return null;
        }

        var tokens = Signature.Split('.');
        if (tokens.Length < 2)
        {
            return null;
        }

        var secondToken = tokens[1];
        var decodedBytes = DecodeBase64Url(secondToken);
        var decodedJson = Encoding.UTF8.GetString(decodedBytes);

        return JsonDocument.Parse(decodedJson);
    }

    // Helper method for Base64Url decoding
    private static byte[] DecodeBase64Url(string base64Url)
    {
        var base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/')
            .PadRight(base64Url.Length + (4 - base64Url.Length % 4) % 4, '=');

        return Convert.FromBase64String(base64);
    }
};

public record EntitlementItem(string Name, string Signature);
