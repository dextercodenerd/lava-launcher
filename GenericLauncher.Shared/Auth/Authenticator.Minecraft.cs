using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using GenericLauncher.Auth.Json;
using Microsoft.Extensions.Logging;
using MicrosoftJsonContext = GenericLauncher.Auth.Json.MicrosoftJsonContext;

namespace GenericLauncher.Auth;

public sealed partial class Authenticator
{
    private async Task<string> GetMinecraftTokenAsync(string xstsToken, string userHash)
    {
        var requestBody = new MinecraftAuthRequest($"XBL3.0 x={userHash};{xstsToken}");
        var response = await _httpClient.PostAsJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            requestBody,
            MicrosoftJsonContext.Default.MinecraftAuthRequest);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger?.LogError("Minecraft login error:\n{Body}", body);
        }

        response.EnsureSuccessStatusCode();

        var responseData =
            await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.MinecraftAuthResponse) ??
            throw new InvalidOperationException("Problem parsing Minecraft auth response");

        return responseData.AccessToken ?? throw new InvalidOperationException("Missing Minecraft access token");
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
               throw new InvalidOperationException("Problem parsing Minecraft profile response");
    }

    private async Task<(bool HasMinecraft, string? Xuid)> GetMinecraftEntitlements(string minecraftToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftToken);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var entitlements =
            await response.Content.ReadFromJsonAsync(MicrosoftJsonContext.Default.EntitlementsResponse) ??
            throw new InvalidOperationException("Problem parsing Minecraft entitlements response");

        // It should be enough that the items array is not empty.
        // https://minecraft.wiki/w/Microsoft_authentication
        // WARN: Xbox Game Pass users also have Minecraft, but it doesn't show-up in the
        //  entitlements. To detect Game Pass, it is enough that the account has a Minecraft profile
        //  which is checked later in the login flow.
        var hasMinecraft = entitlements.Items.Length > 0;
        return (hasMinecraft, entitlements.SignerId);
    }
}
