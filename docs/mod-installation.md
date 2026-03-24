# Mod Installation Architecture

This document describes how LavaLauncher discovers, installs, updates, and deletes Minecraft mods from Modrinth. It covers the data flow from the Modrinth API through the core manager to the UI layer.

## Service Composition

All services are created once in `App.axaml.cs` and wired manually (no DI container):

```
App.axaml.cs
├── HttpClient (shared, with retry policy)
├── FileDownloader(HttpClient)
├── ModrinthApiClient(HttpClient)
├── InstanceModsManager(Platform, ModrinthApiClient, FileDownloader, instanceProvider)
└── MinecraftLauncher(..., InstanceModsManager, ...)
        │
        └── ApplicationViewModel
                └── MainWindowViewModel(ModrinthApiClient, InstanceModsManager, ...)
                        ├── ModrinthSearchViewModel (root, long-lived)
                        ├── InstanceDetailsViewModel (transient, per-push)
                        └── ModrinthProjectDetailsViewModel (transient, per-push)
```

`MainWindowViewModel` is the navigation coordinator. It creates transient screen VMs and pushes them onto `StackNavigationViewModel`. Screens never navigate to each other directly.

## On-Disk Layout

Each Minecraft instance has a folder under `{AppData}/instances/{folder-name}/`:

```
instances/family-pack/
├── meta.json           ← Managed by InstanceModsManager (serialized InstanceMeta)
├── mods/
│   ├── sodium-0.6.0.jar
│   ├── lithium-0.14.3.jar
│   ├── custom-mod.jar  ← Manually placed by user, not in meta.json
│   └── ...
└── .mod-tmp/           ← Temporary folder during install/update, auto-cleaned
    └── <guid>/
```

### meta.json Schema (v1)

Serialized via source-generated `InstanceMetaJsonContext` with snake_case naming.

```json
{
  "schema_version": 1,
  "display_name": "Family Pack",
  "minecraft_version_id": "1.21.1",
  "mod_loader": "fabric",
  "mod_loader_version": "0.16.10",
  "launch_version_id": "1.21.1-fabric",
  "mods": [
    {
      "project_id": "AANobbMI",
      "project_slug": "sodium",
      "project_title": "Sodium",
      "installed_version_id": "4JRBesBX",
      "installed_version_number": "0.6.0",
      "installed_version_type": "release",
      "installed_file_name": "sodium-0.6.0.jar",
      "installed_file_sha512": "abc123...",
      "install_kind": "Direct",
      "required_by_project_ids": [],
      "installed_at_utc": "2026-03-24T00:00:00Z"
    },
    {
      "project_id": "dep123",
      "project_slug": "fabric-api",
      "project_title": "Fabric API",
      "install_kind": "Dependency",
      "required_by_project_ids": ["AANobbMI"],
      ...
    }
  ]
}
```

**`install_kind`** is either `"Direct"` (user-installed) or `"Dependency"` (pulled in automatically). `required_by_project_ids` stores the immediate parent project IDs recorded during dependency resolution, and orphan cleanup still comes from re-resolving the remaining direct mods on delete/update.

## Core: InstanceModsManager

**File:** `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs`

Central hub for all mod operations. Owns the snapshot cache, version cache, and per-instance locks.

### Concurrency Model

```
Per-instance AsyncRwLock (max 8 concurrent readers, exclusive writer)
├── Read lock: GetSnapshotAsync, ReadLocalState, metadata reads
└── Write lock: Install, Update, Delete, metadata writes

Snapshot cache ← protected by Lock (_snapshotLock)
Version cache  ← protected by Lock (_latestCompatibleVersionCacheLock)
```

`AsyncRwLock` (`GenericLauncher.Shared/Misc/AsyncRwLock.cs`) uses three `SemaphoreSlim`s:
- `_readerLock` (capacity = maxReaders): limits concurrent readers
- `_writerLock` (binary): ensures only one writer at a time
- `_blockReadersWhenPendingWriterLock` (binary): prevents new readers when a writer is waiting

The lock is reader-preferential and non-reentrant (read cannot upgrade to write).

### Snapshot vs. Update Info (Two-Phase State)

The manager separates **local truth** from **remote truth**:

1. **`GetSnapshotAsync`** — reads only local state (meta.json + scanned JAR files). Fast, no network calls, deterministic. Returns `InstanceModsSnapshot`.

2. **`GetLatestCompatibleVersionsAsync`** — queries Modrinth API for the latest compatible version of each project. Returns `ImmutableDictionary<string, LatestCompatibleVersionInfo>`. Results are cached per `{minecraftVersion}|{modLoader}|{projectId}`.

This separation means snapshots are never blocked by network latency. The UI fetches update info separately and enriches the display.

### Mod Categories

`BuildSnapshot` classifies each mod into one of four lists:

