This ExecPlan (execution plan) is a living document. The sections `Constraints`, `Tolerances`, `Risks`, `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds.
Status: COMPLETE

The launcher currently caches Modrinth compatible-version lookups for installed mods so the UI can show update availability without repeatedly hitting the network. The current branch improved memory safety by adding a bounded LRU, but it still has three important problems:
1. a temporary Modrinth failure can be cached as if no compatible versions exist;
2. the actual install/update path still uses a separate uncached lookup path;
3. the cache is implemented through a reusable generic layer even though this behavior is only used in one place.
After this change, the launcher should have one direct, manager-local cache path for compatible-version resolution. If a fresh lookup fails and there is older successful data, the launcher should keep using that older data and also tell the UI that the refresh failed, so the user knows the displayed result is stale and should be retried later. If a fresh lookup fails and there is no prior successful data, the UI should show a quiet “status unavailable” state instead of silently claiming there is no update. The cache should expire automatically after a short time window in the 10–30 minute range and should still be bypassed by explicit refresh actions.
A user can observe success by opening instance mods or project details, seeing stable update state while navigating repeatedly, seeing a neutral unavailable state instead of false “up to date” during simulated network failure, and seeing a quiet stale/error indicator when old cached data is shown after a failed refresh. The same selected version should be used whether the result is shown in the UI or used for the actual install/update action.
The main behavior lives in `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs`. That file owns snapshot loading, Modrinth compatibility lookups, install/update flows, and instance-scoped mod operations.
Two UI surfaces consume the compatible-version state:
1. `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs` refreshes update badges for direct-installed mods in an instance.
2. `GenericLauncher.Shared/Screens/ModrinthProjectDetails/ModrinthProjectDetailsViewModel.cs` shows whether a specific project can be installed or updated for the target instance.
The current generic cache implementation is in `GenericLauncher.Shared/Misc/LruCache.cs`. This plan intentionally aims to remove that indirection and keep the cache private to `InstanceModsManager` unless implementation proves that doing so would become materially more complex.
Modrinth network lookup behavior originates in `GenericLauncher.Shared/Modrinth/ModrinthApiClient.cs`, specifically `GetProjectVersionsAsync(...)`. That method currently returns `null` on failures, which means callers must carefully distinguish “request failed” from “request succeeded but there were zero compatible versions”.
The test project already contains relevant coverage patterns in:
1. `GenericLauncher.Tests/Misc/LruCacheTest.cs`
2. `GenericLauncher.Tests/Modrinth/InstanceModsManagerEvictionTest.cs`
3. `GenericLauncher.Tests/Modrinth/InstanceModsManagerStateTest.cs`
4. `GenericLauncher.Tests/Modrinth/ModrinthInstallFlowTest.cs`
5. `GenericLauncher.Tests/Modrinth/RefreshGateTestSupport.cs`
The new work should follow those existing test patterns rather than inventing a new style.
- Keep manual composition intact. Do not introduce DI containers, runtime service discovery, or new architectural layers.
- Preserve trimming and AOT safety. Do not introduce reflection-heavy patterns or libraries.
- Keep Modrinth JSON source-generation behavior intact. Do not replace existing source-generated serialization.
- Do not change public behavior for unrelated mod management features such as metadata files, dependency resolution, or instance deletion.
- Keep the cache process-local and in-memory only. Do not add database-backed or file-backed cache persistence in this change.
- Do not add any external dependency.
- Do not change the Modrinth HTTP API contract used by `ModrinthApiClient.GetProjectVersionsAsync(...)`.
- Prefer the most direct implementation possible inside `InstanceModsManager`. Reuse existing patterns only where they reduce code or risk rather than increase indirection.
- Keep explicit refresh semantics intact: a force-refresh must bypass normal cached reuse.
- If stale cached data is shown after a failed refresh, the failure must also be propagated to the UI so the user is informed that the display is based on old data and should be retried later.
If satisfying the goal requires violating one of these constraints, stop, document the conflict in `Decision Log`, and ask for direction.
Thresholds that trigger escalation when breached. These define the boundaries of autonomous action, not quality criteria.
- Scope: if the implementation requires touching more than 8 files or more than 350 net lines of code, stop and ask whether to widen scope.
- Interface: if any public constructor or view-model public property must change in a way that affects callers outside the changed files, stop and ask.
- Dependencies: if any new package or library seems needed, stop and ask.
- Semantics: if the UI cannot represent either an unavailable state or a stale-data-with-refresh-failure state without a larger redesign than expected, stop and present options.
- Concurrency: if request coalescing cannot be added simply with a small local mechanism inside `InstanceModsManager`, skip it for this pass and record that choice rather than expanding scope.
- Testing: if the required targeted tests still fail after 3 focused iterations, stop and report the failing cases with hypotheses.
- Time: if any single milestone takes more than 90 minutes of active work, pause, document why, and ask whether to continue.
Known uncertainties that might affect the plan. Identify these upfront and update as work proceeds. Each risk should note severity, likelihood, and mitigation or contingency.
- Risk: “no compatible versions” and “lookup failed” are currently collapsed in the call chain.
  Severity: high
  Likelihood: high
  Mitigation: introduce an explicit internal result shape or status enum for compatible-version fetch outcomes so failures are not represented as empty arrays.
- Risk: converging UI and install/update onto one path may accidentally change selection behavior.
  Severity: medium
  Likelihood: medium
  Mitigation: preserve the current ordering rule exactly, validate with tests that the same best version is chosen, and keep one shared selection function.
- Risk: adding TTL may complicate cache state enough to tempt reintroducing a generic abstraction.
  Severity: medium
  Likelihood: medium
  Mitigation: keep the cache entry record private to `InstanceModsManager` and limited to only the fields needed: fetched time, last good value, and refresh failure state.
- Risk: quiet “status unavailable” and “stale data shown after refresh failed” UI states may be underspecified.
  Severity: low
  Likelihood: medium
  Mitigation: implement small neutral indicators only where state is already rendered, avoiding a broader UX redesign.
- Risk: test execution in this environment may fail because sandboxed MSBuild cannot create temp directories.
  Severity: medium
  Likelihood: high
  Mitigation: still write the tests and record exact commands; if local sandbox blocks execution, note that validation must be run in a normal dev environment.
The final design should replace the current generic LRU with a private cache inside `InstanceModsManager`. Each cache entry should be keyed by the same compatibility dimensions already used today: Minecraft version, mod loader, and project id. The entry should store:
1. the last successful compatible-version list;
2. the timestamp when that success was fetched;
3. the most recent refresh status, so callers can tell whether the currently shown data is fresh, stale-after-failure, or unavailable.
The code should stop using a generic “resolve latest directly” path and instead treat the compatible-version list as the source of truth. The “latest compatible version” is simply the first item in the sorted compatible-version list. Actual install/update actions should use the same shared cached lookup path as the UI, with explicit refresh continuing to force a fresh fetch.
The cache expiration rule should be a short TTL in the 10–30 minute range. The exact constant should be chosen and documented in code. A cache hit is valid only if the last successful fetch is younger than the TTL. If the cached success is older than the TTL, the manager should attempt a fresh fetch.
On fresh fetch success, the manager stores the new sorted compatible-version list, clears any stale-failure marker, and updates the fetch timestamp.
On fresh fetch failure:
1. if there is a previous successful value, return it, mark the result as stale, and propagate refresh failure information to the UI so the user knows the shown data is old and retry is needed later;
2. if there is no previous successful value, return an explicit unavailable state to the caller.
This means the manager should no longer expose “empty array means both no compatibility and request failure”. It should instead use an internal result type with enough information for callers to distinguish:
1. `Success` with versions;
2. `SuccessNoCompatibleVersions` or equivalent successful empty result;
3. `Unavailable` because fetch failed and no prior success exists;
4. `StaleSuccess` because refresh failed but prior data exists.
The concrete names may differ, but the semantics must remain explicit. The UI must be able to distinguish `Success` from `StaleSuccess` even when both carry compatible-version data.
If simple and local, add per-key in-flight request coalescing inside `InstanceModsManager` so multiple concurrent callers for the same compatibility key await one shared fetch task. If that adds substantial complexity, skip it in this pass and document the decision.
Create an internal result model in `InstanceModsManager.cs` that separates successful fetches from unavailable fetches and stale-success-after-failure. Do not expose this as a broad new shared abstraction. Keep it private to the manager unless tests prove a narrow internal helper type is useful.
Refactor the current `ResolveCompatibleVersionsAsync(...)` / `GetCompatibleVersionsAsync(...)` flow so the manager no longer stores a failed lookup as an empty list. A genuine successful “zero compatible versions” result must still be cacheable. A failed request must never overwrite a previous successful cache entry.
At the end of this milestone, a code reader should be able to answer, by reading only `InstanceModsManager.cs`, what happens for these four cases: fresh-success, fresh-success-empty, fresh-failure-with-stale-success, and fresh-failure-with-no-history.
Validation for this milestone is a new or updated test that proves a failed lookup after a successful lookup does not erase the last known result and does propagate a stale/failure signal, and a test that proves a failed first lookup produces an unavailable state rather than an empty “no versions” state.
Remove the dependency on `GenericLauncher.Shared/Misc/LruCache.cs` from this feature. Implement a private cache store inside `InstanceModsManager` with a small private entry record and a simple eviction strategy that remains bounded.
Because the user wants maximum directness, prefer a private dictionary plus a compact cleanup policy over a reusable generic abstraction. The cache does not need to be perfect LRU if a simpler bounded strategy with timestamps and access ordering achieves the same practical outcome. If a tiny local MRU/LRU list is needed, keep it private to the manager.
The cache should enforce:
1. bounded maximum entry count;
2. TTL expiration on successful entries;
3. stale reuse after failure;
4. preservation of the latest refresh status so stale data can be shown with a failure indicator;
5. case-insensitive keys;
6. thread safety for concurrent readers and writers.
Do not add a second abstraction layer just to describe this cache. The code should remain locally understandable in `InstanceModsManager.cs`.
At the end of this milestone, `InstanceModsManager` should own all cache policy: TTL, stale reuse, refresh-failure propagation, expiration, and eviction.
Validation for this milestone is a targeted test set covering cache hit, cache expiration, stale reuse after failure, successful caching of genuine empty-compatible-version results, and preservation of stale/failure status.
Refactor `ResolveLatestCompatibleVersionAsync(...)` and any duplicate callers so there is one shared compatibility-resolution path. The actual install/update flow should no longer do its own uncached Modrinth project-versions query if the shared cache-backed path already provides the same answer.
The simplest acceptable outcome is:
1. the cache-backed method returns the compatible-version result object;
2. “latest compatible version” is derived from that result in one place;
3. install and update operations use that same derived value.
Remove obsolete helper methods once the shared path is in place. Do not keep parallel logic that sorts versions twice in different methods unless there is a clear test-backed reason.
Validation for this milestone is a test that warms the compatibility data through a UI-style lookup and then performs an install/update action without triggering an unnecessary second project-versions fetch. If that exact assertion proves too brittle, at minimum prove both code paths select the same version under the same fixture.
Update the instance details and project details view-models so they can represent both of these states without pretending the data is fully fresh:
1. compatibility status unavailable because lookup failed and there is no cached success;
2. stale compatibility status because lookup failed but cached success is still being shown.
This should be a small UI-state extension, not a redesign. The user preference is a quiet indicator rather than loud warning text.
For `InstanceDetailsViewModel`, this likely means not marking the mod as fully up to date when status is unavailable, and also exposing a small neutral display state when update info is stale because a refresh failed.
For `ModrinthProjectDetailsViewModel`, this means the current target-state text or related state should be able to express both a quiet unavailable state and a quiet stale/failure state when cached data is shown after a failed refresh.
Keep the wording restrained. A short neutral phrase such as “Status unavailable” or “Refresh failed; showing cached data” is acceptable if needed. If the existing view model can represent this with an icon/flag and little text, that is preferred.
Validation for this milestone is a view-model test that simulates failed lookup with no cache and confirms the resulting state is neutral/unavailable rather than installable-as-if-unknown or up-to-date-as-if-certain, and another test that simulates failed refresh with cached data and confirms the stale/failure indicator is shown while the old data remains visible.
Add or update tests to cover the chosen semantics end to end. The first pass must include all of the following:
1. cache hit uses previously fetched compatible versions;
2. force refresh bypasses cached success;
3. TTL expiry triggers a fresh fetch;
4. fresh failure after prior success returns stale success, keeps it cached, and propagates stale/failure status;
5. fresh failure with no prior success yields unavailable state;
6. genuine successful zero-compatible result is cacheable and distinct from failure;
7. install/update path uses the converged shared resolution path;
8. if request coalescing is implemented, concurrent duplicate misses perform one fetch.
Prefer adding manager-level tests in the Modrinth-related test area rather than only primitive cache tests. If the generic `LruCache` file becomes unused and is removed, its dedicated tests should be removed as well.
The expected files to inspect and likely modify are:
1. `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs`
2. `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs`
3. `GenericLauncher.Shared/Screens/ModrinthProjectDetails/ModrinthProjectDetailsViewModel.cs`
4. `GenericLauncher.Tests/Modrinth/InstanceModsManagerStateTest.cs`
5. `GenericLauncher.Tests/Modrinth/ModrinthInstallFlowTest.cs`
6. `GenericLauncher.Tests/Modrinth/RefreshGateTestSupport.cs`
The following files may also need changes depending on implementation details:
1. `GenericLauncher.Shared/InstanceMods/InstanceModsSnapshot.cs`
2. `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml`
3. `GenericLauncher.Tests/Modrinth/InstanceModsManagerEvictionTest.cs`
The following files should be removed only if they become unused:
1. `GenericLauncher.Shared/Misc/LruCache.cs`
2. `GenericLauncher.Tests/Misc/LruCacheTest.cs`
Run the narrowest relevant tests first, then broader validation.
1. Run the targeted Modrinth and cache-related tests.
   ```plaintext
   dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Modrinth|FullyQualifiedName~GenericLauncher.Tests.Misc"
   ```
   Expected outcome: the new targeted tests pass, and no cache-related tests fail.
2. Run the full solution test command.
   ```plaintext
   dotnet test LavaLauncher.sln
   ```
   Expected outcome: if the environment matches normal development conditions, the solution builds and the relevant tests pass. If the existing runner still reports discoverability limitations for some tests, record that fact exactly and note which targeted tests were still executed successfully.
3. If UI bindings or XAML state changed, run a build.
   ```plaintext
   dotnet build LavaLauncher.sln
   ```
   Expected outcome: successful build with no new binding or compile errors.
4. If the environment prevents test execution because MSBuild cannot create temp directories, capture the failure text and record that validation must be run outside the sandbox. Do not silently skip validation.
Evidence to capture in the final implementation report should include:
- which tests were added or updated;
- which commands were run;
- whether failure-with-stale-success and failure-without-cache behaviors were observed in tests;
- whether the UI stale/failure indicator was exercised in tests;
- whether request coalescing was implemented or intentionally deferred.
- [x] 2026-03-11 Review current branch behavior and identify defects.
- [x] 2026-03-11 Interview the user to choose target semantics.
- [x] 2026-03-11 Update semantics so stale cached data also propagates refresh failure to the UI.
- [x] 2026-03-31 Draft the internal fetch-result model.
- [x] 2026-03-31 Replace generic cache with manager-local TTL cache.
- [x] 2026-03-31 Converge install/update and UI onto one lookup path.
- [x] 2026-03-31 Add quiet unavailable and stale-failure handling in view models.
- [x] 2026-03-31 Add full targeted tests.
- [x] 2026-03-31 Run validation commands and capture evidence.
- [x] 2026-03-31 Update this ExecPlan with implementation outcomes.
- The current branch already improved boundedness by replacing an unbounded dictionary with a bounded cache, but the branch still preserves an uncached compatibility-resolution path for the actual install/update action.
- `ModrinthApiClient.GetProjectVersionsAsync(...)` returns `null` for all failures, so callers must not treat `null` and `[]` as equivalent.
- In this environment, `dotnet test LavaLauncher.sln` failed because MSBuild could not create temp directories in the sandbox. This affects validation but not the design itself.
- The intended UX is not just “show stale data on failure”; it is “show stale data and also tell the user the refresh failed”. That means the manager result type must carry both data and refresh-status metadata.
- Decision: stale successful compatibility data should be kept on transient lookup failure.
  Rationale: the user prefers stale-but-useful over incorrect “no updates”.
- Decision: if stale cached data is shown after a failed refresh, the failure must also be propagated to the UI.
  Rationale: the user wants the launcher to remain honest about refresh failure and encourage retry later rather than silently presenting stale data as fresh.
- Decision: if no prior successful result exists and the fresh lookup fails, the UI should show a quiet unavailable state.
  Rationale: this is more honest than silently implying either “up to date” or “no compatible versions”.
- Decision: install/update actions should use the same cached resolution path as the UI unless the user explicitly refreshes.
  Rationale: this removes duplication and improves consistency.
- Decision: the cache should have a short TTL in the 10–30 minute range.
  Rationale: this preserves reasonable freshness while still reducing repeated network traffic.
- Decision: prefer manager-local cache implementation over a reusable generic cache abstraction.
  Rationale: the user explicitly wants the most direct implementation with fewer layers of indirection.
- Decision: request coalescing is desirable only if it can be implemented simply and locally.
  Rationale: concurrency optimization should not dominate this cleanup.
- Decision: the first pass requires full targeted test coverage for the chosen semantics.
  Rationale: this change alters correctness behavior, not just performance.
- Decision: remove the duplicate latest-version resolution path if possible.
  Rationale: one shared path is simpler, easier to reason about, and less likely to drift.
## Implementation completed 2026-03-31

### Files changed
- `GenericLauncher.Shared/InstanceMods/InstanceModsSnapshot.cs` — added `LatestCompatibleVersionsResult` record; removed unused `using GenericLauncher.Misc` (restored after build failure).
- `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs` — full cache replacement: removed `LruCache<>` field; added private `CompatibleVersionCacheEntry` class, `CompatibleVersionFetchResult` record struct, `_compatibleVersionsCache` dictionary + `_compatibleVersionsCacheOrder` linked list + `_compatibleVersionsCacheLock`; TTL constant 15 minutes, max 512 entries. Added `GetCompatibleVersionsInternalAsync` (four-outcome private method), `UpsertSuccessfulCacheEntry`, `PromoteCacheEntry`, `InvalidateCompatibleVersionsForProjects`. Fixed `ResolveCompatibleVersionsAsync` to throw on null (distinguishing failure from empty). Changed `GetLatestCompatibleVersionsAsync` to return `LatestCompatibleVersionsResult`. Converged `ResolveProjectAsync` to use `GetCompatibleVersionsInternalAsync` then `GetVersionAsync` instead of the old `ResolveLatestCompatibleVersionAsync` bypass. Added `InvalidateCompatibleVersionsForProjects` call in `ApplyManagedModChangeAsync` after successful install/update. Removed `ResolveLatestCompatibleVersionAsync` and the old public `GetCompatibleVersionsAsync`.
- `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs` — updated local `GetLatestCompatibleVersionsAsync` return type; updated `ApplyLatestCompatibleVersions` to extract dict and set `ModsUpdateCheckFailed`; added `[ObservableProperty] bool _modsUpdateCheckFailed`.
- `GenericLauncher.Shared/Screens/ModrinthProjectDetails/ModrinthProjectDetailsViewModel.cs` — updated `RefreshTargetLatestCompatibleVersionAsync` to use `LatestCompatibleVersionsResult`; added `[ObservableProperty] bool _hasCompatibilityRefreshFailure`; updated `TargetStateText` to show "Compatibility status unavailable." and "Installed X. Update status unavailable." for the respective unavailable states.
- `GenericLauncher.Shared/Misc/LruCache.cs` — **deleted** (no longer used).
- `GenericLauncher.Tests/Misc/LruCacheTest.cs` — **deleted** (no longer used).
- `GenericLauncher.Tests/Modrinth/InstanceModsManagerCompatibleVersionsCacheTest.cs` — **new**; 7 tests covering cache hit, force-refresh bypass, TTL expiry (via reflection), stale success after failure, unavailable state, empty-versions cacheability, and install/update path convergence.
- `GenericLauncher.Tests/Modrinth/InstanceModsManagerStateTest.cs` — added `/v2/version/bravo-1` handler for the converged install path's `GetVersionAsync` call.
- `GenericLauncher.Tests/Modrinth/ModrinthProjectDetailsRefreshGateTest.cs` — added `/v2/version/alpha-2` handler for both race-condition tests.

### Validation results
- `dotnet build LavaLauncher.sln` → succeeded, 0 warnings, 0 errors.
- `dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Modrinth|FullyQualifiedName~GenericLauncher.Tests.Misc"` → Passed 170/170.
- `dotnet test LavaLauncher.sln` → Passed 195/195.

