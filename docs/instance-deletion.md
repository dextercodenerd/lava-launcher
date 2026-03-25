# Add instance deletion to LavaLauncher

This ExecPlan is a living document. The sections `Constraints`, `Tolerances`, `Risks`, `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds.

Status: COMPLETE

## Purpose / big picture

Users can create Minecraft instances but cannot delete them. After this change, a user viewing an instance's details can delete it via a two-step confirmation flow. On confirmation, the instance transitions through visible states: `Ready` → `Deleting` → gone (or `DeleteFailed` on error). The user sees a "Deleting..." status in the UI while the disk folder is being removed. Only after successful disk cleanup is the DB record deleted. If deletion fails, the instance stays in `DeleteFailed` state with a retry option — even across app restarts.

**State machine:**
```
Ready ──────→ Deleting ──────→ (DB record removed, instance gone)
                  │
                  └──→ DeleteFailed ──→ Deleting (retry)
```

## Constraints

- No new DI container or reflection-heavy patterns (AGENTS.md rule 1).
- AOT/trimming safe — no new reflection, no new JSON models needed.
- Extend existing services rather than introducing new abstractions.
- Follow the existing callback pattern for navigation (screens don't navigate directly; `MainWindowViewModel` coordinates).
- Cannot delete a running instance (`LaunchedInstances` guard).
- Cannot delete an instance that is currently installing.

## Tolerances

- Scope: max ~10 files changed, ~150 lines net added. Escalate if significantly more.
- No new external dependencies.
- No public API signature changes to existing methods (only additions).

## Risks

- Risk: Disk deletion fails (permissions, file locks from antivirus, etc.).
  Severity: low. Likelihood: low.
  Mitigation: Instance transitions to `DeleteFailed` state. User can retry. On next app launch, `DeleteFailed` instances are visible with a retry option.

- Risk: Concurrent mod operation in progress when delete is requested.
  Severity: medium. Likelihood: low.
  Mitigation: `MinecraftLauncher._lock` serializes `DeleteInstanceAsync` against `CreateInstance`. Any in-flight mod operation on the folder will fail gracefully.

- Risk: Portable instance re-import discovers a `DeleteFailed` instance's folder.
  Severity: low. Likelihood: low.
  Mitigation: `DeleteFailed` instances still have a DB record with a known `Folder`, so `ImportPortableInstancesAsync` skips them (it checks `knownFolders`).

## Progress

- [x] (2026-03-25) Stage 1: Add `Deleting` and `DeleteFailed` to `MinecraftInstanceState` enum and string conversions
- [x] (2026-03-25) Stage 2: Database layer — add state update and delete methods
- [x] (2026-03-25) Stage 3: InstanceModsManager — add cache eviction
- [x] (2026-03-25) Stage 4: MinecraftLauncher — add `DeleteInstanceAsync` with soft-delete flow
- [x] (2026-03-25) Stage 5: InstanceDetailsViewModel — add delete commands, confirmation state, onDeleted callback, DeleteFailed retry
- [x] (2026-03-25) Stage 6: MainWindowViewModel — pass onDeleted callback
- [x] (2026-03-25) Stage 7: InstanceDetailsView.axaml — add delete UI with confirmation and retry
- [x] (2026-03-25) Stage 8: Home screen — show Deleting/DeleteFailed status correctly (verified: no changes needed)
- [x] (2026-03-25) Stage 9: Validation — build succeeded (0 errors), 180 tests passed

## Surprises & discoveries

(None yet.)

## Decision log

- Decision: Soft-delete with `Deleting` and `DeleteFailed` states instead of immediate DB deletion.
  Rationale: User requested visible progress during deletion. Soft-delete lets the UI show "Deleting..." while disk cleanup happens. If disk deletion fails, `DeleteFailed` state persists across restarts with a retry button.
  Date: 2026-03-24.

- Decision: Use inline confirmation panel instead of a dialog.
  Rationale: For a simple yes/no confirmation, an inline panel is simpler than DialogHost infrastructure.
  Date: 2026-03-24.

- Decision: Navigate back via `_onDeleted` callback.
  Rationale: Follows the existing coordinator pattern where `MainWindowViewModel` owns navigation.
  Date: 2026-03-24.

## Outcomes & retrospective

Implementation completed 2026-03-25. All 9 stages delivered as planned with no surprises:

- Build: `dotnet build LavaLauncher.sln` succeeded, 0 errors, 0 warnings.
- Tests: `dotnet test LavaLauncher.sln` — 180 passed, 0 failed, no regressions.
- 7 files changed; well within the ~10 file tolerance.
- Constructor signature change to `InstanceDetailsViewModel` was additive only (new optional parameters with defaults); existing callers unaffected except `MainWindowViewModel` which was updated to pass `onDeleted`.
- No new dependencies introduced.

## Context and orientation

LavaLauncher is a .NET 10 Avalonia desktop Minecraft launcher. Instances are Minecraft installations with a specific version, mod loader, and mods. Each instance has:
- A row in the `MinecraftInstances` SQLite table (keyed by `Id`, with a `Folder` column for the disk directory name). The `State` column is `TEXT NOT NULL` with no CHECK constraint on values.
- A folder at `{AppDataPath}/instances/{Folder}/` containing `meta.json`, `mods/`, `natives/`, etc.
- In-memory cached state in `InstanceModsManager` (snapshots keyed by instance ID, per-instance `AsyncRwLock` keyed by full folder path, version compatibility cache keyed by `"{VersionId}|{ModLoader}|{ProjectId}"`).

Key files:
- `GenericLauncher.Shared/Database/Model/MinecraftInstance.cs` — enum `MinecraftInstanceState` (Unknown, Installing, Ready), `StateFromString`/`StateToString` conversions, DB schema
- `GenericLauncher.Shared/Database/LauncherDatabase.cs` — raw SQLite operations with `AsyncRwLock`, has `SetMinecraftInstanceAsReadyAsync` (line 136) and `RemoveAccountAsync` (line 104) as patterns
- `GenericLauncher.Shared/Database/LauncherRepository.cs` — thin wrapper ensuring DB init
- `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs` — instance lifecycle orchestrator, holds `Instances` (ImmutableList), `LaunchedInstances`, `CurrentInstallProgress`, `_lock` (SemaphoreSlim), fires `InstancesChanged`
- `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs` — per-instance mod state, `_snapshots` (keyed by instance ID), `_instanceStateLocks` (keyed by full folder path via `GetInstanceFolder(instance.Folder)`), `_latestCompatibleVersionCache`, `InvalidateSnapshot(instanceId)` (line 1128)
- `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs` — transient screen, IDisposable, subscribes to launcher events, has `OnInstancesChanged` (line 147)
- `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml` — Avalonia XAML view
- `GenericLauncher.Shared/Screens/MainWindow/MainWindowViewModel.cs` — navigation coordinator, `GoToInstanceDetails` (line 237)
- `GenericLauncher.Shared/Controls/MinecraftInstanceListItem.axaml` — home list item template, displays `Instance.State` as text, Play button uses `IsReadyConverter`
- `GenericLauncher.Shared/Converters/IsReadyConverter.cs` — returns `true` only for `Ready` state (new states automatically hide Play button)

## Plan of work

### Stage 1: MinecraftInstanceState enum and conversions

**`GenericLauncher.Shared/Database/Model/MinecraftInstance.cs`**

Add two new values to the enum:

```csharp
public enum MinecraftInstanceState
{
    Unknown,
    Installing,
    Ready,
    Deleting,
    DeleteFailed,
}
```

Update `StateFromString` (line 198):
```csharp
public static MinecraftInstanceState StateFromString(string raw) => raw switch
{
    "INSTALLING" => MinecraftInstanceState.Installing,
    "READY" => MinecraftInstanceState.Ready,
    "DELETING" => MinecraftInstanceState.Deleting,
    "DELETE_FAILED" => MinecraftInstanceState.DeleteFailed,
    _ => MinecraftInstanceState.Unknown,
};
```

Update `StateToString` (line 206):
```csharp
public static string StateToString(MinecraftInstanceState state) => state switch
{
    MinecraftInstanceState.Installing => "INSTALLING",
    MinecraftInstanceState.Ready => "READY",
    MinecraftInstanceState.Deleting => "DELETING",
    MinecraftInstanceState.DeleteFailed => "DELETE_FAILED",
    _ => "UNKNOWN",
};
```

No DB migration needed — the `State` column is `TEXT NOT NULL` with no CHECK constraint.

### Stage 2: Database layer

**`LauncherDatabase.cs`** — Add two methods:

1. `SetMinecraftInstanceStateAsync` — generalizes `SetMinecraftInstanceAsReadyAsync` for any state transition:

```csharp
public Task SetMinecraftInstanceStateAsync(string instanceId, MinecraftInstanceState state)
{
    return _rwLock.ExecuteWriteAsync(() => _conn.ExecuteAsync(
        $"UPDATE {MinecraftInstance.Table} SET State = @State WHERE Id = @Id",
        (instanceId, MinecraftInstance.StateToString(state)),
        static (cmd, args) =>
        {
            cmd.Parameters.AddWithValue("@Id", args.instanceId);
            cmd.Parameters.AddWithValue("@State", args.Item2);
        }));
}
```

2. `DeleteMinecraftInstanceAsync` — following `RemoveAccountAsync` (line 104):

```csharp
public async Task<bool> DeleteMinecraftInstanceAsync(string instanceId)
{
    var count = await _rwLock.ExecuteWriteAsync(() =>
        _conn.ExecuteScalarAsync<long>(
            $"DELETE FROM {MinecraftInstance.Table} WHERE Id = @Id;",
            bind: cmd => { cmd.Parameters.AddWithValue("@Id", instanceId); }));
    return count == 1;
}
```

**`LauncherRepository.cs`** — Add two corresponding methods:

```csharp
public async Task SetMinecraftInstanceStateAsync(string instanceId, MinecraftInstanceState state)
{
    await _initTask;
    await _db.SetMinecraftInstanceStateAsync(instanceId, state);
}

