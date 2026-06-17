# Fix Per-Mod Refresh Status And Simplify Compatible-Version Cache

This ExecPlan (execution plan) is a living document. The sections `Constraints`, `Tolerances`, `Risks`, `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds.

Status: COMPLETE

## Purpose / big picture

The launcher currently knows more about Modrinth refresh failures than it shows to the user. `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs` can tell whether a lookup failed and whether stale cached data was reused, but that detail is collapsed into one aggregate boolean before it reaches the screens. The result is user-visible dishonesty: a mod row with unavailable update status is rendered the same as a real “no update”, and project details can show stale cached update data as if it were fresh.

After this change, every requested project id will carry its own refresh status all the way from `InstanceModsManager` to the two UI surfaces that consume it. The instance-mods list will show a quiet per-row unavailable or stale state instead of pretending the row is up to date. The project-details screen will show a quiet stale indicator when cached data is being shown after a failed refresh. The manager-local cache will also be simplified so it stays correct during failures without the current hand-rolled linked-list LRU bookkeeping. The cache must continue to store the full compatible-version list per project key, not only the latest version, so later UI can offer specific-version installation without reworking the cache again.

A user can observe success by opening instance mods during a simulated Modrinth outage and seeing only the affected rows marked as unavailable or stale, while unaffected rows still show normal update state. A user can also open project details for an installed project, trigger a failed refresh after one successful lookup, and see wording that explicitly says cached data is being shown. Repeated navigation during the outage should reuse the cached stale result instead of hammering Modrinth on every screen refresh.

## Constraints

- Keep manual composition intact. Do not introduce `IServiceCollection`, service providers, runtime scanning, or any new architectural layer outside the existing `App` / view-model construction flow.
- Preserve trimming and AOT safety. Do not add reflection-heavy runtime features, dynamic serialization, or external packages.
- Keep `ModrinthApiClient.GetProjectVersionsAsync(...)` unchanged at the HTTP-contract level. The change is in how its `null` result is interpreted by callers, not in the API client signature or JSON model.
- Keep the cache process-local and in-memory only. Do not persist compatibility status to SQLite or to files.
- Preserve the existing install/update selection rule: the best compatible version remains the first version after sorting by version type rank and publish date.
- Keep caching the full compatible-version list for each `(Minecraft version, mod loader, project id)` key. Do not collapse the cache itself down to only the latest version.
- Do not redesign the UI. The new user-visible states must be small, quiet extensions of the current list row and project state text.
- Do not modify unrelated mod-management behavior such as metadata schema, dependency resolution rules, instance deletion flow, or auth.
- Use records for immutable value objects and DTO-like shapes that model compatibility results or cache entries. Do not force records onto mutable workflow/state-holder types where mutation is the natural fit.

If satisfying the objective requires violating a constraint, stop, document the conflict in `Decision Log`, and ask for direction.

## Tolerances (exception triggers)

- Scope: if implementation requires touching more than 9 files or more than 450 net lines of code, stop and ask whether to widen scope.
- Interface: if any public constructor, XAML binding contract, or externally consumed public method must change in a way that affects callers outside the touched files, stop and ask before proceeding.
- Dependencies: if a new package or library seems necessary, stop and ask.
- Semantics: if the existing screens cannot express per-project `Fresh`, `Stale`, and `Unavailable` states without a broader UI redesign, stop and present options with trade-offs.
- Cache policy: if preventing repeated failed refreshes requires a second cache layer, background timer, or asynchronous request registry that is materially more complex than a small local dictionary policy, stop and ask.
- Testing: if the new targeted tests still fail after 3 focused iterations, stop and report the failing cases with hypotheses.
- Time: if any single milestone takes more than 90 minutes of active work, pause, document why, and ask whether to continue.

## Risks

- Risk: the public result shape may still be too lossy if it only exposes “latest version” rather than the per-project refresh state.
  Severity: high
  Likelihood: high
  Mitigation: replace the aggregate-boolean contract with a per-project record that always exists for each requested project id and carries both the latest compatible version (if any) and a status enum.

- Risk: simplifying the cache too aggressively could reintroduce unbounded growth or repeated network fetches during outages.
  Severity: high
  Likelihood: medium
  Mitigation: keep a bounded dictionary with a small immutable cache-entry record, and prune it under lock by timestamp whenever an entry is inserted or updated.

- Risk: caching stale results incorrectly could hide refresh failures or keep retrying too aggressively.
  Severity: medium
  Likelihood: high
  Mitigation: model “last successful fetch time” separately from “last refresh attempt time”, and add a short retry delay after a failed refresh so repeated navigation reuses stale status instead of spamming Modrinth.

- Risk: per-row UI state may drift between `InstanceDetailsViewModel` and `ModrinthProjectDetailsViewModel`.
  Severity: medium
  Likelihood: medium
  Mitigation: define one shared vocabulary in `GenericLauncher.Shared/InstanceMods/InstanceModsSnapshot.cs` for refresh status, then adapt both screens from that shared model.

- Risk: test fixtures currently prove aggregate failure behavior but not per-project propagation.
  Severity: medium
  Likelihood: high
  Mitigation: add red tests first for mixed-success/mixed-failure batches so the implementation must preserve per-project state end to end.

## Progress

- [x] (2026-03-31) Review the last commit, the current manager/view-model code, and the existing cache execplan.
- [x] (2026-03-31) Identify concrete defects to fix in the follow-up plan: per-mod status loss, stale-data UI omission, and cache retry spam after one failed refresh.
- [x] (2026-03-31) Draft this ExecPlan with explicit file targets, record-based result shapes, and validation steps.
- [x] (2026-03-31) Add targeted manager and view-model tests for per-project refresh-state propagation, stale cached messaging, and retry-delay reuse.
- [x] (2026-03-31) Refactor the manager result model to return per-project compatibility status entries and simplify the compatible-version cache to immutable record values plus prune-on-write.
- [x] (2026-03-31) Update instance details and project details to render quiet `Fresh`, `Stale`, and `Unavailable` states, including per-row text in the mods list.
- [x] (2026-03-31) Validate the change with targeted Modrinth tests, a full solution test run, and a serialized solution build.

## Surprises & Discoveries

- Observation: the full test suite currently passes in this workspace, so the most important problem is semantic regression rather than a presently reproducible red build.
  Evidence: `dotnet test LavaLauncher.sln --no-restore` passed 195/195 during review.
  Impact: the implementation plan must start with new failing tests that encode the missing semantics rather than assuming an existing broken test will guide the work.

- Observation: the current follow-up problem is not “classes vs records” in the abstract. The real issue is that the current immutable public result type is too small and loses per-project state before the UI sees it.
  Evidence: `LatestCompatibleVersionsResult` currently contains only `Versions` and one `HasRefreshFailure` boolean.
  Impact: the fix should prioritize a richer record-based result model over mechanical record conversion of every private type.

- Observation: the current cache already stores the full compatible-version array, while only the screen-facing result is reduced to “latest version”.
  Evidence: `CompatibleVersionCacheEntry` stores `ImmutableArray<CompatibleVersionInfo> Versions`, and `GetLatestCompatibleVersionsAsync(...)` picks `result.Versions[0]` when building its public response.
  Impact: the follow-up should preserve full-list caching and only widen the public/shared result model enough to expose richer status, with an optional future path to expose all cached versions to a specific-version picker.

- Observation: the hand-maintained linked-list LRU is now the main source of incidental complexity in the cache path.
  Evidence: `CompatibleVersionCacheEntry` stores `OrderNode`, and the manager contains `PromoteCacheEntry`, `InvalidateCompatibleVersionsForProjects`, and manual order maintenance under lock.
  Impact: a simpler bounded prune-by-timestamp policy is a better fit for a 512-entry cache and will make immutable record entries practical.

- Observation: Avalonia build output in this repo is not safe to verify in parallel with `dotnet test`.
  Evidence: running `dotnet build LavaLauncher.sln` in parallel with `dotnet test LavaLauncher.sln` produced `AVLN9999` because `GenericLauncher.Shared.pdb` was locked by the concurrent test build.
  Impact: validation should run `dotnet test` and `dotnet build` serially when both are needed.

## Decision log

- Decision: this follow-up will replace the aggregate result contract with a per-project record-based contract.
  Rationale: the aggregate boolean is the root cause of the per-mod status loss. Fixing only the screens would preserve an underpowered API and force the UI to guess.
  Date/Author: 2026-03-31 / Codex

- Decision: the cache simplification target is “small bounded dictionary with immutable record values and prune-on-write”, not a perfect linked-list LRU.
  Rationale: 512 entries is small enough that simple timestamp-based pruning is easy to reason about and avoids mutable node bookkeeping.
  Date/Author: 2026-03-31 / Codex

- Decision: the cache will continue storing the full compatible-version list, while the immediate UI-facing follow-up may still expose only the latest version plus refresh status.
  Rationale: future specific-version install UI needs the cached list. The current bug fix does not require exposing all versions to the existing screens yet, but it must not narrow the cache and create another migration later.
  Date/Author: 2026-03-31 / Codex

- Decision: failed refreshes will have a short retry delay even when stale cached data exists.
  Rationale: without a retry delay, one outage causes every navigation to refetch and fail again. The cache must remain honest without becoming noisy or wasteful.
  Date/Author: 2026-03-31 / Codex

- Decision: the plan will keep unavailable state per project rather than only as a screen-wide banner.
  Rationale: the user explicitly asked for failure propagation per mod, and the review showed that a screen-wide flag is insufficient.
  Date/Author: 2026-03-31 / Codex

- Decision: failed first-lookups are cached briefly as explicit `Unavailable` entries instead of being left uncached.
  Rationale: short-lived unavailable entries preserve the “every requested project id has a status entry” invariant and prevent repeated identical failing requests on every navigation during an outage.
  Date/Author: 2026-03-31 / Codex

- Decision: the instance-mods list uses one nullable `UpdateStatusText` field rather than a broader row-state UI redesign.
  Rationale: the user-visible requirement was to surface quiet stale/unavailable text per row while preserving the current layout and update-button behavior.
  Date/Author: 2026-03-31 / Codex

## Outcomes & retrospective

This plan was implemented successfully. `InstanceModsManager` now returns one `ProjectCompatibilityStatus` per requested project id, including `Fresh`, `Stale`, and `Unavailable` states. The compatible-version cache is now a single bounded dictionary of immutable record values with prune-on-write and a 60-second retry delay for stale or unavailable failures. The linked-list LRU bookkeeping is gone, while the cache still retains the full sorted `ImmutableArray<CompatibleVersionInfo>` per key.

On the UI side, the instance-mods list now renders quiet per-row text for updates, stale cached data, and unavailable status via `UpdateStatusText`, and project details now tell the user when cached compatibility data is being shown after a failed refresh. The existing update-button behavior remains intact: only real update availability drives `HasUpdate` and `CanUpdateAll`.

Validation evidence:

- `dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Modrinth"` passed with 35/35 tests.
- `dotnet test LavaLauncher.sln` passed with 201/201 tests.
- `dotnet build LavaLauncher.sln` passed after rerunning it serially once the full test run had released the shared Avalonia build outputs.

