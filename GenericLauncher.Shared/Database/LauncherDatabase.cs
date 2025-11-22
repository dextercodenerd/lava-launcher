using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Database.Orm;
using GenericLauncher.Database.TypeHandlers;
using GenericLauncher.Misc;
using Microsoft.Data.Sqlite;

namespace GenericLauncher.Database;

/// <summary>
/// SQLite allows only one writer thread, and we are using a custom `AsyncRwLock` for that.
/// Originally we had a `ThreadSafeSqliteConnection`, that inherited and wrapped all the Dapper
/// functions, but that didn't work with AOT compilation. Dapper.AOT doesn't support calling their
/// extension methods with generic parameters, only with explicit types, thus we handle the
/// thread-safety here.
///
/// Dapper.AOT's rule DAP016 https://aot.dapperlib.dev/rules/DAP016
/// </summary>
public sealed class LauncherDatabase
    : IDisposable, IAsyncDisposable
{
    private const int DefaultMaxConcurrentReaders = 10;

    // We settled on C# convention of PascalCase names for tables & columns for easy use of models
    // and everything. Also, we escape the table name with square brackets [].

    private readonly SqliteConnection _conn;
    private readonly AsyncRwLock _rwLock;

    static LauncherDatabase()
    {
        SqliteConnectionExtensions.DefaultHandlers.Add(new BooleanHandler());
        SqliteConnectionExtensions.DefaultHandlers.Add(new UtcInstantHandler());
        SqliteConnectionExtensions.DefaultHandlers.Add(new ListStringHandler());
    }

    public LauncherDatabase(SqliteConnection conn, int maxConcurrentReaders = DefaultMaxConcurrentReaders)
    {
        _conn = conn;
        _rwLock = new AsyncRwLock(maxConcurrentReaders);
    }

    public async Task EnsureCreatedAsync()
    {
        await _conn.OpenAsync();
        await _conn.ExecuteAsync("PRAGMA journal_mode = WAL;");
        await _conn.ExecuteAsync("PRAGMA synchronous = NORMAL;");
        await _conn.ExecuteAsync("PRAGMA foreign_keys = ON;");
        await _conn.ExecuteAsync("PRAGMA temp_store = memory;");
        await _conn.ExecuteAsync("PRAGMA cache_size = -32000;"); // 32 MiB, TODO: tweaks once we have bigger DB
        await _conn.ExecuteAsync("PRAGMA busy_timeout = 5000;"); // 5 seconds should be enough in our case

        await MigrateDatabase();
    }

    private async Task MigrateDatabase()
    {
        // We are using STRICT keyword when creating tables, so nobody can insert different data
        // type into a table even by accident. BUT we cannot use SQLite non-native types like
        // boolean, but we can enforce boolean-like behaviour with:
        // CHECK (HasMinecraftLicense IN (0, 1))

        // STRICT tables catch a lot of random mistakes ;)

        // TODO: Because of STRICT, and Dapper's broken overriding of default TypeHandlers, we must
        //  use TEXT type for timestamps. Waiting for Dapper 3
        //  https://github.com/DapperLib/Dapper/pull/471
        //  https://github.com/DapperLib/Dapper/issues/206
        //  https://github.com/DapperLib/Dapper/issues/688

        await Account.MigrateTable(_conn);
        await MinecraftInstance.MigrateTable(_conn);
    }

    public Task<IEnumerable<Account>> GetAllAccountsAsync()
    {
        return _rwLock.ExecuteReadAsync(async () =>
            await _conn.QueryAsync<Account>($"SELECT * FROM {Account.Table}"));
    }

    public Task UpsertAccountAsync(Account account)
    {
        return _rwLock.ExecuteWriteAsync(() => _conn.ExecuteAsync(
            $@"
            INSERT INTO {Account.Table} (Id, XboxAccountState, XboxUserId, Username, HasMinecraftLicense, SkinUrl, CapeUrl, AccessToken, RefreshToken, ExpiresAt)
            VALUES (@Id, @XboxAccountState, @XboxUserId, @Username, @HasMinecraftLicense, @SkinUrl, @CapeUrl, @AccessToken, @RefreshToken, @ExpiresAt)
            ON CONFLICT(Id) DO UPDATE SET
                XboxAccountState=excluded.XboxAccountState,
                XboxUserId=excluded.XboxUserId,
                Username=excluded.Username,
                HasMinecraftLicense=excluded.HasMinecraftLicense,
                SkinUrl=excluded.SkinUrl,
                CapeUrl=excluded.CapeUrl,
                AccessToken=excluded.AccessToken,
                RefreshToken=excluded.RefreshToken,
                ExpiresAt=excluded.ExpiresAt
            ",
            account));
    }

    public Task<IEnumerable<MinecraftInstance>> GetAllMinecraftInstancesAsync()
    {
        return _rwLock.ExecuteReadAsync(async () =>
            await _conn.QueryAsync<MinecraftInstance>($"SELECT * FROM {MinecraftInstance.Table}"));
    }

    public async Task<bool> MinecraftInstanceExists(string name)
    {
        var count = await _rwLock.ExecuteReadAsync(() =>
            _conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {MinecraftInstance.Table} WHERE Id = @Id;",
                bind: cmd => { cmd.Parameters.AddWithValue("@Id", name); }));
        return count == 1;
    }

    public Task InsertMinecraftInstanceAsync(MinecraftInstance instance)
    {
        return _rwLock.ExecuteWriteAsync(() => _conn.ExecuteAsync(
            $@"
                INSERT INTO {MinecraftInstance.Table} (Id, VersionId, State, Type, Folder, RequiredJavaVersion, ClientJarPath, MainClass, AssetIndex, ClassPath, GameArguments, JvmArguments)
                VALUES (@Id, @VersionId, @State, @Type, @Folder, @RequiredJavaVersion, @ClientJarPath, @MainClass, @AssetIndex, @ClassPath, @GameArguments, @JvmArguments)
                ",
            instance));
    }

    public Task SetMinecraftInstanceAsReadyAsync(string name)
    {
        return _rwLock.ExecuteWriteAsync(() => _conn.ExecuteAsync(
            $"UPDATE {MinecraftInstance.Table} SET State = @State WHERE Id = @Id",
            (name, MinecraftInstance.StateToString(MinecraftInstanceState.Ready)),
            static (cmd, args) =>
            {
                cmd.Parameters.AddWithValue("@Id", args.name);
                cmd.Parameters.AddWithValue("@State", args.Item2);
            }));
    }

    public void Dispose()
    {
        _conn.Dispose();
    }

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
