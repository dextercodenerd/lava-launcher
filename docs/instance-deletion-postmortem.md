# Instance Deletion Postmortem

## Executive Summary

- Architectural violation: the new cache-eviction hook does not match the real cache-key shape, so deletion does not actually evict latest-compatible-version entries for the deleted instance. Production code removes keys prefixed by `instance.Id|` in `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:925-944`, but the cache is keyed as `VersionId|ModLoader|ProjectId` in `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:1053-1054`. The tests repeat the same wrong assumption in `GenericLauncher.Tests/Modrinth/InstanceModsManagerEvictionTest.cs:18-19,34-35`.
- Architectural violation: `MinecraftLauncher` now reaches across the `InstanceModsManager` boundary to delete the instance folder and then manually clean manager-owned caches (`GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:547-572`). That coupling exists without any shared lock or deletion contract with the mod-state subsystem.
- Technical debt: the ExecPlan was not kept honest once implementation expanded. The plan still claims `~10 files`, `~150 lines`, `7 files changed`, and `180 tests passed` in `docs/instance-deletion.md:29-31,57,81-84`, but the branch diff from the merge-base touches 14 files and current validation on March 25, 2026 is `188` passing tests.
- Positive: the user-visible state machine largely landed as intended. The `Ready -> Deleting -> gone / DeleteFailed` flow is represented coherently across the enum, launcher, view model, and view (`GenericLauncher.Shared/Database/Model/MinecraftInstance.cs:25-31,200-216`, `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:511-575`, `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs:165-183,385-421`, `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml:42-66`).
- Positive: the implementation stayed aligned with the repo's navigation ownership model. Deletion completion routes back through the coordinator callback from `MainWindowViewModel` instead of allowing the screen to navigate directly (`GenericLauncher.Shared/Screens/MainWindow/MainWindowViewModel.cs:237-249`, `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs:167-172`).

## Specification Gaps

1. The scope guard in the plan was fiction by the time the branch was done. `docs/instance-deletion.md:29-31` capped the work at roughly 10 files and 150 net new lines, but the branch includes database fixes, a new `.editorconfig`, a large unrelated test reformat, and three new test cases. The document's retrospective never reconciles that expansion.

2. The spec's concurrency story was underspecified. It claims `_lock` in `MinecraftLauncher` serializes deletion against create and that any concurrent mod operation "will fail gracefully" (`docs/instance-deletion.md:39-42`), but the implementation does not coordinate with `InstanceModsManager`'s per-instance locks before `Directory.Delete(..., true)` (`GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:547-553`, `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:929-930,1056-1060`). The plan hand-waved a guarantee the code does not actually enforce.

3. The cache-cleanup requirement was missing from the original architecture description. The spec identifies `InstanceModsManager` caches (`docs/instance-deletion.md:92`) but does not define a deletion boundary for them. That omission likely led to `MinecraftLauncher` learning manager internals and then getting the cache key wrong.

4. The validation section drifted. `docs/instance-deletion.md:57,81-84` reports build success and 180 passing tests. Fresh validation on March 25, 2026 succeeded, but the test count is now 188 and the initial concurrent build attempt failed due to a locked PDB because build and test were run in parallel. The document should capture actual, reproducible verification rather than stale numbers.

## Architecture Assessment

Architecture pattern assessed: custom layered MVVM with manual composition in `App`, repository-backed persistence, and `MainWindowViewModel`-owned navigation. No provided template cleanly fits; this review uses the core dimensions.

### Specification Fidelity

- Implemented well: the state machine from the ExecPlan is represented explicitly in the domain enum and surfaced in the UI. `MinecraftInstanceState` gained `Deleting` and `DeleteFailed` in `GenericLauncher.Shared/Database/Model/MinecraftInstance.cs:25-31`, and the view model/view pair exposes status, retry, and confirmation UI in `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs:68-72,375-421` and `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml:42-66`.
- Divergence: the plan said cache eviction was part of completion, but the shipped eviction logic only removes snapshots and per-folder locks reliably. The version cache removal path does not hit the real keys (`GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:925-944,1053-1054`).
- Divergence: the retrospective claims the implementation stayed within tolerance and touched seven files (`docs/instance-deletion.md:81-84`). That is not true of the branch that actually shipped.