The main lesson from the reviewed commit held up during implementation: the earlier cache-local refactor was directionally right, but the result model had been flattened too early. Restoring per-project status at the shared contract boundary solved the UI honesty problem and made the simpler cache design easier to reason about.

## Context and orientation

The relevant behavior is split across one manager, one shared result file, and two screens.

`GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs` owns Modrinth compatibility lookup, cache policy, install/update resolution, and snapshot publication. The current public method `GetLatestCompatibleVersionsAsync(...)` builds one `LatestCompatibleVersionsResult`, but that result only exposes a dictionary of latest versions and one aggregate failure boolean. Internally, `GetCompatibleVersionsInternalAsync(...)` already distinguishes fresh success, stale success, and unavailable states, but it collapses those outcomes before returning to callers.

`GenericLauncher.Shared/InstanceMods/InstanceModsSnapshot.cs` contains immutable data shapes used by the screens. It is the natural place for small record-based compatibility status DTOs because both `InstanceDetailsViewModel` and `ModrinthProjectDetailsViewModel` depend on the shared instance-mods namespace already.

`GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs` loads a batch of direct-installed project ids and applies compatibility results back onto `InstanceModListItem`. Today it can only set `HasUpdate` and `LatestVersionNumber`, so a failed lookup with no cached data is rendered the same as “no update”. The accompanying XAML in `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml` only knows how to show “Update available: X”.