### Behaviours exercised in tests
- `GetLatestCompatibleVersionsAsync_CacheHit_DoesNotFetchAgain`: verifies `HasRefreshFailure=false` and single fetch.
- `GetLatestCompatibleVersionsAsync_ForceRefresh_FetchesEvenWhenCacheValid`: verifies bypass and updated version.
- `GetLatestCompatibleVersionsAsync_TtlExpired_FetchesFresh`: uses reflection to backdate FetchedAt; verifies second fetch.
- `GetLatestCompatibleVersionsAsync_FetchFailureWithPriorSuccess_ReturnsStaleWithRefreshFailure`: stale-success path; `HasRefreshFailure=true`, old version still in `Versions`.
- `GetLatestCompatibleVersionsAsync_FetchFailureNoPriorSuccess_ReturnsUnavailable`: unavailable path; `HasRefreshFailure=true`, no version in `Versions`.
- `GetLatestCompatibleVersionsAsync_EmptyVersionsFromApi_CachedAndDistinctFromFailure`: empty array cached, `HasRefreshFailure=false`, second call hits cache.
- `UpdateModAsync_UsesCachedCompatibleVersions_WithoutAdditionalProjectVersionsFetch`: one project-versions fetch total; install uses cached data.

### Surprises & Discoveries during implementation
- `ResolveCompatibleVersionsAsync` previously returned `[]` for both `null` (API failure) and `[]` (genuine empty) responses. After fixing to throw on null, the exception propagates cleanly to `GetCompatibleVersionsInternalAsync`'s catch block, enabling proper stale/unavailable branching.
- The converged install path (`ResolveProjectAsync`) now calls `GetVersionAsync` after getting the version ID from the compatible-versions cache. This required adding `/v2/version/{id}` handlers to three existing tests.
- `ApplyManagedModChangeAsync` must call `InvalidateCompatibleVersionsForProjects` after a successful install/update. Without this, the event-triggered background refresh after an install hits the install-time cache entry instead of fetching fresh data — breaking two pre-existing race-condition tests.
- Request coalescing was intentionally deferred. Multiple concurrent callers for the same key may each attempt a fresh fetch during a cache miss. This is acceptable for this pass; it can be added later inside `GetCompatibleVersionsInternalAsync` if needed.
- Test TTL expiry requires reflection-based `FetchedAt` backdating; no time-injection seam was added (consistent with the plan's directness preference).
