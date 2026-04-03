using System.Linq;
using System.Threading.Tasks;
using GenericLauncher.Database;
using GenericLauncher.Database.Model;
using GenericLauncher.Database.Orm;
using GenericLauncher.Misc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GenericLauncher.Tests.Database;

public sealed class LauncherDatabaseTest
{
    [Fact]
    public async Task ExecuteScalarAsync_OnDelete_ReturnsDefaultValueInsteadOfAffectedRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync("CREATE TABLE [Test] (Id TEXT PRIMARY KEY) STRICT;", ct: cancellationToken);
        await conn.ExecuteAsync("INSERT INTO [Test] (Id) VALUES ('account-1');", ct: cancellationToken);

        // This test shows that ExecuteScalarAsync always returns 0, instead of the number of deleted rows
        var result = await conn.ExecuteScalarAsync<long>(
            "DELETE FROM [Test] WHERE Id = 'account-1';",
            ct: cancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM [Test];", ct: cancellationToken));
    }

    [Fact]
    public async Task RemoveAccountAsync_ReturnsTrue_WhenRowWasDeleted()
    {
        await using var db = await CreateDatabaseAsync();
        await db.UpsertAccountAsync(CreateAccount("account-1"));

        var deleted = await db.RemoveAccountAsync("account-1");

        Assert.True(deleted);
        Assert.Empty((await db.GetAllAccountsAsync()).ToList());
    }

    [Fact]
    public async Task RemoveAccountAsync_ReturnsFalse_WhenRowDoesNotExist()
    {
        await using var db = await CreateDatabaseAsync();

        var deleted = await db.RemoveAccountAsync("missing-account");

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteMinecraftInstanceAsync_ReturnsTrue_WhenRowWasDeleted()
    {
        await using var db = await CreateDatabaseAsync();
        await db.InsertMinecraftInstanceAsync(CreateInstance("instance-1"));

        var deleted = await db.DeleteMinecraftInstanceAsync("instance-1");

        Assert.True(deleted);
        Assert.Empty((await db.GetAllMinecraftInstancesAsync()).ToList());
    }

    [Fact]
    public async Task DeleteMinecraftInstanceAsync_ReturnsFalse_WhenRowDoesNotExist()
    {
        await using var db = await CreateDatabaseAsync();

        var deleted = await db.DeleteMinecraftInstanceAsync("missing-instance");

        Assert.False(deleted);
    }

    [Fact]
    public async Task SetMinecraftInstanceClassPathAsync_UpdatesOnlyClassPath()
    {
        await using var db = await CreateDatabaseAsync();
        await db.InsertMinecraftInstanceAsync(CreateInstance("instance-1"));

        var repairedClassPath = new[]
        {
            "/tmp/mc/modloaders/fabric/libraries/org/ow2/asm/asm/9.9/asm-9.9.jar",
        }.ToList();

        await db.SetMinecraftInstanceClassPathAsync("instance-1", repairedClassPath);

        var stored = (await db.GetAllMinecraftInstancesAsync()).Single();
        Assert.Equal(repairedClassPath, stored.ClassPath);
        Assert.Equal("client.jar", stored.ClientJarPath);
        Assert.Equal("net.minecraft.client.main.Main", stored.MainClass);
    }

    private static async Task<LauncherDatabase> CreateDatabaseAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        var db = new LauncherDatabase(conn);
        await db.EnsureCreatedAsync();
        return db;
    }

    private static Account CreateAccount(string accountId) =>
        new(
            accountId,
            XboxAccountState.Ok,
            "minecraft-user",
            "xbox-user",
            "Steve",
            true,
            null,
            null,
            "access-token",
            "refresh-token",
            UtcInstant.UnixEpoch);

    private static MinecraftInstance CreateInstance(string instanceId) =>
        new(
            instanceId,
            "1.21.1",
            "1.21.1",
            MinecraftInstanceModLoader.Vanilla,
            null,
            MinecraftInstanceState.Ready,
            "release",
            "folder",
            21,
            "client.jar",
            "net.minecraft.client.main.Main",
            "asset-index",
            [],
            [],
            []);
}