`GenericLauncher.Shared/Screens/ModrinthProjectDetails/ModrinthProjectDetailsViewModel.cs` asks for exactly one project id and renders `TargetStateText`. Today it handles the unavailable case only when `TargetLatestCompatibleVersion` is null. It does not show any stale indicator when cached data is returned after a failed refresh.

`GenericLauncher.Tests/Modrinth/InstanceModsManagerCompatibleVersionsCacheTest.cs`, `GenericLauncher.Tests/Modrinth/InstanceDetailsRefreshGateTest.cs`, and `GenericLauncher.Tests/Modrinth/ModrinthProjectDetailsRefreshGateTest.cs` are the best starting points for tests. They already model manager cache semantics, UI refresh races, and Modrinth HTTP fixtures. The full suite is currently green, so this plan must add red tests that encode the missing semantics before any production changes.

For this plan, “per-mod refresh status” means that each requested project id has its own compatibility state. The manager cache should continue storing the full sorted compatible-version list for each project key. The screen-facing result for this plan only needs the latest version plus refresh state, but it must be built from the full cached list so a later specific-version picker can reuse the same cache.

The minimum useful state vocabulary is:

1. `Fresh`: the latest data came from a successful fetch or a TTL-valid cached success and can be treated as current.
2. `Stale`: a fresh fetch failed, but older successful data is still available and is being shown with a quiet warning.
3. `Unavailable`: the refresh failed and there is no successful data to show, so the UI must not claim “no update” or “up to date”.