| Category | Source | CanUpdate | CanDelete |
|---|---|---|---|
| **InstalledMods** | In meta.json as `Direct`, JAR exists | Yes | Yes |
| **RequiredDependencies** | In meta.json as `Dependency`, JAR exists | No | No |
| **ManualMods** | JAR in mods/ but not in meta.json | No | Yes |
| **BrokenMods** | In meta.json but JAR is missing | No | Depends on kind |

## Installation Flow

### Entry Points

There are two ways to trigger a mod install:

1. **Instance-scoped search** — from the Mods tab of `InstanceDetailsViewModel`, user clicks "Add Mods", which opens an inline `ModrinthSearchViewModel` pre-filtered to the instance's Minecraft version and mod loader.

2. **Root Modrinth search** — from the sidebar, user browses Modrinth globally. When installing, a picker dialog (`ModrinthInstallTargetPickerViewModel`) shows compatible instances.

### Install Pipeline (ApplyManagedModChangeAsync)

All managed mutations (install, update, update-all, and deletion of managed direct mods) flow through `ApplyManagedModChangeAsync`. Manual JAR deletion uses a smaller write-locked path that deletes the file and rebuilds the snapshot:

```
1. EnsureInstanceMetadataAsync
   └── Creates or updates meta.json if needed

2. ReadLocalStateAsync (read lock)
   └── Reads meta.json + scans mods/ folder → LocalInstanceState

3. BuildDesiredDirectMods(meta, projectId, ModChangeKind)
   └── Builds target set of direct mods {projectId → versionId?}
       null versionId = "resolve latest compatible"

4. ResolveDesiredDirectModsAsync (parallel, up to 4 concurrent)
   └── For each desired direct mod:
       ├── ResolveProjectAsync
       │   ├── Fetch version from Modrinth (by ID or latest compatible)
       │   ├── Fetch project metadata
       │   ├── Recursively resolve required dependencies
       │   └── Detect conflicts (same project, different versions)
       └── MergeResolutionState (combine per-mod results)
       → ResolutionState (all projects + versions + files + dependency graph)

5. PrepareDownloadsAsync (outside lock)
   └── For each resolved project:
       ├── Check if existing file has matching expected hash → skip
       └── Download to .mod-tmp/<guid>/<filename>, verify hash

6. ExecuteWithInstanceWriteLockAsync
   ├── Re-read meta.json
   ├── EnsureExpectedMeta (optimistic concurrency: compare observed vs current)
   └── CommitPreparedResolutionUnlockedAsync
       ├── Backup existing files → .mod-tmp/<guid>/<random>.bak
       ├── Move downloaded files to mods/
       ├── Write new meta.json (atomic: write to .tmp, then rename)
       ├── Delete obsolete managed files
       ├── Delete backups
       └── On failure: restore backups, delete new files (rollback)

7. StoreSnapshot + PublishSnapshot
   └── Cache new snapshot, fire InstanceModsChanged event
```

### Dependency Resolution

`ResolveProjectAsync` walks the dependency tree recursively:

- Only `"required"` dependencies are followed (optional/incompatible are skipped)
- If a dependency is already resolved with the same version → merge (add to `RequiredByProjectIds`)
- If a dependency is resolved with a different version → throw conflict error
- A project that appears as both direct and dependency is marked `Direct` (direct wins)

### Version Selection

`SelectBestVersion` picks from available versions:
1. Prefer `release` over `beta` over `alpha`
2. Among same stability tier, prefer most recently published

`SelectInstallFile` picks from version files:
1. Filter to `.jar` files only
2. Prefer primary file
3. Alphabetical fallback

## Update Flow

**Single mod update** (`UpdateModAsync`): Sets the target version to `null` for the specified project, triggering re-resolution to the latest compatible version.

**Update all** (`UpdateAllAsync`): Sets all direct mods' target versions to `null`.

Both flow through the same `ApplyManagedModChangeAsync` pipeline, which re-resolves all direct mods and their dependencies from scratch.

## Delete Flow

`DeleteModAsync` handles three cases:

```
1. Identify target type (read lock)
   ├── Managed Direct → ApplyManagedModChangeAsync with ModChangeKind.Remove
   │   └── Re-resolves remaining direct mods → orphaned dependencies are dropped
   ├── Dependency → throw ("cannot be deleted manually")
   └── Manual → write lock, File.Delete, rebuild snapshot
```

Orphan cleanup happens naturally: when a direct mod is removed, `BuildDesiredDirectMods` excludes it, and `ResolveDesiredDirectModsAsync` only resolves dependencies of remaining mods. Any dependency not referenced by a remaining mod is omitted from the new `ResolutionState`, and `CollectObsoleteManagedFiles` marks its file for deletion.

## Event-Driven UI Updates