### Boundary Integrity

- Violation: `MinecraftLauncher` now needs to know that deleting an instance also requires manual calls into `InstanceModsManager` cache internals (`GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:569-571`). That is a lifecycle concern crossing from the launcher/service layer into mod-state storage details.
- Violation: `InstanceModsManagerEvictionTest` reaches into a private field via reflection (`GenericLauncher.Tests/Modrinth/InstanceModsManagerEvictionTest.cs:65-69`) instead of testing behavior through a supported API. That is a test boundary violation and a sign the production abstraction is missing the right seam.
- What worked: navigation ownership stayed clean. `MainWindowViewModel` provides `onDeleted: () => Navigation.Pop()` and `InstanceDetailsViewModel` merely invokes the callback when its instance disappears (`GenericLauncher.Shared/Screens/MainWindow/MainWindowViewModel.cs:239-247`, `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs:167-172`).

### State Management

- Good: database state remains authoritative for deletion progress. `DeleteInstanceAsync` writes `Deleting`, refreshes instances, then either writes `DeleteFailed` or removes the record (`GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:515-574`). That preserves restart-visible failure state, which was the main product requirement.
- Weak: the final database deletion result is ignored. `RemoveMinecraftInstanceAsync` returns `bool`, but `DeleteInstanceAsync` does not inspect it (`GenericLauncher.Shared/Database/LauncherRepository.cs:108-112`, `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:569-570`). If the row is not deleted, the method still evicts caches and refreshes as if deletion succeeded.
- Weak: filesystem state is mutated without using the per-instance locks already owned by `InstanceModsManager`. This leaves authoritative ownership of instance-on-disk state split across two services.

### Error Handling

- Good: disk deletion failure is handled explicitly and promoted to a restart-visible state. The launcher logs the exception, writes `DeleteFailed`, refreshes instances, and rethrows a user-facing error (`GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:556-566`).
- Weak: recovery semantics stop at the filesystem boundary. There is no equivalent error path if the final database delete fails, and there is no compensating action if cache eviction fails or becomes stale.
- Weak: the UI always collapses deletion failures to the same generic string (`GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs:397-401,417-420`). That is acceptable for now, but it throws away diagnostics that could help distinguish permission problems from logic errors.

### Testability

- Good: the branch added a targeted regression test for the custom database API misuse. `GenericLauncher.Tests/Database/LauncherDatabaseTest.cs:14-31` documents why `ExecuteScalarAsync<long>` on a `DELETE` was wrong, and the follow-up tests assert the fixed `ExecuteAsync` path for both accounts and instances (`GenericLauncher.Tests/Database/LauncherDatabaseTest.cs:33-75`).
- Weak: the cache-eviction tests are white-box and validate the wrong contract. By seeding `_latestCompatibleVersionCache` with `instance.Id`-based keys through reflection, the tests assert a behavior production code never uses (`GenericLauncher.Tests/Modrinth/InstanceModsManagerEvictionTest.cs:18-19,34-35,65-69`).
- Gap: there is no integration-style test covering the full delete flow from `DeleteInstanceAsync` through state transition, folder removal, cache cleanup, and instance disappearance from `Instances`.

## Boundary Violations

- `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs:569-571` calls `_instanceModsManager.EvictInstanceCaches(instance)` after directly deleting the instance folder. `MinecraftLauncher` should not need to know which caches exist inside the mod subsystem or how to clean them.
- `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:925-944` duplicates cache-key knowledge instead of encapsulating it next to `GetLatestCompatibleVersionCacheKey` at `GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:1053-1054`. The duplication is already wrong.
- `GenericLauncher.Tests/Modrinth/InstanceModsManagerEvictionTest.cs:65-69` reads a private field via reflection. The test is coupled to implementation details instead of a supported observable behavior.

## Library Feedback

### LauncherDatabase / custom ORM

#### Fit for Purpose

- Fit is mostly good. The repository and database layers remain explicit, AOT-safe, and easy to trace.

#### What Worked

- The custom `ExecuteAsync` API made it easy to correct the deletion bug once it was recognized. `RemoveAccountAsync` and `DeleteMinecraftInstanceAsync` now both use affected-row counts correctly in `GenericLauncher.Shared/Database/LauncherDatabase.cs:82-87,146-152`.