public async Task<bool> RemoveMinecraftInstanceAsync(string instanceId)
{
    await _initTask;
    return await _db.DeleteMinecraftInstanceAsync(instanceId);
}
```

### Stage 3: InstanceModsManager cache eviction

**`InstanceModsManager.cs`** — Add public method:

```csharp
public void EvictInstanceCaches(MinecraftInstance instance)
{
    InvalidateSnapshot(instance.Id);

    var folderPath = GetInstanceFolder(instance.Folder);
    _instanceStateLocks.TryRemove(folderPath, out _);

    lock (_latestCompatibleVersionCacheLock)
    {
        _latestCompatibleVersionCache.Clear();
    }
}
```

### Stage 4: MinecraftLauncher orchestration

**`MinecraftLauncher.cs`** — Add `DeleteInstanceAsync(string instanceId)`:

```csharp
public async Task DeleteInstanceAsync(string instanceId)
{
    MinecraftInstance instance;

    // Phase 1: Validate and set Deleting state (under lock)
    await _lock.WaitAsync();
    try
    {
        instance = Instances.Find(i => i.Id == instanceId)
            ?? throw new InvalidOperationException($"Instance '{instanceId}' not found.");

        if (LaunchedInstances.ContainsKey(instanceId))
            throw new InvalidOperationException(
                $"Cannot delete instance '{instanceId}' because it is currently running.");

        if (instance.State == MinecraftInstanceState.Installing
            || CurrentInstallProgress.ContainsKey(instanceId))
            throw new InvalidOperationException(
                $"Cannot delete instance '{instanceId}' because it is currently installing.");

        // Soft-delete: mark as Deleting in DB
        await _repository.SetMinecraftInstanceStateAsync(instanceId, MinecraftInstanceState.Deleting);
    }
    finally
    {
        _lock.Release();
    }

    // Refresh so UI shows "Deleting" status
    await RefreshInstancesAsync();

    // Phase 2: Delete disk folder
    var instanceFolder = Path.Combine(_instancesFolder, instance.Folder);
    try
    {
        if (Directory.Exists(instanceFolder))
            Directory.Delete(instanceFolder, recursive: true);
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Failed to delete instance folder '{Folder}'", instanceFolder);

        // Transition to DeleteFailed
        await _repository.SetMinecraftInstanceStateAsync(instanceId, MinecraftInstanceState.DeleteFailed);
        await RefreshInstancesAsync();
        throw new InvalidOperationException(
            $"Failed to delete instance folder. The instance is marked for retry.", ex);
    }

    // Phase 3: Disk is gone — now delete DB record and clean caches
    await _repository.RemoveMinecraftInstanceAsync(instanceId);
    _instanceModsManager.EvictInstanceCaches(instance);
    CurrentInstallProgress.TryRemove(instanceId, out _);

    await RefreshInstancesAsync();
}
```

The flow:
1. Under lock: validate guards, set state to `Deleting` in DB
2. Refresh → UI shows "Deleting..."
3. Delete disk folder (outside lock, can be slow)
4. On disk failure: set state to `DeleteFailed`, refresh, throw
5. On disk success: delete DB record, evict caches, refresh → instance gone from UI

### Stage 5: InstanceDetailsViewModel

**`InstanceDetailsViewModel.cs`** — Changes:

1. Add `Action? _onDeleted` field and constructor parameter (insert before `logger`):

```csharp
private readonly Action? _onDeleted;
```

Add to constructor parameters:
```csharp
Action? onDeleted = null,
```

And in constructor body:
```csharp
_onDeleted = onDeleted;
```

2. Add new observable properties and computed properties:

```csharp
[ObservableProperty] private bool _isDeleteConfirmationVisible;
[ObservableProperty] private string _deleteErrorMessage = "";

