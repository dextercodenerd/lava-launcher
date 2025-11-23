using System;
using GenericLauncher.Auth.Json;
using GenericLauncher.Misc;

namespace GenericLauncher.Auth;

public record MinecraftAccount(
    string UniqueUserId, // based on Microsoft accounts tenant id and ID token's 'sub' claim
    bool HasMinecraft,
    XstsFailureReason? XboxAccountProblem,
    MinecraftProfile? Profile,
    string MinecraftAccessToken,
    string MicrosoftRefreshToken,
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
