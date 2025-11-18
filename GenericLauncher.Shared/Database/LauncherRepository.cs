using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GenericLauncher.Minecraft;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Database;

public class LauncherRepository
{
    private readonly string _baseDir;
    private readonly ILogger? _logger;

    private readonly LauncherDatabase _db;
    private readonly Task _initTask;

    public LauncherRepository(
        string baseDir,
        ILogger? logger = null)
    {
        _baseDir = baseDir;
        _logger = logger;

        Directory.CreateDirectory(_baseDir);
        var dbPath = Path.Combine(_baseDir, "ll.sqlite");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
        };
        var dbConnection = new SqliteConnection(builder.ConnectionString);
        _db = new LauncherDatabase(dbConnection);

        _initTask = Task.Run(() => _db.EnsureCreatedAsync());
    }

    public async Task<IEnumerable<Account>> GetAllAccountsAsync()
    {
        await _initTask;
        return await _db.GetAllAccountsAsync();
    }

    public async Task SaveAccountAsync(Account account)
    {
        await _initTask;
        await _db.UpsertAccountAsync(account);
    }

    public async Task<IEnumerable<MinecraftInstance>> GetAllMinecraftInstancesAsync()
    {
        await _initTask;
        return await _db.GetAllMinecraftInstancesAsync();
    }

    public async Task<bool> MinecraftInstanceExists(string name)
    {
        await _initTask;
        return await _db.MinecraftInstanceExists(name);
    }

    public async Task AddInstallingMinecraftInstanceAsync(
        string name,
        MinecraftVersionManager.Version minecraft,
        string folderName)
    {
        var instance = new MinecraftInstance(
            name,
            minecraft.VersionId,
            MinecraftInstanceState.Installing,
            minecraft.Type,
            folderName,
            minecraft.RequiredJavaVersion,
            minecraft.ClientJarPath,
            minecraft.MainClass,
            minecraft.AssetIndex,
            minecraft.ClassPath,
            minecraft.GameArguments,
            minecraft.JvmArguments);

        await _initTask;
        await _db.InsertMinecraftInstanceAsync(instance);
    }

    public async Task SetMinecraftInstanceAsReadyAsync(string name)
    {
        await _initTask;
        await _db.SetMinecraftInstanceAsReadyAsync(name);
    }
}