public bool IsDeleting => Instance.State == MinecraftInstanceState.Deleting;
public bool IsDeleteFailed => Instance.State == MinecraftInstanceState.DeleteFailed;
public bool CanDelete => Instance.State == MinecraftInstanceState.Ready
    && RunningState == MinecraftLauncher.RunningState.Stopped;
```

3. Add commands:

```csharp
[RelayCommand]
private void ShowDeleteConfirmation() => IsDeleteConfirmationVisible = true;

[RelayCommand]
private void CancelDelete()
{
    IsDeleteConfirmationVisible = false;
    DeleteErrorMessage = "";
}

[RelayCommand]
private async Task ConfirmDeleteAsync()
{
    if (_minecraftLauncher is null) return;
    try
    {
        DeleteErrorMessage = "";
        await _minecraftLauncher.DeleteInstanceAsync(Instance.Id);
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Failed to delete instance {InstanceId}", Instance.Id);
        DeleteErrorMessage = "Failed to delete instance files. You can retry.";
        IsDeleteConfirmationVisible = false;
    }
}

[RelayCommand]
private async Task RetryDeleteAsync()
{
    if (_minecraftLauncher is null) return;
    try
    {
        DeleteErrorMessage = "";
        await _minecraftLauncher.DeleteInstanceAsync(Instance.Id);
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Failed to retry delete for instance {InstanceId}", Instance.Id);
        DeleteErrorMessage = "Failed to delete instance files. You can retry.";
    }
}
```

4. Update `OnInstancesChanged` to detect deletion (instance gone from list):

```csharp
private void OnInstancesChanged(object? sender, EventArgs e)
{
    if (_minecraftLauncher is null) return;
    Dispatcher.UIThread.Post(() =>
    {
        var updatedInstance = _minecraftLauncher.Instances.Find(i => i.Id == Instance.Id);
        if (updatedInstance is null)
        {
            // Instance was fully deleted — navigate back
            _onDeleted?.Invoke();
            return;
        }
        if (updatedInstance != Instance)
        {
            Instance = updatedInstance;
            OnPropertyChanged(nameof(IsInstalling));
            OnPropertyChanged(nameof(CanManageMods));
            OnPropertyChanged(nameof(IsDeleting));
            OnPropertyChanged(nameof(IsDeleteFailed));
            OnPropertyChanged(nameof(CanDelete));
            ClickPlayCommand.NotifyCanExecuteChanged();
        }
    });
}
```

5. Update `OnInstanceChanged` partial method to notify computed properties:

```csharp
partial void OnInstanceChanged(MinecraftInstance value)
{
    OnPropertyChanged(nameof(Title));
    OnPropertyChanged(nameof(CanManageMods));
    OnPropertyChanged(nameof(IsDeleting));
    OnPropertyChanged(nameof(IsDeleteFailed));
    OnPropertyChanged(nameof(CanDelete));
}
```

6. Notify `CanDelete` when `RunningState` changes — add `[NotifyPropertyChangedFor(nameof(CanDelete))]` to `_runningState`:

```csharp
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(ClickPlayCommand))]
[NotifyPropertyChangedFor(nameof(CanDelete))]
private MinecraftLauncher.RunningState _runningState = MinecraftLauncher.RunningState.Stopped;
```

### Stage 6: MainWindowViewModel

**`MainWindowViewModel.cs`** — Update `GoToInstanceDetails` (line 237):

```csharp
private void GoToInstanceDetails(MinecraftInstance instance)
{
    var vm = new InstanceDetailsViewModel(
        instance,
        _auth,
        _minecraftLauncher,
        _instanceModsManager,
        _modrinthApiClient,
        GoToModrinthProjectDetails,
        onDeleted: () => Navigation.Pop(),
        App.LoggerFactory?.CreateLogger(nameof(InstanceDetailsViewModel)));
    Navigation.Push(vm);
}
```

### Stage 7: InstanceDetailsView.axaml

Add to the Content tab, below the existing play/instance info section:

```xml
<!-- Deleting status -->
<TextBlock Text="Deleting instance..."
           IsVisible="{Binding IsDeleting}"
           Margin="0,16,0,0" />

