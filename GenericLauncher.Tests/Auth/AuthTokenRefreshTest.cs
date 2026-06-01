using GenericLauncher.Auth;
using GenericLauncher.Auth.Json;
using GenericLauncher.Database.Model;
using GenericLauncher.Misc;
using Xunit;

namespace GenericLauncher.Tests.Auth;

public sealed class AuthTokenRefreshTest
{
    [Fact]
    public void MinecraftAccount_ShouldRefresh_WhenTokenExpiresWithinThreshold()
    {
        var account = CreateMinecraftAccount(UtcInstant.Now.AddMinutes(10));

        Assert.True(account.ShouldRefresh);
    }

    [Fact]
    public void MinecraftAccount_ShouldNotRefresh_WhenTokenExpiresAfterThreshold()
    {
        var account = CreateMinecraftAccount(UtcInstant.Now.AddMinutes(20));

        Assert.False(account.ShouldRefresh);
    }

    [Fact]
    public void Account_ShouldRefresh_WhenTokenExpiresWithinThreshold()
    {
        var account = CreateStoredAccount(UtcInstant.Now.AddMinutes(10));

        Assert.True(account.ShouldRefresh);
    }

    [Fact]
    public void Account_ShouldNotRefresh_WhenTokenExpiresAfterThreshold()
    {
        var account = CreateStoredAccount(UtcInstant.Now.AddMinutes(20));

        Assert.False(account.ShouldRefresh);
    }

    private static MinecraftAccount CreateMinecraftAccount(UtcInstant expiresAt) =>
        new(
            "account-id",
            true,
            null,
            new MinecraftProfile("minecraft-user", "Steve", [], []),
            "minecraft-access-token",
            "refresh-token",
            "xbox-user",
            expiresAt);

    private static Account CreateStoredAccount(UtcInstant expiresAt) =>
        new(
            "account-id",
            XboxAccountState.Ok,
            "minecraft-user",
            "xbox-user",
            "Steve",
            true,
            null,
            null,
            "minecraft-access-token",
            "refresh-token",
            expiresAt);
}