#### What Hurt

- The API surface is easy to misuse because `ExecuteScalarAsync<long>` on `DELETE` compiles and silently returns the wrong signal. That defect was serious enough to need a dedicated explanatory test in `GenericLauncher.Tests/Database/LauncherDatabaseTest.cs:14-31`.
- Impact: wasted implementation time, an extra fixup commit, and a feature branch that had to widen scope to repair persistence semantics.

#### Documentation Gaps

- There is no obvious repository-local guidance on when to use `ExecuteAsync` versus `ExecuteScalarAsync` for mutation statements. The new regression test acts as living documentation, but only after the mistake happened.

### InstanceModsManager

#### Fit for Purpose

- Fit is mixed. It already owned the right concepts for per-instance snapshots and folder-scoped locks, but it did not expose a first-class deletion or lifecycle API.

#### What Worked

- `InvalidateSnapshot(instance.Id)` and the existing folder-lock map gave the implementation a clear place to start cleanup (`GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:925-930`).

#### What Hurt

- The manager forces callers to understand cache internals they should not know. `EvictInstanceCaches` had to guess which caches mattered and how they were keyed, and it guessed wrong (`GenericLauncher.Shared/InstanceMods/InstanceModsManager.cs:932-944,1053-1054`).
- Impact: stale cache risk, a false-positive test suite, and tighter coupling between launcher lifecycle and mod metadata plumbing.

#### Documentation Gaps

- There is no documented contract for "instance is being deleted; stop touching this folder and drop all state." The lack of that contract is the root cause of the cross-boundary cleanup logic.

### CommunityToolkit MVVM + Avalonia compiled bindings

#### Fit for Purpose

- Good fit for the UI portion of this work. Small derived-state additions were straightforward and stayed in the existing pattern.

#### What Worked

- Adding `CanDelete`, `IsDeleting`, `IsDeleteFailed`, and the delete commands was cheap in the current ViewModel model (`GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs:68-72,375-421`), and the XAML changes stayed localized (`GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml:42-66`).

#### What Hurt

- The pattern still requires manual `OnPropertyChanged(...)` fan-out for derived state when `Instance` changes (`GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs:177-183,239-245`). That is manageable here but scales poorly.

#### Documentation Gaps

- None specific to this change.

## Tooling Report Card

| Tool | Purpose | Effectiveness | Recommendation |
|------|---------|---------------|----------------|
| ExecPlan in `docs/instance-deletion.md` | Scope control and implementation journal | Useful at the start, weak at the end because tolerances, file counts, and validation results were left stale | Improve |
| `dotnet test LavaLauncher.sln` | Regression validation | Strong. On March 25, 2026 it passed with 188 tests and surfaced no regressions | Keep |
| `dotnet build LavaLauncher.sln` | Compile validation | Strong when run serially. The first concurrent run produced a locked-PDB false failure, which is operator noise rather than product risk | Keep |
| Reflection-based eviction tests | Validate cache cleanup | Weak. They asserted the wrong cache contract and therefore gave false confidence | Improve |
| Directory-local `.editorconfig` plus broad formatter churn | Style enforcement | Poor fit for this feature branch. It expanded diff size and obscured the actual deletion review surface | Improve |

## Recommendations

- S: Fix `EvictInstanceCaches` so it uses the real cache key contract, then rewrite the tests to seed and observe behavior through supported operations instead of reflection. This is the only clearly broken functional piece in the shipped deletion path.
- M: Introduce a deletion-oriented contract between `MinecraftLauncher` and `InstanceModsManager`. The launcher should be able to say "delete this instance" and have the mod subsystem acquire its own folder lock, stop future work, and drop its caches internally.
- S: Treat a `false` result from `RemoveMinecraftInstanceAsync` as a hard failure. Do not evict caches or navigate away unless the database record was actually removed.
- S: Update `docs/instance-deletion.md` to reflect the real branch scope, the database bug discovered mid-flight, and the actual validation numbers from March 25, 2026. A living plan that stops being true stops being useful.
- S: Keep formatting/tooling-only churn out of feature branches unless the branch is explicitly about style or cleanup. The added `.editorconfig` and large unrelated test reformat made the architectural review harder for no product gain.