<!-- Delete failed + retry -->
<StackPanel IsVisible="{Binding IsDeleteFailed}"
            Spacing="8" Margin="0,16,0,0">
    <TextBlock Text="{Binding DeleteErrorMessage}" />
    <Button Content="Retry Delete"
            Command="{Binding RetryDeleteCommand}" />
</StackPanel>

<!-- Delete section (only visible when Ready and not running) -->
<StackPanel Spacing="8" Margin="0,32,0,0"
            IsVisible="{Binding CanDelete}">
    <Button Content="Delete Instance"
            Command="{Binding ShowDeleteConfirmationCommand}"
            IsVisible="{Binding !IsDeleteConfirmationVisible}" />
    <StackPanel IsVisible="{Binding IsDeleteConfirmationVisible}"
                Orientation="Horizontal" Spacing="8">
        <TextBlock Text="Are you sure? This will permanently delete this instance and all its files."
                   VerticalAlignment="Center" />
        <Button Content="Yes, Delete"
                Command="{Binding ConfirmDeleteCommand}" />
        <Button Content="Cancel"
                Command="{Binding CancelDeleteCommand}" />
    </StackPanel>
</StackPanel>
```

### Stage 8: Home screen verification

The home screen (`MinecraftInstanceListItem.axaml`) already displays `Instance.State` as text (line 24) and the Play button uses `IsReadyConverter` which returns `false` for non-Ready states. This means:
- `Deleting` instances show "Deleting" text, Play button hidden — correct.
- `DeleteFailed` instances show "DeleteFailed" text, Play button hidden — correct.

No changes needed to the home screen, converters, or list item template. The user can click a `DeleteFailed` instance to open details and use the retry button.

### Stage 9: Validation

1. `dotnet build LavaLauncher.sln` — must succeed.
2. `dotnet test LavaLauncher.sln` — must not regress.
3. Run the app: `dotnet run --project LavaLauncher.Desktop/LavaLauncher.Desktop.csproj`
4. Manual tests:
   - Open instance details → "Delete Instance" button visible → click it → confirmation panel appears → click "Cancel" → panel disappears
   - Click "Delete Instance" → "Yes, Delete" → instance shows "Deleting..." → instance disappears → navigated back to home → instance gone from list
   - Verify instance folder deleted from disk
   - Running instance: delete button should not be visible
   - Installing instance: delete button should not be visible

## Concrete steps

All commands run from `/Users/martinflorek/Documents/lavaray/LavaLauncher/`.

After each stage, verify the build:
```bash
dotnet build LavaLauncher.sln
```

After all stages:
```bash
dotnet test LavaLauncher.sln
```

## Validation and acceptance

Quality criteria:
- Build: `dotnet build LavaLauncher.sln` succeeds with 0 errors.
- Tests: `dotnet test LavaLauncher.sln` does not regress.
- Behavior: Soft-delete flow works end-to-end. `Deleting` state visible during folder removal. `DeleteFailed` state persists with retry. Successful deletion removes instance from DB and UI.

## Idempotence and recovery

All changes are additive (new enum values, new methods, new properties, new XAML elements). Small modifications to existing methods (`OnInstancesChanged`, `GoToInstanceDetails`, `OnInstanceChanged`). If something goes wrong, `git checkout` reverts cleanly.

## Interfaces and dependencies

No new dependencies. All new methods extend existing classes:

- `MinecraftInstanceState.Deleting`, `MinecraftInstanceState.DeleteFailed` — new enum values
- `LauncherDatabase.SetMinecraftInstanceStateAsync(string, MinecraftInstanceState) → Task`
- `LauncherDatabase.DeleteMinecraftInstanceAsync(string) → Task<bool>`
- `LauncherRepository.SetMinecraftInstanceStateAsync(string, MinecraftInstanceState) → Task`
- `LauncherRepository.RemoveMinecraftInstanceAsync(string) → Task<bool>`
- `InstanceModsManager.EvictInstanceCaches(MinecraftInstance) → void`
- `MinecraftLauncher.DeleteInstanceAsync(string) → Task`
- `InstanceDetailsViewModel`: new `Action? _onDeleted` param, `IsDeleteConfirmationVisible`, `DeleteErrorMessage`, `IsDeleting`, `IsDeleteFailed`, `CanDelete`, `ShowDeleteConfirmationCommand`, `CancelDeleteCommand`, `ConfirmDeleteCommand`, `RetryDeleteCommand`