## Plan of work

Stage A is test-first design. In `GenericLauncher.Tests/Modrinth/InstanceModsManagerCompatibleVersionsCacheTest.cs`, add failing manager tests that request more than one project id at once and prove the returned shape retains per-project status instead of a single boolean. One test should model mixed results, for example `alpha` succeeds while `bravo` becomes unavailable, and assert that the result contains both project ids with distinct statuses. A second test should model stale cached data after a failed refresh and assert that the stale state survives repeated non-force reads during a short retry window. In `GenericLauncher.Tests/Modrinth/ModrinthProjectDetailsRefreshGateTest.cs` or a new dedicated view-model test file, add a red test for stale project details text. In `GenericLauncher.Tests/Modrinth/InstanceDetailsRefreshGateTest.cs` or a new dedicated instance-details test file, add a red test that one row can be unavailable while another still shows an update.

Do not proceed past Stage A until those new tests fail for the current codebase for the expected reason: the manager returns only aggregate failure, the instance list cannot express per-row unavailable/stale state, and project details do not label stale cached data.

Stage B is the data-model refactor. Replace `LatestCompatibleVersionsResult` in `GenericLauncher.Shared/InstanceMods/InstanceModsSnapshot.cs` with a richer record-based shape. The preferred design is a small enum plus one or two immutable records, for example:

```csharp
public enum CompatibilityRefreshState
{
    Fresh,
    Stale,
    Unavailable,
}

public sealed record ProjectCompatibilityStatus(
    string ProjectId,
    LatestCompatibleVersionInfo? LatestVersion,
    CompatibilityRefreshState RefreshState
);

public sealed record LatestCompatibleVersionsResult(
    ImmutableDictionary<string, ProjectCompatibilityStatus> Projects
);
```

The important requirement is not the exact names. The important requirement is that every requested project id has one entry, and each entry preserves both the latest version (or lack of one) and the refresh state. Keep these as records because they are immutable value objects shared between the manager and view models.

In `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs`, update `GetLatestCompatibleVersionsAsync(...)` to populate the new per-project records instead of aggregating into `hasRefreshFailure`. If a lookup succeeds with zero compatible versions, still return a project entry with `RefreshState.Fresh` and `LatestVersion = null`. If a lookup fails with stale cached data, return the prior latest version and `RefreshState.Stale`. If it fails with no cached success, return `LatestVersion = null` and `RefreshState.Unavailable`.

Do not narrow the cache or the internal fetch result to “latest only”. The manager should continue caching and carrying the full sorted `ImmutableArray<CompatibleVersionInfo>`, and the screen-facing latest-version record should be derived from element zero of that list. If implementation stays simple, add a second internal/shared record that exposes the full list plus refresh state for future specific-version UI; if that would widen scope too much for this bug-fix pass, keep the full-list type private to `InstanceModsManager` and document the future extraction point.

Stage C is cache simplification and correctness. Replace the current mutable `CompatibleVersionCacheEntry` class, linked-list order tracking, and `PromoteCacheEntry(...)` helper with one immutable record and a small prune helper. A concrete target is:

```csharp
private sealed record CompatibleVersionsCacheEntry(
    ImmutableArray<CompatibleVersionInfo> Versions,
    DateTime LastSuccessfulFetchAtUtc,
    DateTime LastAccessedAtUtc,
    DateTime LastRefreshAttemptAtUtc,
    CompatibilityRefreshState RefreshState
);
```

The dictionary remains the only mutable structure. Under the existing lock, replace an entry with a new record instead of mutating fields in place. Remove `OrderNode`, `_compatibleVersionsCacheOrder`, and `PromoteCacheEntry(...)`.

