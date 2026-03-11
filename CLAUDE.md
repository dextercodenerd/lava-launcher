# CLAUDE.md - LavaLauncher Project Guide

## Important: Read AGENTS.md First

**Please read [`AGENTS.md`](./AGENTS.md) before starting work on this project.** It contains essential architecture rules, conventions, and coding guidance.

## Project Overview

LavaLauncher is a .NET 10 desktop Minecraft launcher built with Avalonia UI and CommunityToolkit.MVVM. The codebase is optimized for NativeAOT and trimming, with strict architectural patterns to maintain these constraints.

## Key Architectural Rules (from AGENTS.md)

1. **Manual DI Only**: No IServiceCollection or reflection-driven DI. Long-lived services are created directly in `GenericLauncher.Shared/App.axaml.cs`.

2. **AOT & Trimming Safety**: Prefer static code paths and explicit types. Avoid reflection-heavy libraries and runtime type scanning.

3. **Source-Generated JSON**: All serialization uses `System.Text.Json` with `JsonSerializerIsReflectionEnabledByDefault=false`. Register new JSON models in relevant `JsonSerializerContext` files.

4. **Ownership Model**: App owns long-lived services (AuthService, MinecraftLauncher, LauncherRepository, etc.). Root screens are reused; transient screens are pushed onto StackNavigationViewModel.

5. **UI Conventions**: Use CommunityToolkit MVVM attributes, maintain accurate `x:DataType` in Avalonia views, and marshal UI updates to the UI thread.

6. **Persistence**: Use `LauncherRepository` and `LauncherDatabase` for all SQLite access. The database layer uses a custom `AsyncRwLock` for concurrency.

## Project Layout

- `LavaLauncher.Desktop/`: Native desktop host and Avalonia bootstrap
- `GenericLauncher.Shared/`: Core application logic, UI, services, persistence, auth, and Minecraft integration
- `GenericLauncher.Tests/`: Unit tests

## Verification

Run tests with:
```bash
dotnet test LavaLauncher.sln
```

For build/publish verification:
```bash
dotnet build LavaLauncher.sln
dotnet publish LavaLauncher.Desktop/LavaLauncher.Desktop.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Next Steps

Refer to **AGENTS.md** for:
- Detailed project layout
- Complete architecture rules
- Coding guidance for new features
- Database and persistence patterns
- Configuration and build settings
