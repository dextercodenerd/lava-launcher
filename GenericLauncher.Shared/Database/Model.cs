using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using GenericLauncher.Database.Orm;
using GenericLauncher.Misc;
using Microsoft.Data.Sqlite;

namespace GenericLauncher.Database;

public record Account(
    // The user's UUID
    string Id,
    string? XboxUserId,
    string Username,
    bool HasMinecraftLicense,
    string? SkinUrl,
    string? CapeUrl,
    string AccessToken,
    string RefreshToken,
    UtcInstant ExpiresAt
) : IRecord<Account>, IParams<Account>
{
    // We settled on C# convention of PascalCase names for tables & columns for easy use of models
    // and everything. Also, we escape the table name with square brackets [].
    public const string Table = "[Accounts]";

    // STRICT tables catch a lot of random mistakes ;)

    // TODO: Because of STRICT, and Dapper's broken overriding of default TypeHandlers, we must
    //  use TEXT type for timestamps. Waiting for Dapper 3
    //  https://github.com/DapperLib/Dapper/pull/471
    //  https://github.com/DapperLib/Dapper/issues/206
    //  https://github.com/DapperLib/Dapper/issues/688
    public static Task<int> MigrateTable(SqliteConnection conn) =>
        conn.ExecuteAsync($@"
            CREATE TABLE IF NOT EXISTS {Table} (
                Id TEXT PRIMARY KEY,
                XboxUserId TEXT,
                Username TEXT NOT NULL,
                HasMinecraftLicense INTEGER NOT NULL CHECK (HasMinecraftLicense IN (0, 1)),
                SkinUrl TEXT,
                CapeUrl TEXT,
                AccessToken TEXT NOT NULL,
                RefreshToken TEXT NOT NULL,
                ExpiresAt INTEGER NOT NULL
            ) STRICT");

    public static Account Read(Row row)
    {
        var id = row.Get<string>(row.Ord("Id"));
        var xboxUserId = row.Get<string?>(row.Ord("XboxUserId"));
        var username = row.Get<string>(row.Ord("Username"));
        var hasMc = row.Get<bool>(row.Ord("HasMinecraftLicense"));
        var skinUrl = row.Get<string?>(row.Ord("SkinUrl"));
        var capeUrl = row.Get<string?>(row.Ord("CapeUrl"));
        var accessToken = row.Get<string>(row.Ord("AccessToken"));
        var refreshToken = row.Get<string>(row.Ord("RefreshToken"));
        var expireAt = row.Get<UtcInstant>(row.Ord("ExpiresAt"));

        return new Account(id, xboxUserId, username, hasMc, skinUrl, capeUrl, accessToken, refreshToken, expireAt);
    }

    public static void Bind(SqliteCommand cmd, Account v, Orm.TypeHandlers handlers)
    {
        cmd.Parameters.Clear();
        cmd.AddParam("@Id", v.Id, handlers, DbType.String);
        cmd.AddParam("@XboxUserId", v.XboxUserId, handlers, DbType.String);
        cmd.AddParam("@Username", v.Username, handlers, DbType.String);
        cmd.AddParam("@HasMinecraftLicense", v.HasMinecraftLicense, handlers, DbType.Int64);
        cmd.AddParam("@SkinUrl", v.SkinUrl, handlers, DbType.String);
        cmd.AddParam("@CapeUrl", v.CapeUrl, handlers, DbType.String);
        cmd.AddParam("@AccessToken", v.AccessToken, handlers, DbType.String);
        cmd.AddParam("@RefreshToken", v.RefreshToken, handlers, DbType.String);
        cmd.AddParam("@ExpiresAt", v.ExpiresAt, handlers, DbType.Int64);
    }
}

// TODO: Dapper.AOT has many problems e.g.,
//  1) there is no annotation to ignore property, so it maps everything
//  2) it ignores List<string> TypeHandler
//  3) a + 2 together forces us to do a dance via a method, if we want List<string> data in the DB
//  follow:
//  https://github.com/DapperLib/DapperAOT/pull/162
//  https://github.com/DapperLib/DapperAOT/pull/151
//  https://github.com/DapperLib/DapperAOT/issues/163
//  https://github.com/DapperLib/DapperAOT/issues/159

// TODO: Another thought, we probably do not need all this information here, besides the Id/Name and
//  VersionId, because we cache the client.json files and parse them before launch. So do a clean-up
//  of the properties to bare a minimum.

public enum MinecraftInstanceState
{
    Unknown,
    Installing,
    Ready,
}

public record MinecraftInstance(
    string Id,
    string VersionId,
    MinecraftInstanceState State,
    string Type,
    string Folder,
    long RequiredJavaVersion,
    string ClientJarPath,
    string MainClass,
    string AssetIndex,
    List<string> ClassPath,
    List<string> GameArguments,
    List<string> JvmArguments
) : IRecord<MinecraftInstance>, IParams<MinecraftInstance>
{
/*
    private List<string>? _classPathList;

    public List<string> GetClassPathList()
    {
        _classPathList ??= ParseList(ClassPath);
        return _classPathList;
    }

    private List<string>? _gameArgumentsList;

    public List<string> GetGameArgumentsList()
    {
        _gameArgumentsList ??= ParseList(GameArguments);
        return _gameArgumentsList;
    }

    private List<string>? _jvmArgumentsList;

    public List<string> GetJvmArgumentsList()
    {
        _jvmArgumentsList ??= ParseList(JvmArguments);
        return _jvmArgumentsList;
    }

    public static MinecraftInstance Create(
        string id,
        string versionId,
        string type,
        string folder,
        long requiredJavaVersion,
        string clientJarPath,
        string mainClass,
        string assetIndex,
        List<string> classPath,
        List<string> gameArguments,
        List<string> jvmArguments) =>
        new(id,
            versionId,
            type,
            folder,
            (int)requiredJavaVersion,
            clientJarPath,
            mainClass,
            assetIndex,
            JsonSerializer.Serialize(classPath, SqliteJsonContext.Default.ListString),
            JsonSerializer.Serialize(gameArguments, SqliteJsonContext.Default.ListString),
            JsonSerializer.Serialize(jvmArguments, SqliteJsonContext.Default.ListString));

    public static List<string> ParseList(string? listJson) =>
        string.IsNullOrWhiteSpace(listJson)
            ? []
            : JsonSerializer.Deserialize<List<string>>(listJson, SqliteJsonContext.Default.ListString)
              ?? [];
*/
    public const string Table = "[MinecraftInstances]";

    public static Task<int> MigrateTable(SqliteConnection conn) => conn.ExecuteAsync($@"
            CREATE TABLE IF NOT EXISTS {Table} (
                Id TEXT PRIMARY KEY,
                VersionId TEXT NOT NULL,
                State TEXT NOT NULL,
                Type TEXT NOT NULL,
                Folder TEXT NOT NULL,
                RequiredJavaVersion INTEGER NOT NULL,
                ClientJarPath TEXT NOT NULL,
                MainClass TEXT NOT NULL,
                AssetIndex TEXT NOT NULL,
                ClassPath TEXT NOT NULL,
                GameArguments TEXT NOT NULL,
                JvmArguments TEXT NOT NULL
            ) STRICT");

    public static MinecraftInstance Read(Row row)
    {
        var id = row.Get<string>(row.Ord("Id"));
        var versionId = row.Get<string>(row.Ord("VersionId"));
        var state = row.Get<string>(row.Ord("State"));
        var type = row.Get<string>(row.Ord("Type"));
        var folder = row.Get<string>(row.Ord("Folder"));
        var requiredJavaVersion = row.Get<long>(row.Ord("RequiredJavaVersion"));
        var clientJarPath = row.Get<string>(row.Ord("ClientJarPath"));
        var mainClass = row.Get<string>(row.Ord("MainClass"));
        var assetIndex = row.Get<string>(row.Ord("AssetIndex"));
        var classPath = row.Get<List<string>>(row.Ord("ClassPath"));
        var gameArguments = row.Get<List<string>>(row.Ord("GameArguments"));
        var jvmArguments = row.Get<List<string>>(row.Ord("JvmArguments"));

        return new MinecraftInstance(id,
            versionId,
            StateFromString(state),
            type,
            folder,
            (int)requiredJavaVersion,
            clientJarPath,
            mainClass,
            assetIndex,
            classPath,
            gameArguments,
            jvmArguments);
    }

    public static void Bind(SqliteCommand cmd, MinecraftInstance v, Orm.TypeHandlers handlers)
    {
        cmd.Parameters.Clear();
        cmd.AddParam("@Id", v.Id, handlers, DbType.String);
        cmd.AddParam("@VersionId", v.VersionId, handlers, DbType.String);
        cmd.AddParam("State", StateToString(v.State), handlers, DbType.String);
        cmd.AddParam("@Type", v.Type, handlers, DbType.String);
        cmd.AddParam("@Folder", v.Folder, handlers, DbType.String);
        cmd.AddParam("@RequiredJavaVersion", v.RequiredJavaVersion, handlers, DbType.Int64);
        cmd.AddParam("@ClientJarPath", v.ClientJarPath, handlers, DbType.String);
        cmd.AddParam("@MainClass", v.MainClass, handlers, DbType.String);
        cmd.AddParam("@AssetIndex", v.AssetIndex, handlers, DbType.String);
        cmd.AddParam("@ClassPath", v.ClassPath, handlers, DbType.String);
        cmd.AddParam("@GameArguments", v.GameArguments, handlers, DbType.String);
        cmd.AddParam("@JvmArguments", v.JvmArguments, handlers, DbType.String);
    }

    public static MinecraftInstanceState StateFromString(string raw) => raw switch
    {
        "INSTALLING" => MinecraftInstanceState.Installing,
        "READY" => MinecraftInstanceState.Ready,
        _ => MinecraftInstanceState.Unknown,
    };


    public static string StateToString(MinecraftInstanceState state) => state switch
    {
        MinecraftInstanceState.Installing => "INSTALLING",
        MinecraftInstanceState.Ready => "READY",
        _ => "UNKNOWN",
    };
}
