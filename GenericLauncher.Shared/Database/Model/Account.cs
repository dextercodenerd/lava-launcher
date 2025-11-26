using System.Data;
using System.Threading.Tasks;
using GenericLauncher.Database.Orm;
using GenericLauncher.Misc;
using Microsoft.Data.Sqlite;

namespace GenericLauncher.Database.Model;

public enum XboxAccountState
{
    Unknown,
    Ok,
    Missing,
    Banned,
    NotAvailable,
    AgeVerificationMissing,
}

public record Account(
    // Global account id -- hash of Microsoft 'tid' (tenant id) and 'sub' JWT claims
    string Id,
    XboxAccountState XboxAccountState,
    // The user's Minecraft UUID -- null, when the account doesn't have Minecraft
    string? MinecraftUserId,
    string? XboxUserId,
    string? Username,
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
    public static Task<int> MigrateTable(SqliteConnection conn) =>
        conn.ExecuteAsync($@"
            CREATE TABLE IF NOT EXISTS {Table} (
                Id TEXT PRIMARY KEY,
                XboxAccountState TEXT NOT NULL,
                MinecraftUserId TEXT,
                XboxUserId TEXT,
                Username TEXT,
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
        var xboxAccountState = row.Get<string>(row.Ord("XboxAccountState"));
        var minecraftUserId = row.Get<string?>(row.Ord("MinecraftUserId"));
        var xboxUserId = row.Get<string?>(row.Ord("XboxUserId"));
        var username = row.Get<string?>(row.Ord("Username"));
        var hasMc = row.Get<bool>(row.Ord("HasMinecraftLicense"));
        var skinUrl = row.Get<string?>(row.Ord("SkinUrl"));
        var capeUrl = row.Get<string?>(row.Ord("CapeUrl"));
        var accessToken = row.Get<string>(row.Ord("AccessToken"));
        var refreshToken = row.Get<string>(row.Ord("RefreshToken"));
        var expireAt = row.Get<UtcInstant>(row.Ord("ExpiresAt"));

        return new Account(id,
            XboxAccountStateFromString(xboxAccountState),
            minecraftUserId,
            xboxUserId,
            username,
            hasMc,
            skinUrl,
            capeUrl,
            accessToken,
            refreshToken,
            expireAt);
    }

    public static void Bind(SqliteCommand cmd, Account v, Orm.TypeHandlers handlers)
    {
        cmd.Parameters.Clear();
        cmd.AddParam("@Id", v.Id, handlers, DbType.String);
        cmd.AddParam("@XboxAccountState", XboxAccountStateToString(v.XboxAccountState), handlers, DbType.String);
        cmd.AddParam("@MinecraftUserId", v.MinecraftUserId, handlers, DbType.String);
        cmd.AddParam("@XboxUserId", v.XboxUserId, handlers, DbType.String);
        cmd.AddParam("@Username", v.Username, handlers, DbType.String);
        cmd.AddParam("@HasMinecraftLicense", v.HasMinecraftLicense, handlers, DbType.Int64);
        cmd.AddParam("@SkinUrl", v.SkinUrl, handlers, DbType.String);
        cmd.AddParam("@CapeUrl", v.CapeUrl, handlers, DbType.String);
        cmd.AddParam("@AccessToken", v.AccessToken, handlers, DbType.String);
        cmd.AddParam("@RefreshToken", v.RefreshToken, handlers, DbType.String);
        cmd.AddParam("@ExpiresAt", v.ExpiresAt, handlers, DbType.Int64);
    }


    public static XboxAccountState XboxAccountStateFromString(string raw) => raw switch
    {
        "OK" => XboxAccountState.Ok,
        "MISSING" => XboxAccountState.Missing,
        "BANNED" => XboxAccountState.Banned,
        "NOT_AVAILABLE" => XboxAccountState.NotAvailable,
        "AGE_VERIFICATION_MISSING" => XboxAccountState.AgeVerificationMissing,
        _ => XboxAccountState.Unknown,
    };


    public static string XboxAccountStateToString(XboxAccountState state) => state switch
    {
        XboxAccountState.Ok => "OK",
        XboxAccountState.Missing => "MISSING",
        XboxAccountState.Banned => "BANNED",
        XboxAccountState.NotAvailable => "NOT_AVAILABLE",
        XboxAccountState.AgeVerificationMissing => "AGE_VERIFICATION_MISSING",
        _ => "UNKNOWN",
    };
}
