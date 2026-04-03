using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft;
using GenericLauncher.Misc;
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
        LauncherPlatform platform,
        ILogger? logger = null)
    {
        _baseDir = platform.AppDataPath;
        _logger = logger;

        Directory.CreateDirectory(_baseDir);
        var dbPath = Path.Combine(_baseDir, "ll.sqlite");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, // shared cache is obsolete and replaced by WAL
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

    public async Task<bool> RemoveAccountAsync(Account account)
    {
        await _initTask;
        return await _db.RemoveAccountAsync(account.Id);
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
        string folderName,
        MinecraftInstanceModLoader modLoader,
        string launchVersionId,
        string? modLoaderVersion)
    {
        var instance = new MinecraftInstance(
            name,
            minecraft.VersionId,
            launchVersionId,
            modLoader,
            modLoaderVersion,
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

    public async Task SetMinecraftInstanceStateAsync(string instanceId, MinecraftInstanceState state)
    {
        await _initTask;
        await _db.SetMinecraftInstanceStateAsync(instanceId, state);
    }

    public async Task SetMinecraftInstanceClassPathAsync(string instanceId, List<string> classPath)
    {
        await _initTask;
        await _db.SetMinecraftInstanceClassPathAsync(instanceId, classPath);
    }

    public async Task<bool> RemoveMinecraftInstanceAsync(string instanceId)
    {
        await _initTask;
        return await _db.DeleteMinecraftInstanceAsync(instanceId);
    }
}
