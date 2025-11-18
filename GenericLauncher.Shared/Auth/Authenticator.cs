using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GenericLauncher.Auth.Json;
using GenericLauncher.Microsoft.Json;
using GenericLauncher.Misc;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Auth;

public record MicrosoftAccount(
    bool HasMinecraft,
    MinecraftProfile? Profile,
    string MinecraftAccessToken,
    string MsRefreshToken,
    string? XboxUserId,
    UtcInstant ExpiresAt
)
{
    /// <summary>
    /// The <see cref="MinecraftAccessToken" /> is valid for one hour (3600 seconds) and is required for launching the
    /// game, and it must still be valid when connecting to Minecraft servers. Thus, we refresh it when its validity is
    /// near expiration, so users can connect to servers even after some time after launching Minecraft. Expired token
    /// leads to infamous Minecraft error "Invalid Session (Try Restarting your Game!)".
    ///
    /// In our case, we set 15 minutes threshold.
    /// </summary>
    public bool ShouldRefresh
    {
        get => UtcInstant.Now.Subtract(TimeSpan.FromMinutes(15)) >= ExpiresAt;
    }
}

public sealed class Authenticator : IDisposable
{
    private readonly ILogger? _logger;
    private readonly string _clientId; // Azure app client ID
    private readonly string _redirectUrl; // OAuth redirect URL for the client ID
    private readonly HttpClient _httpClient;

