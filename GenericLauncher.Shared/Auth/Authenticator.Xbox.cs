using System;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using GenericLauncher.Auth.Json;
using GenericLauncher.Microsoft.Json;

namespace GenericLauncher.Auth;

public sealed partial class Authenticator
{
    private async Task<string> GetXboxLiveTokenAsync(string microsoftAccessToken)
    {
        var requestBody = new XboxLiveAuthRequest(
            new XboxLiveAuthProperties("RPS", "user.auth.xboxlive.com", $"d={microsoftAccessToken}"),
            "http://auth.xboxlive.com", // WARN: RelyingParty is plain-text http://, otherwise we get 400 bad request
            "JWT"
        );

        var response =
            await _httpClient.PostAsJsonAsync(
                "https://user.auth.xboxlive.com/user/authenticate",
                requestBody,
                XboxLiveJsonContext.Default.XboxLiveAuthRequest);
        response.EnsureSuccessStatusCode();

        var responseData = await response.Content.ReadFromJsonAsync(XboxLiveJsonContext.Default.XboxLiveAuthResponse) ??
                           throw new InvalidOperationException("Problem parsing Xbox Live auth response");

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
            // 2148916233: The account doesn't have an Xbox account. Once they sign up for one (or login through
            //             minecraft.net to create one) then they can proceed with the login. This shouldn't happen with
            //             accounts that have purchased Minecraft with a Microsoft account, as they would've already gone
            //             through that Xbox signup process.
            // 2148916227: The account is banned from Xbox.
            // 2148916235: Accounts from countries where XBox Live is not available or banned.
            // 2148916236: You must complete adult verification on the XBox homepage. (South Korea)
            // 2148916237: Age verification must be completed on the XBox homepage. (South Korea)
            // 2148916238: The account is under the age of 18, an adult must add the account to the family.
            // 2148916262: TBD, happens rarely without any additional information.
            var responseErr =
                await response.Content.ReadFromJsonAsync(XboxLiveJsonContext.Default.XstsAuthErrorResponse)
                ?? throw new InvalidOperationException("Problem parsing XSTS auth error response");

            throw responseErr.XErr switch
            {
                2148916233 => new XstsException(XstsFailureReason.XboxAccountMissing, responseErr.XErr),
                2148916227 => new XstsException(XstsFailureReason.XboxAccountBanned, responseErr.XErr),
                2148916235 => new XstsException(XstsFailureReason.XboxAccountNotAvailable, responseErr.XErr),
                2148916236 or 2148916237 or 2148916238 =>
                    new XstsException(XstsFailureReason.AgeVerificationRequired, responseErr.XErr),
                _ => new XstsException(XstsFailureReason.Unknown, responseErr.XErr),
            };
        }

        var responseData =
            await response.Content.ReadFromJsonAsync(XboxLiveJsonContext.Default.XstsAuthResponse) ??
            throw new InvalidOperationException("Problem parsing XSTS auth response");

        var userHash = responseData.DisplayClaims.Xui.FirstOrDefault()?.Uhs ??
                       throw new InvalidOperationException("Missing XSTS user hash");

        return (responseData.Token, userHash);
    }
}