Use a bounded prune-on-write strategy because the cache is tiny. Whenever a new entry is inserted or an existing one is replaced, call `PruneCompatibleVersionsCache(nowUtc)`. That helper should first drop entries that are both expired and not currently useful, then, if the count is still above the max, remove the least recently accessed entries by scanning the dictionary for the smallest `LastAccessedAtUtc`. A linear scan is acceptable here because the maximum size is 512 and simplicity matters more than perfect asymptotics.

Fix the retry-spam bug by separating “successful data freshness” from “refresh retry eligibility”. If an entry is stale because a refresh failed, normal non-force reads should keep returning the stale entry until a small retry delay elapses. A 60-second retry delay is a reasonable default unless implementation discovers a better existing convention in the codebase. This means a stale entry can still be served on navigation without immediate refetches, while explicit force refresh still bypasses the delay.

If there is no cached success and a refresh fails, decide whether to store a short-lived unavailable entry or leave unavailable uncached. The preferred option is to store a short-lived unavailable entry with no versions and `RefreshState.Unavailable`, because it prevents repeated identical failing requests on every navigation and keeps the “every requested project id has an entry” invariant simpler. If this turns out to complicate the cache too much, document the trade-off in `Decision Log` and keep unavailable uncached, but only after confirming the UI can still preserve per-project status reliably.

Stage D is screen integration. In `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs`, stop deriving row state from dictionary presence. Instead, look up the per-project `ProjectCompatibilityStatus` and map it onto the list row explicitly. Extend `InstanceModListItem` in `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs` only as far as needed to render the new quiet state. The smallest useful extension is one new nullable text field and possibly one enum-like visual state field. For example, `UpdateStatusText` can hold `Update available: 1.2.0`, `Status unavailable.`, or `Refresh failed; showing cached 1.2.0.` while `HasUpdate` continues to drive the Update button. This keeps the XAML change small and per-row.

In `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml`, bind to the new row text instead of only showing `LatestVersionNumber` when `HasUpdate` is true. The row should remain quiet: no modal warning, no large banner required. If a row is stale, it should still show the cached latest version and a short qualifier. If a row is unavailable, it should show a neutral line such as `Status unavailable.` and must not imply that the installed version is current.

In `GenericLauncher.Shared/Screens/ModrinthProjectDetails/ModrinthProjectDetailsViewModel.cs`, replace the current `HasCompatibilityRefreshFailure` boolean logic with direct inspection of the per-project status record. When the project status is `Stale`, `TargetStateText` must explicitly say cached data is being shown after a failed refresh. When it is `Unavailable`, it should keep the existing quiet unavailable wording. Remove any now-redundant aggregate boolean properties if they no longer serve a real UI purpose.

Stage E is cleanup and validation. Remove or rename helpers in `InstanceModsManager.cs` that only existed to support the older aggregate result. Keep record types for immutable status models and cache entries. Keep classes only where mutation or framework shape requires them, such as `EventArgs` or workflow holders like `ResolutionState`.

## Concrete steps

Run all commands from the repository root:

```plaintext
/Users/martinflorek/Documents/lavaray/LavaLauncher
```

1. Establish the red tests first.

```plaintext
dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Modrinth.InstanceModsManagerCompatibleVersionsCacheTest|FullyQualifiedName~GenericLauncher.Tests.Modrinth.InstanceDetails|FullyQualifiedName~GenericLauncher.Tests.Modrinth.ModrinthProjectDetails"
```

Expected pre-change outcome: at least the new per-project propagation tests fail, and they fail because the current result model or UI state cannot represent the requested semantics.

2. After the data-model and cache changes, rerun the targeted tests.

```plaintext
dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Modrinth"
```

Expected post-change outcome: the targeted Modrinth tests pass, including new manager tests for mixed per-project status, stale reuse after failure, unavailable status propagation, and the two UI-facing tests.

3. If `InstanceDetailsView.axaml` or any other XAML changes, build the solution.

```plaintext
dotnet build LavaLauncher.sln
```

Expected outcome: build succeeds with no new XAML binding errors.

4. Run the full suite before considering the work complete.

```plaintext
dotnet test LavaLauncher.sln
```

Expected outcome: full suite passes. If the environment fails for sandbox reasons, record the exact failure text in this document and stop rather than silently skipping validation.

## Validation and acceptance

The change is complete only when all of the following are true:

- Manager API behavior:
  - A batched call for multiple project ids returns one per-project status entry for each requested project id.
  - A successful empty result is distinct from unavailable failure.
  - A failed refresh after prior success returns stale cached data and marks that project as stale.
  - Repeated non-force reads during the retry delay reuse stale data instead of triggering a fresh fetch each time.
  - The manager cache still stores the full compatible-version list, and “latest version” is always derived from that list rather than cached separately.

