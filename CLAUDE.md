# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **Read AGENTS.md** for full architecture rules, coding conventions, and guidance before making changes.

## Commands

```bash
# Run tests
dotnet test LavaLauncher.sln

# Run a single test by name
dotnet test LavaLauncher.sln --filter "FullyQualifiedName~MyTestName"

# Build
dotnet build LavaLauncher.sln

# Run locally (debug)
dotnet run --project LavaLauncher.Desktop/LavaLauncher.Desktop.csproj

# AOT publish (Windows)
dotnet publish LavaLauncher.Desktop/LavaLauncher.Desktop.csproj -c Release -r win-x64 -p:PublishAot=true
```

For Azure auth config, copy `user.example.props` to `user.props` and fill in values before building.

## Architecture

**Startup flow**: `Program.cs` → Avalonia bootstraps `App` (composition root in `App.axaml.cs`) → `ApplicationViewModel` creates `MainWindowViewModel` → `MainWindowViewModel` owns `StackNavigationViewModel` and all root page VMs.

**Service ownership** (`App.axaml.cs`): `AuthService`, `MinecraftLauncher`, `LauncherRepository`, `ModrinthApiClient`, `HttpClient`, and all mod loader services are created once here and passed down manually — no DI container.

**Navigation**: `StackNavigationViewModel` holds a `Stack<IPageViewModel>`. `SetRoot()` replaces the root and disposes any non-root pages in the backstack. `Push()` adds transient pages. `MainWindowViewModel` is the navigation coordinator — screens don't navigate to each other directly.

**Screen lifecycle**: Root screens (`IsRootScreen = true`) are never disposed. Transient screens implement `IDisposable` if they subscribe to long-lived events, and are disposed on `Pop()` or `SetRoot()`.

**State pattern**: `AuthService` and `MinecraftLauncher` maintain immutable snapshots + events. UI subscribes to events and posts updates to `Dispatcher.UIThread`.

**Persistence**: All SQLite access goes through `LauncherRepository` → `LauncherDatabase`. Custom `AsyncRwLock` handles single-writer concurrency. No ORM.

**JSON**: `JsonSerializerIsReflectionEnabledByDefault=false`. New models must be registered in the appropriate `*JsonSerializerContext.cs` file under `*/Json/`.

**Code generation**: `AzureConfig.generated.cs` and `AppConfig.generated.cs` are generated at build time by MSBuild targets in `GenericLauncher.Shared.csproj` from templates in `Data/`. Do not edit the generated files.

**Logging**: Use `ILogger` (not `ILogger<T>`), created via `App.LoggerFactory?.CreateLogger(typeof(...))`.
