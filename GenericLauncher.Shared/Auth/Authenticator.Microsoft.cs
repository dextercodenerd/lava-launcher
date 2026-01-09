using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Auth.Json;
using Microsoft.Extensions.Logging;
using MicrosoftJsonContext = GenericLauncher.Auth.Json.MicrosoftJsonContext;

namespace GenericLauncher.Auth;

public sealed partial class Authenticator
{
    private const string MsScope = "openid XboxLive.signin offline_access";

    private static (string CodeVerifier, string CodeChallenge) GeneratePkceCodes()
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

    private async Task<string> GetAuthorizationCodeAsync(
        string clientId,
        string codeChallenge,
        CancellationToken ctsToken)
    {
        var authUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?" +
                      $"client_id={clientId}&" +
                      "response_type=code&" +
                      $"redirect_uri={Uri.EscapeDataString(_redirectUrl)}&" +
                      "response_mode=query&" +
                      $"scope={Uri.EscapeDataString(MsScope)}&" +
                      $"code_challenge={codeChallenge}&" +
                      "code_challenge_method=S256&" +
                      "prompt=select_account"; // force account selection i.e., no automatic login

        // Start an HTTP listener to catch the redirect
        using var listener = new HttpListener();
        listener.Prefixes.Add($"{_redirectUrl}/");
        listener.Start();

        // When the token cancels (timeout or manual), force the listener to stop immediately.
        using var registration = ctsToken.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // ignore
            }
        });

        try
        {
            // Open the default system browser with the auth URL
            // WARN: There is no way of detecting if the webpage/tab/browser was closed, when opening
            //  a URL with Process.Start(). For reliable detection we have to embed a WebView in our
            //  app, or use the NativeWebDialog, but those are paid features of Avalonia.
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true, });

            // Wait for the OAuth2 redirect and extract the code.
            var context = await listener.GetContextAsync().ConfigureAwait(false);
            var code = context.Request.QueryString.Get("code");

            if (string.IsNullOrEmpty(code))
            {
                listener.Stop();
                throw new InvalidOperationException("Authorization code not found in the response.");
            }

            // TODO: send a nicer webpage instead of the simple text
            // Send a response to the browser to show a "success" message
            var responseBytes = Encoding.UTF8.GetBytes("<html><body>You can close this window now.</body></html>");
            context.Response.ContentLength64 = responseBytes.Length;
            // Do not try to cancel when we are sending the response back to the browser, because the auth was success
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.OutputStream.Close();

            return code;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // If we cancelled via the token, listener.Stop() was called. The GetContextAsync() will
            // throw one of these exceptions. We want these exceptions to be handled as
            // OperationCanceledException when the token is cancelled.
            ctsToken.ThrowIfCancellationRequested();

            throw;
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<MicrosoftTokenResponse> GetMicrosoftTokenAsync(
        string clientId,
        string authCode,
        string codeVerifier)
    {
        var parameters = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "scope", MsScope },
            { "code", authCode },
            { "redirect_uri", _redirectUrl },
            { "grant_type", "authorization_code" },
            { "code_verifier", codeVerifier },
        };

        var request =
            new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/consumers/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(parameters),
            };

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger?.LogWarning("Problem obtaining Microsoft token from code:\n{ErrorBody}", errorBody);
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.MicrosoftTokenResponse)
               ?? throw new InvalidOperationException("Problem parsing Microsoft token response");
    }

    private async Task<MicrosoftTokenResponse> RefreshMicrosoftTokenAsync(string clientId, string refreshToken)
    {
        var parameters = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "refresh_token", refreshToken },
            { "grant_type", "refresh_token" },
        };

        var request =
            new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/consumers/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(parameters),
            };

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.MicrosoftTokenResponse)
               ?? throw new InvalidOperationException("Problem parsing Microsoft refresh token response");
    }
}
