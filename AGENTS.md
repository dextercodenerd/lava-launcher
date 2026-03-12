# AGENTS.md

## Overview
- `LavaLauncher` is a .NET 10 desktop Minecraft launcher built with Avalonia UI and CommunityToolkit.MVVM.
- The solution is split into three projects:
  - `LavaLauncher.Desktop`: native desktop host and Avalonia bootstrap.
  - `GenericLauncher.Shared`: almost all application logic, UI, services, persistence, auth, and Minecraft integration.
  - `GenericLauncher.Tests`: unit tests.
- Release builds are trimmed and NativeAOT-enabled. Debug builds are intentionally more permissive for local iteration and Avalonia preview support.

## Project Layout
- `LavaLauncher.Desktop/Program.cs`: process entry point, Avalonia app builder, logging setup, crash policy.
- `GenericLauncher.Shared/App.axaml.cs`: composition root. This is where long-lived services are created and wired.
- `GenericLauncher.Shared/ApplicationViewModel.cs`: creates the main window and root view model.
- `GenericLauncher.Shared/Screens/*`: Avalonia views and CommunityToolkit MVVM view models.
- `GenericLauncher.Shared/Navigation/*`: stack-based page navigation.
- `GenericLauncher.Shared/Auth/*`: Microsoft/Xbox/Minecraft authentication flow.
- `GenericLauncher.Shared/Minecraft/*`: version management, installation, launching, mod loader integration.
- `GenericLauncher.Shared/Database/*`: SQLite access and the custom AOT-safe ORM layer.
- `GenericLauncher.Shared/*/Json/*Context.cs`: source-generated `System.Text.Json` contexts.

## Architecture Rules

### 1. Keep manual DI
- Do not introduce `IServiceCollection`, service providers, reflection-driven DI, or runtime composition frameworks.
- Long-lived services are created directly in `GenericLauncher.Shared/App.axaml.cs`.
- View models are instantiated manually, usually in `ApplicationViewModel` or `MainWindowViewModel`.

### 2. Preserve AOT and trimming safety
- Prefer static code paths, explicit types, and source generation.
- Do not introduce reflection-heavy libraries or patterns unless there is a strong reason and trimming impact is understood.
- Avoid APIs that rely on runtime type scanning or dynamic serialization metadata.
- The codebase intentionally avoids `ILogger<T>`; use plain `ILogger` and create it from `App.LoggerFactory`.

### 3. Use source-generated JSON
- Serialization is configured with `JsonSerializerIsReflectionEnabledByDefault=false`.
- When adding new JSON models, register them in the relevant `JsonSerializerContext` file instead of relying on reflection-based serialization.
- Keep naming policies aligned with the remote API being modeled.

### 4. Respect the current ownership model
- `App` owns long-lived services such as `AuthService`, `MinecraftLauncher`, `LauncherRepository`, `ModrinthApiClient`, and HTTP infrastructure.
- Root screens are reused.
- Transient screens are pushed onto `StackNavigationViewModel` and should implement `IDisposable` if they subscribe to long-lived events.

## UI and ViewModel Conventions
- The app uses CommunityToolkit MVVM attributes such as `[ObservableProperty]` and `[RelayCommand]`.
- Avalonia compiled bindings are enabled by default. Keep `x:DataType` accurate when changing views.
- Add screen-to-view mappings through Avalonia `DataTemplate`s, currently in `GenericLauncher.Shared/Screens/MainWindow/MainWindow.axaml`.
- Navigation is phone-style stack navigation:
  - Root pages implement `IPageViewModel` and usually return `IsRootScreen => true`.
  - Transient pages default to `false` and are disposed on back navigation or root replacement.
- UI-bound collections and state updates are marshaled back to the UI thread with `Dispatcher.UIThread.Post(...)` or guarded with `VerifyAccess()`.
- Follow the existing pattern where screens do not navigate directly to each other; `MainWindowViewModel` acts as the coordinator.

## Persistence and Concurrency
- SQLite access goes through `LauncherRepository` and `LauncherDatabase`; do not bypass them casually.
- The database layer uses a custom `AsyncRwLock` because SQLite allows only one writer and the project avoids ORM/runtime patterns that are problematic under AOT.
- The repository is responsible for initialization and database file placement under `%LocalAppData%/<AssemblyName>`.
- Several services maintain immutable snapshots plus events (`AuthService`, `MinecraftLauncher`). Preserve that pattern when adding state.
- Event handlers that touch Avalonia state must hop back to the UI thread.

## Configuration
- Shared build settings live in `Directory.Build.props`.
- Azure auth values are injected at build time from:
  - MSBuild properties,
  - environment variables,
  - or `user.props` copied from `user.example.props`.
- `GenericLauncher.Shared.csproj` generates `AzureConfig.generated.cs` from `Data/AzureConfig.template.cs`. Keep that pipeline intact if auth config changes.
- Be careful with `Product` and `AssemblyName` settings in `LavaLauncher.Desktop.csproj`; comments there explain why changing them can break installation/update behavior.

## Coding Guidance For Future Tasks
- Prefer extending existing services and view models over adding new abstractions.
- Match the existing namespace split: desktop host in `LavaLauncher.Desktop`, everything reusable in `GenericLauncher.Shared`.
- Reuse `HttpRetry.CreateHttpClient(...)` for HTTP behavior instead of ad hoc clients.
- Keep logging concise and use the existing logger factory pattern.
- When adding new screens:
  - create the view and view model,
  - implement `IPageViewModel` if it participates in navigation,
  - register a `DataTemplate`,
  - decide whether the page is root or transient,
  - dispose event subscriptions on transient pages.
- When adding new persisted fields, update:
  - the database model,
  - SQL migration is not needed while we are in early development,
  - the database schema,
  - repository mapping,
  - and any UI/state refresh code that depends on immutable snapshots.

## Verification
- Preferred baseline command:
  - `dotnet test LavaLauncher.sln`
- Current repo state on March 11, 2026:
  - `dotnet test LavaLauncher.sln` restores and builds successfully, but the test runner reports `No test is available` for `GenericLauncher.Tests`.
  - Treat test validation as incomplete until the test project is made discoverable by the configured runner.
- For build/publish-sensitive changes, also verify:
  - `dotnet build LavaLauncher.sln`
  - `dotnet publish LavaLauncher.Desktop/LavaLauncher.Desktop.csproj -c Release -r win-x64 -p:PublishAot=true`
- Any change touching serialization, auth, DI/composition, database access, or UI bindings should be reviewed with AOT/trimming in mind before considering the task complete.