```
InstanceModsManager.InstanceModsChanged event
    │
    ├── InstanceDetailsViewModel.OnInstanceModsChanged
    │   ├── Dispatcher.UIThread.Post → ApplyModsSnapshot(snapshot)
    │   │   └── RenderModLists → updates ObservableCollections
    │   │       └── InlineSearchViewModel?.ApplyTargetState(snapshot, versions)
    │   └── RefreshUpdateStatusesAsync (background)
    │       └── GetLatestCompatibleVersionsAsync → ApplyLatestCompatibleVersions
    │
    ├── ModrinthProjectDetailsViewModel.OnInstanceModsChanged
    │   ├── Dispatcher.UIThread.Post → ApplySnapshot(snapshot)
    │   │   └── Updates TargetProjectState → ShowInstallAction / ShowUpdateAction
    │   └── RefreshTargetLatestCompatibleVersionAsync (background)
    │
    └── ModrinthSearchViewModel (via ApplyTargetState called from InstanceDetailsViewModel)
        └── Updates each search result's install/update buttons and status text
```

Screens subscribe in their constructor and unsubscribe in `Dispose()`. Transient screens are disposed when popped from the navigation stack.

## Update Button Visibility

The "Update" button is shown only when all conditions are met:

```csharp
ShowUpdateAction = CanInstall                                    // project type is "mod"
    && TargetInstance is not null                                 // instance-scoped context
    && TargetProjectState is { InstallKind: Direct, IsBroken: false }
    && LatestCompatibleVersion is not null                        // API returned a version
    && LatestCompatibleVersion.VersionId != InstalledVersionId    // not already latest
```

This logic lives in the VM layer (`ModrinthProjectDetailsViewModel`, `ModrinthSearchResultItemViewModel`), not in the snapshot, since update info is fetched separately from snapshot building.

## Search and Filtering

`ModrinthSearchViewModel` manages search against the Modrinth API with:

- **Debounced text input** — `DispatcherTimer` at 500ms; resets on each keystroke
- **Facet filtering** — when instance-scoped, adds `categories:{loader}`, `versions:{minecraftVersion}`, and client-side compatibility facets
- **Cancellation** — each search cancels the previous in-flight request via `CancellationTokenSource`
- **Pagination** — 20 results per page with next/previous navigation

The same `ModrinthSearchViewModel` is used in two contexts:
- **Root search** (sidebar) — `ModrinthSearchContext.CreateRoot()`, no filters, shows all project types
- **Instance-scoped** (inline in Mods tab) — `ModrinthSearchContext.CreateForInstance(instance)`, locked to mods, pre-filtered by version and loader

## Optimistic Concurrency

Before committing changes inside the write lock, `EnsureExpectedMeta` compares the metadata read before resolution with the current metadata. If another operation modified state in between (e.g., concurrent install from another screen), it throws rather than silently corrupting. The comparison uses JSON serialization of both `InstanceMeta` values for simplicity and immunity to field additions.

## File Safety

- **Atomic metadata writes**: meta.json is written to a `.tmp` file first, then atomically renamed
- **Download verification**: every downloaded file is verified against the expected hash from Modrinth metadata (the manager prefers SHA-512 and falls back to SHA-1 when needed; `FileDownloader` also supports SHA-256)
- **Transactional commits**: existing files are backed up before replacement; on failure, backups are restored and new files are deleted
- **Temp folder cleanup**: `PreparedDownloads` implements `IDisposable`, and `ApplyManagedModChangeAsync` disposes it in a `finally` block to clean up `.mod-tmp/<guid>/`

## Key Files

| File | Role |
|---|---|
| `InstanceMods/InstanceModsManager.cs` | Core manager: install, update, delete, snapshot, caching |
| `InstanceMods/InstanceModsSnapshot.cs` | Immutable snapshot + state records |
| `InstanceMods/Json/InstanceMetaJsonContext.cs` | AOT-safe JSON serializer for meta.json |
| `InstanceMods/Json/InstanceMetaModels.cs` | `InstanceMeta` and `InstanceMetaMod` records |
| `Screens/InstanceDetails/InstanceDetailsViewModel.cs` | Mods tab UI, inline search, update/delete actions |
| `Screens/ModrinthSearch/ModrinthSearchViewModel.cs` | Search screen with debounce, pagination, install |
| `Screens/ModrinthSearch/ModrinthSearchResultItemViewModel.cs` | Single search result with install state display |
| `Screens/ModrinthSearch/ModrinthSearchContext.cs` | Search scope (root vs instance-scoped) |
| `Screens/ModrinthProjectDetails/ModrinthProjectDetailsViewModel.cs` | Project detail page with install/update |
| `Screens/ModrinthSearch/ModrinthInstallTargetPickerViewModel.cs` | Instance picker dialog for root search install |
| `Screens/MainWindow/MainWindowViewModel.cs` | Navigation coordinator, creates screen VMs |
| `Modrinth/ModrinthApiClient.cs` | Modrinth REST API client |
| `Http/FileDownloader.cs` | Hash-verified file downloads with concurrency limit |
| `Misc/AsyncRwLock.cs` | Async reader-writer lock primitive |