- Instance details behavior:
  - When one project refresh fails with no cache and another succeeds, the failed row shows a neutral unavailable message while the successful row still shows normal update information.
  - A stale row shows cached data with a quiet stale qualifier rather than looking fully fresh.
  - `CanUpdateAll` is driven only by rows with real updates, not by rows that are unavailable.

- Project details behavior:
  - If the project has stale cached compatibility data, `TargetStateText` explicitly says cached data is being shown after refresh failure.
  - If the project has unavailable status, `TargetStateText` remains neutral and does not imply certainty.

- Cache simplification behavior:
  - The linked-list LRU helpers are gone.
  - Immutable record entries are used for cache values where practical.
  - Cache correctness is preserved by targeted tests for hit, force refresh, TTL expiry, stale reuse, unavailable status, and retry-delay behavior.

Quality criteria:

- Tests: `dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Modrinth"` passes, then `dotnet test LavaLauncher.sln` passes.
- Build: `dotnet build LavaLauncher.sln` passes if any XAML changed.
- Behavior: the new red tests fail before the change and pass after it.

## Idempotence and recovery

The implementation steps are re-runnable. Test commands are read-only aside from standard build artifacts. The cache refactor is local to `InstanceModsManager.cs`, so recovery from a partial attempt is straightforward: reset only the touched work if needed, then rerun the targeted tests until the manager API and both screens agree on the new per-project state model.

No database migration or destructive file operation is part of this plan. The only caution is that if the public compatibility result type changes, all call sites must be updated in the same change before rerunning the build, because partial compilation states will fail.

## Artifacts and notes

The main review findings this plan addresses are:

```plaintext
1. InstanceDetailsViewModel collapses missing per-project compatibility info into "no update".
2. ModrinthProjectDetailsViewModel hides the stale-data case when a cached version exists.
3. InstanceModsManager stops reusing cached stale data after one failed refresh and refetches on every navigation.
4. The follow-up must preserve full-list version caching for future specific-version install UI.
```

The concrete production files expected to change are:

- `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs`
- `GenericLauncher.Shared/InstanceMods/InstanceModsSnapshot.cs`
- `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs`
- `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml`
- `GenericLauncher.Shared/Screens/ModrinthProjectDetails/ModrinthProjectDetailsViewModel.cs`

The concrete test files expected to change are:

- `GenericLauncher.Tests/Modrinth/InstanceModsManagerCompatibleVersionsCacheTest.cs`
- `GenericLauncher.Tests/Modrinth/InstanceDetailsRefreshGateTest.cs` or a new nearby instance-details view-model test
- `GenericLauncher.Tests/Modrinth/ModrinthProjectDetailsRefreshGateTest.cs` or a new nearby project-details view-model test

## Interfaces and dependencies

Do not add dependencies.

At the end of Stage B, the shared interface should expose immutable per-project compatibility status. The preferred end state is:

```csharp
public enum CompatibilityRefreshState
{
    Fresh,
    Stale,
    Unavailable,
}

public sealed record ProjectCompatibilityStatus(
    string ProjectId,
    LatestCompatibleVersionInfo? LatestVersion,
    CompatibilityRefreshState RefreshState
);

public sealed record LatestCompatibleVersionsResult(
    ImmutableDictionary<string, ProjectCompatibilityStatus> Projects
);
```

At the end of Stage C, the manager should keep one private cache dictionary plus one private prune helper. The preferred private entry shape is an immutable record, not a mutable class with a linked-list node reference. The manager may keep a mutable dictionary and lock, but individual entry values should be replaced as records under that lock. Those private entries should continue to hold the full `ImmutableArray<CompatibleVersionInfo>` so later specific-version UI can read from the same cache.

At the end of Stage D, `InstanceDetailsViewModel` should derive row state from `ProjectCompatibilityStatus`, not from dictionary presence or one screen-wide failure boolean. `ModrinthProjectDetailsViewModel` should derive `TargetStateText` from the same shared state vocabulary.

## Revision note

Initial draft created on 2026-03-31 in response to review findings against the previous mods-cache implementation. Revised on 2026-03-31 to make explicit that the cache must continue storing the full compatible-version list, not only the latest version, so future specific-version installation UI can build on the same cache. This does not materially change the current bug-fix scope, but it is now a hard requirement for the follow-up implementation.