    public Authenticator(string azureAppClientId,
        string oauthRedirectUrl,
        HttpClient httpClient,
        ILogger? logger)
    {
        _logger = logger;
        _clientId = azureAppClientId;
        _redirectUrl = oauthRedirectUrl;
        _httpClient = httpClient;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task<MicrosoftAccount> AuthenticateAsync()
    {
        // Minecraft login is a multistep process. First is the Microsoft account OAuth2 flow with PKCE.
        var (verifier, challenge) = GeneratePkceCodes();
        var authCode = await GetAuthorizationCodeAsync(_clientId, challenge);
        var msTokenResponse = await GetMicrosoftTokenAsync(_clientId, authCode, verifier)
                              ?? throw new InvalidOperationException("couldn't obtain access MS token");

        return await GetMinecraftAccountAsync(msTokenResponse);
    }

    public async Task<MicrosoftAccount> AuthenticateWithMsRefreshTokenAsync(string refreshToken)
    {
        var msTokenResponse = await RefreshMicrosoftTokenAsync(_clientId, refreshToken)
                              ?? throw new InvalidOperationException("couldn't refresh MS token");

        return await GetMinecraftAccountAsync(msTokenResponse);
    }

    private async Task<MicrosoftAccount> GetMinecraftAccountAsync(MicrosoftTokenResponse msTokenResponse)
    {
        // With and MS access token we can start the Minecraft authorization dance
        var microsoftAccessToken = msTokenResponse.AccessToken;
        _logger?.LogInformation("Successfully retrieved Microsoft access token.");
        var expiresAt = UtcInstant.Now.Add(TimeSpan.FromSeconds(msTokenResponse.ExpiresIn));

        // Xbox Live token
        var xblToken = await GetXboxLiveTokenAsync(microsoftAccessToken);
        _logger?.LogInformation("Successfully retrieved Xbox Live token.");

        // === Xbox services security tokens (XSTS token)
        var (xstsToken, userHash) = await GetXstsTokenAsync(xblToken);
        _logger?.LogInformation("Successfully retrieved XSTS token.");

        // Minecraft token
        var minecraftToken = await GetMinecraftTokenAsync(xstsToken, userHash)
                             ?? throw new InvalidOperationException("no Minecraft token obtained");
        _logger?.LogInformation("Successfully retrieved Minecraft access token!");

        // Now we can get the player's Minecraft profile
        var profile = await GetMinecraftProfileAsync(minecraftToken);
        if (profile is not null)
        {
            _logger?.LogInformation("Logged in as {ProfileName} ({ProfileId})", profile.Name, profile.Id);
        }
        else
        {
            _logger?.LogWarning("Logged in Microsoft account doesn't have a Minecraft profile");
        }

        var (hasMinecraft, xuid) = await GetMinecraftEntitlements(minecraftToken);
        _logger?.LogInformation("Has Minecraft: {HasMinecraft}", hasMinecraft);
        _logger?.LogInformation("xuid: {Xuid}", xuid);

        // An account can have Minecraft, but no profile yet, because they didn't log into the
        // official launcher. Or they haven't bought the game, but have it via Xbox Game Pass. With
        // the Game Pass, the entitlements array is empty, but if the Minecraft profile is not null,
        // the user "owns" in. But to have an MC profile with Game Pass, the user has to log into
        // the official launcher first, to set up their username.

        return new MicrosoftAccount(
            hasMinecraft || profile is not null,
            profile,
            minecraftToken,
            msTokenResponse.RefreshToken,
            xuid,
            expiresAt
        );
    }

    private async Task<string> GetXboxLiveTokenAsync(string microsoftAccessToken)
    {
        var requestBody = new XboxLiveAuthRequest(
            new XboxLiveAuthProperties("RPS", "user.auth.xboxlive.com", $"d={microsoftAccessToken}"),
            "http://auth.xboxlive.com",
            "JWT"
        );

        var response =
            await _httpClient.PostAsJsonAsync(
                "https://user.auth.xboxlive.com/user/authenticate",
                requestBody,
                XboxLiveJsonContext.Default.XboxLiveAuthRequest);
        response.EnsureSuccessStatusCode();

        var responseData =
            await response.Content.ReadFromJsonAsync(XboxLiveJsonContext.Default.XboxLiveAuthResponse) ??
            throw new InvalidOperationException("xbox live auth error");

        return responseData.Token;
    }

    private async Task<(string Token, string UserHash)> GetXstsTokenAsync(string xblToken)
    {
        var requestBody = new XstsAuthRequest(
            new XstsAuthProperties("RETAIL", [xblToken]),
            "rp://api.minecraftservices.com/",
            "JWT"
        );

        var response = await _httpClient.PostAsJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
            requestBody,
            XboxLiveJsonContext.Default.XstsAuthRequest);

        if (!response.IsSuccessStatusCode)
        {
            // TODO: /xsts/authorize can return different `XErr`s, so handle them. Here are some known values:
            // https://learn.microsoft.com/en-in/answers/questions/583869/what-kind-of-xerr-is-displayed-during-xsts-authent
            // https://minecraft.wiki/w/Microsoft_authentication
            // 2148916227: The account is banned from Xbox.
            // 2148916233: The account doesn't have an Xbox account. Once they sign up for one (or login through
            //             minecraft.net to create one) then they can proceed with the login. This shouldn't happen with
            //             accounts that have purchased Minecraft with a Microsoft account, as they would've already gone
            //             through that Xbox signup process.
            // 2148916235: Accounts from countries where XBox Live is not available or banned.
            // 2148916236: You must complete adult verification on the XBox homepage. (South Korea)
            // 2148916237: Age verification must be completed on the XBox homepage. (South Korea)
            // 2148916238: The account is under the age of 18, an adult must add the account to the family.
            // 2148916262: TBD, happens rarely without any additional information.
            var responseErr =
                await response.Content.ReadFromJsonAsync(XboxLiveJsonContext.Default.XstsAuthErrorResponse)
                ?? throw new InvalidOperationException("xsts error parsing problem");

            switch (responseErr.XErr)
            {
                // TODO...
                case 2148916227:
                    throw new AuthenticationException("");
                default:
                    response.EnsureSuccessStatusCode();
                    break;
            }
        }

        var responseData =
            await response.Content.ReadFromJsonAsync(XboxLiveJsonContext.Default.XstsAuthResponse) ??
            throw new InvalidOperationException("xsts auth parsing problem");
        var userHash = responseData.DisplayClaims.Xui.FirstOrDefault()?.Uhs ??
                       throw new InvalidOperationException("xsts user hash error");

        return (responseData.Token, userHash);
    }

