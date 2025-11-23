using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GenericLauncher.Auth.Json;
using GenericLauncher.Auth.Jwt;
using GenericLauncher.Misc;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Auth;

public sealed partial class Authenticator : IDisposable
{
    private readonly ILogger? _logger;
    private readonly string _clientId; // Azure app client ID
    private readonly string _redirectUrl; // OAuth redirect URL for the client ID
    private readonly HttpClient _httpClient;

    private readonly MicrosoftJwtVerifier _jwtVerifier;

    public Authenticator(string azureAppClientId,
        string oauthRedirectUrl,
        HttpClient httpClient,
        ILogger? logger)
    {
        _logger = logger;
        _clientId = azureAppClientId;
        _redirectUrl = oauthRedirectUrl;
        _httpClient = httpClient;
        _jwtVerifier = new MicrosoftJwtVerifier(azureAppClientId, httpClient);
    }

    public async Task<MinecraftAccount> AuthenticateAsync()
    {
        // Minecraft login is a multistep process. First is the Microsoft account OAuth2 flow with PKCE.
        var (verifier, challenge) = GeneratePkceCodes();
        var authCode = await GetAuthorizationCodeAsync(_clientId, challenge);
        var msTokenResponse = await GetMicrosoftTokenAsync(_clientId, authCode, verifier);

        // Now we have the MS access and refresh tokens and can get the Minecraft token
        return await GetMinecraftAccountAsync(msTokenResponse);
    }

    public async Task<MinecraftAccount> AuthenticateWithMsRefreshTokenAsync(string refreshToken)
    {
        var msTokenResponse = await RefreshMicrosoftTokenAsync(_clientId, refreshToken);

        return await GetMinecraftAccountAsync(msTokenResponse);
    }

    private async Task<MinecraftAccount> GetMinecraftAccountAsync(MicrosoftTokenResponse msTokenResponse)
    {
        // With a Microsoft access token we can start the Minecraft authorization dance
        var microsoftAccessToken = msTokenResponse.AccessToken;
        var expiresAt = UtcInstant.Now.Add(TimeSpan.FromSeconds(msTokenResponse.ExpiresIn));

        var (tid, sub) = await _jwtVerifier.VerifyMicrosoftTokenAsync(msTokenResponse.IdToken);
        // Hash 'tid' and 'sub' to create a privacy-focused unique id
        var uniqueUserId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tid}_{sub}")));

        // Xbox Live token
        var xblToken = await GetXboxLiveTokenAsync(microsoftAccessToken);

        // Xbox services security tokens (XSTS token)
        string xstsToken;
        string userHash;
        try
        {
            (xstsToken, userHash) = await GetXstsTokenAsync(xblToken);
        }
        catch (XstsException ex)
        {
            _logger?.LogWarning(ex, "Problem with Xbox account");

            return new MinecraftAccount(
                uniqueUserId,
                false,
                ex.Reason,
                null,
                "",
                msTokenResponse.RefreshToken,
                null,
                UtcInstant.MinValue);
        }

        // Minecraft token
        var minecraftToken = await GetMinecraftTokenAsync(xstsToken, userHash);

        // Now we can get the player's Minecraft profile
        MinecraftProfile? profile = null;
        try
        {
            profile = await GetMinecraftProfileAsync(minecraftToken);
            if (profile is not null)
            {
                _logger?.LogInformation("Logged in as {ProfileName} ({ProfileId})", profile.Name, profile.Id);
            }
            else
            {
                _logger?.LogWarning("Logged in Microsoft account doesn't have a Minecraft profile");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Problem loading Minecraft profile");
        }

        var hasMinecraft = false;
        string? xuid = null;
        try
        {
            (hasMinecraft, xuid) = await GetMinecraftEntitlements(minecraftToken);
            _logger?.LogInformation("Has Minecraft: {HasMinecraft}", hasMinecraft);
            _logger?.LogInformation("xuid: {Xuid}", xuid);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Problem loading Minecraft entitlements");
        }

        // TODO: Handle Game Pass ownership with the non-null profile + empty entitlements
        // An account can have Minecraft, but no profile yet, because they didn't log into the
        // official launcher. Or they haven't bought the game, but have a profile, because they have
        // it via Xbox Game Pass. With the Game Pass "ownership", the entitlements array is empty,
        // but if the Minecraft profile is not null, the user "owns" in. But to have an MC profile
        // with Game Pass, the user has to log into the official launcher first, to set up their
        // username.

        return new MinecraftAccount(
            uniqueUserId,
            hasMinecraft || profile is not null,
            null,
            profile,
            minecraftToken,
            msTokenResponse.RefreshToken,
            xuid,
            expiresAt
        );
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