    private async Task<string?> GetMinecraftTokenAsync(string xstsToken, string userHash)
    {
        var requestBody = new MinecraftAuthRequest($"XBL3.0 x={userHash};{xstsToken}");
        var response = await _httpClient.PostAsJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            requestBody,
            MicrosoftJsonContext.Default.MinecraftAuthRequest);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger?.LogError("minecraft login error:\n{Body}", body);
        }

        response.EnsureSuccessStatusCode();

        var responseData =
            await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.MinecraftAuthResponse) ??
            throw new InvalidOperationException("minecraft auth error");

        return responseData.AccessToken;
    }

    private async Task<MinecraftProfile?> GetMinecraftProfileAsync(string minecraftToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftToken);

        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.MinecraftProfile) ??
               throw new InvalidOperationException("profile parse problem");
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="minecraftToken"></param>
    /// <returns>A tuple indicating if the user has bought a Minecraft and xuid that is required for starting Minecraft.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task<(bool HasMinecraft, string? Xuid)> GetMinecraftEntitlements(string minecraftToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftToken);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var entitlements =
            await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.EntitlementsResponse) ??
            throw new InvalidOperationException("profile error");

        // It should be enough that the items array is not empty.
        // https://minecraft.wiki/w/Microsoft_authentication
        // TODO: Xbox Game Pass users also have Minecraft, but it doesn't show-up here
        // var hasMinecraft = entitlements.Items.Any(e => e.Name.Contains("minecraft"));
        var hasMinecraft = entitlements.Items.Length > 0;
        return (hasMinecraft, entitlements.SignerId);
    }

    private (string CodeVerifier, string CodeChallenge) GeneratePkceCodes()
    {
        // Generate a random 32-byte verifier and Base64 encode them with URL-safe alphabet/encoding
        // without padding. C# doesn't have bult-in URL-safe Base64 encoder and padding options.
        var codeVerifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Convert.ToBase64String(codeVerifierBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // Create the challenge by hashing the Base64-encoded verifier.
        var challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        var codeChallenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return (codeVerifier, codeChallenge);
    }

    private async Task<string> GetAuthorizationCodeAsync(string clientId, string codeChallenge)
    {
        const string scope = "XboxLive.signin offline_access";
        var authUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?" +
                      $"client_id={clientId}&" +
                      "response_type=code&" +
                      $"redirect_uri={Uri.EscapeDataString(_redirectUrl)}&" +
                      "response_mode=query&" +
                      $"scope={Uri.EscapeDataString(scope)}&" +
                      $"code_challenge={codeChallenge}&" +
                      "code_challenge_method=S256";

        // TODO: Add time-out, like 5 minutes?, to cancel the flow. And listen for closing the webpage.
        // Start an HTTP listener to catch the redirect
        using var listener = new HttpListener();
        listener.Prefixes.Add($"{_redirectUrl}/");
        listener.Start();

        // Open the default system browser with the auth URL
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        _logger?.LogInformation("Your browser has been opened to sign in.");

        // Wait for the OAuth2 redirect and extract the code
        var context = await listener.GetContextAsync();
        var code = context.Request.QueryString.Get("code")!;

        // TODO: send a nicer webpage instead of the simple text
        // Send a response to the browser to show a "success" message
        var responseBytes = Encoding.UTF8.GetBytes("<html><body>You can close this window now.</body></html>");
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
        context.Response.OutputStream.Close();
        listener.Stop();

        return code;
    }

    private async Task<MicrosoftTokenResponse?> GetMicrosoftTokenAsync(
        string clientId,
        string authCode,
        string codeVerifier)
    {
        var parameters = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "scope", "XboxLive.signin offline_access" },
            { "code", authCode },
            { "redirect_uri", _redirectUrl },
            { "grant_type", "authorization_code" },
            { "code_verifier", codeVerifier }
        };

        var request =
            new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/consumers/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(parameters)
            };

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.MicrosoftTokenResponse);
    }

    private async Task<MicrosoftTokenResponse?> RefreshMicrosoftTokenAsync(string clientId, string refreshToken)
    {
        var parameters = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "refresh_token", refreshToken },
            { "grant_type", "refresh_token" }
        };

        var request =
            new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/consumers/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(parameters)
            };

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.MicrosoftTokenResponse);
    }
}
