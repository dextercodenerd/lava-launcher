# Refactor repository-owned SQLite concurrency and initialization

This ExecPlan (execution plan) is a living document. The sections `Constraints`, `Tolerances`, `Risks`, `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds.

Status: DRAFT

## Purpose / big picture

The current database layer uses one shared `SqliteConnection` guarded by `AsyncRwLock`, while `LauncherDatabase` already enables SQLite Write-Ahead Logging (WAL). WAL allows a writer and readers to overlap at the database-file level, but that benefit is not realized when multiple reads share one ADO.NET connection object. The current design therefore has two problems at once: it may allow unsafe concurrent use of one connection for reads, and it serializes database behavior in a way that does not match SQLite’s strongest concurrency model for this application.

After this change, the launcher’s database access will have one explicit write path and one explicit read path. The write path will use one long-lived writer connection protected by a simple `SemaphoreSlim(1, 1)`. The read path will open a fresh pooled connection per query. `LauncherRepository` will become the only boundary responsible for moving database work off the caller thread, using centralized `Task.Run(...)` helpers for both reads and writes. `LauncherRepository.InitializeAsync()` will be called explicitly during application startup, and repository methods will throw if called before initialization. A caller that awaits a write and then awaits a read in the same async flow will observe the committed write.

Someone reviewing the finished change should be able to observe success in three ways. First, there are no remaining repository-related `Task.Run(...)` wrappers in higher-level services such as `MinecraftLauncher`; the repository owns that policy. Second, the database layer no longer uses `AsyncRwLock` and no longer executes concurrent reads on one shared `SqliteConnection`. Third, application startup explicitly initializes the repository before `AuthService` and `MinecraftLauncher` begin normal repository use, so lifecycle failures happen at startup instead of surfacing later during the first random query.

## Repository orientation

The key files in this change are:

1. `GenericLauncher.Shared/Database/LauncherRepository.cs`, which is the application-facing repository used by services such as auth and Minecraft management.
2. `GenericLauncher.Shared/Database/LauncherDatabase.cs`, which currently owns one shared connection and the custom reader-writer lock.
3. `GenericLauncher.Shared/Database/Orm/SqliteConnectionExtensions.cs`, which contains the AOT-safe async helpers used by `LauncherDatabase`.
4. `GenericLauncher.Shared/App.axaml.cs`, which is the manual composition root where long-lived services are created.
5. `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs`, which currently contains repository-related `Task.Run(...)` wrappers that should disappear once the repository guarantees off-caller-thread execution.

The repository is manually composed in `App.axaml.cs`. That manual composition rule is intentional in this codebase and must remain intact. This plan changes how the repository and database classes divide responsibilities, but it does not introduce a dependency injection container or any other runtime composition mechanism.

## Constraints

- Keep manual composition intact. Do not introduce `IServiceCollection`, service providers, runtime discovery, or reflection-driven dependency injection.
- Preserve AOT and trimming safety. Do not add reflection-heavy libraries or patterns. Continue using the existing AOT-safe database helpers in `GenericLauncher.Shared/Database/Orm/SqliteConnectionExtensions.cs`.
- Keep the existing repository-facing behavior stable for current callers except where this plan explicitly requires additions such as `InitializeAsync()` and `CancellationToken` parameters. If implementation proves that wider API changes are required, stop and escalate.
- Do not weaken write semantics. Any awaited repository write must complete only after its SQL work is fully committed or the operation fails and surfaces the exception.
- Do not add a single-consumer queue, custom worker pool, or background service framework. Parallel reads should remain possible through separate pooled SQLite connections.
- Do not reintroduce concurrent access to one shared `SqliteConnection`.
- Keep SQLite WAL enabled and continue using one writer at a time at the application level.
- Do not add schema migrations beyond whatever `EnsureCreatedAsync()` and the existing table migration methods already do.
- Do not change unrelated service behavior in auth, mod management, or launcher startup beyond what is needed to adapt to explicit repository initialization and the removal of caller-side DB offloading.

If satisfying the goal requires violating one of these constraints, stop, document the conflict in `Decision Log`, and ask for direction.

## Tolerances

- Scope: if implementation requires touching more than 6 files or more than 300 net lines of code, stop and ask whether to widen scope.
- Interface: if any public repository method must change in a way that forces broad call-site churn beyond the expected initialization and optional cancellation-token updates, stop and ask.
- Startup lifecycle: if explicit repository initialization in `App.axaml.cs` forces asynchronous startup changes that are materially more invasive than expected, stop and present options before proceeding.
- Async semantics: if `Microsoft.Data.Sqlite` behavior forces a larger redesign than centralized repository `Task.Run(...)` helpers plus connection separation, stop and ask before adding custom scheduling infrastructure.
- Validation: if `dotnet build LavaLauncher.sln` fails for reasons unrelated to this change, document the failure, capture the output, and stop after verifying the failure is unrelated.

## Risks

- The repository currently starts initialization in its constructor. Moving to explicit `InitializeAsync()` may reveal assumptions elsewhere in startup ordering. Mitigation: update only the composition root first, then keep repository guards strict so misuse fails immediately with a clear exception.
- Some SQLite PRAGMAs are connection-local rather than database-persistent. Using transient read connections without applying the required PRAGMAs could cause inconsistent behavior. Mitigation: centralize read-connection creation in `LauncherDatabase` and apply the same connection-local settings on every opened connection.
- Using `Task.Run(...)` on every repository call is intentionally simple, but it adds overhead and may not be the final long-term design. Mitigation: isolate the policy in a tiny number of repository helpers so it can be swapped later without changing call sites.
- Removing `AsyncRwLock` may expose hidden assumptions about read-after-write visibility. Mitigation: preserve the rule that repository write methods do not complete early, and validate sequential write-then-read flows in the tests or during focused manual reasoning.
- The current code may dispose only the shared connection and not the lock. Refactoring lifetime management could reveal missing disposal expectations. Mitigation: make `LauncherDatabase` disposal own both the writer connection and writer semaphore lifetime cleanly.

## Proposed design

`LauncherRepository` will become the threading and lifecycle boundary. Its constructor will only capture platform paths, logger references, and a `LauncherDatabase` instance. It will not start any background initialization work. Instead, it will expose `InitializeAsync(CancellationToken)` and require callers to invoke that method explicitly from the composition root before normal repository use.

`LauncherRepository` will contain small centralized helpers for reads and writes. Each helper will first verify that initialization has completed successfully, then use `Task.Run(...)` to run the lower-level database operation off the caller thread. The read helper will return values, and the write helper will return either `Task` or `Task<T>` depending on the database operation. These helpers are the only place where repository threading policy is encoded. Higher layers such as `MinecraftLauncher` must simply await repository methods directly.

`LauncherDatabase` will become a lower-level SQLite executor. It will own one persistent writer `SqliteConnection` and one write `SemaphoreSlim(1, 1)`. It will also own a read-connection factory method that opens a new pooled `SqliteConnection` for each read. The database class will expose low-level methods with names that make the contract explicit, such as `GetAllAccountsCoreAsync(...)`, `UpsertAccountCoreAsync(...)`, `GetAllMinecraftInstancesCoreAsync(...)`, `SetMinecraftInstanceStateCoreAsync(...)`, and similar methods for the rest of the existing operations.

The writer connection will be initialized during `InitializeAsync()` or an equivalent lower-level initialization method invoked from the repository. Initialization will open the writer connection, apply the initialization PRAGMAs, and run table creation through the existing migration methods. The read-connection factory will apply the required connection-local PRAGMAs to every transient read connection. `journal_mode=WAL` should remain part of initialization, while connection-local settings such as foreign key enforcement and busy timeout must also be applied on read connections where relevant.

The current `AsyncRwLock` will be removed from `LauncherDatabase` and no longer used by this database path. One writer at a time will be enforced by the write semaphore. Reads will not take an application-level reader lock; concurrency will be delegated to SQLite and the connection pool through separate read connections.

## Implementation plan

### Milestone 1: reshape lifecycle and thread ownership

Start in `GenericLauncher.Shared/Database/LauncherRepository.cs`. Remove constructor-started `_initTask = Task.Run(...)`. Add explicit initialization state tracking and `InitializeAsync(CancellationToken ct = default)`. Make initialization idempotent for repeated awaited calls from the composition root, but fail fast if regular repository methods are used before initialization completes. A simple guard that throws `InvalidOperationException` with a clear message is the expected behavior.

Add small repository-private helpers for read and write execution. These helpers should:

1. verify initialization has completed;
2. call `Task.Run(...)` with the lower-level database delegate;
3. preserve exception flow without wrapping unrelated exceptions;
4. accept and pass through `CancellationToken` where supported.

At this milestone, no call sites outside the repository should change yet except for the new explicit initialization in the composition root.

### Milestone 2: split the database into writer and reader paths

Refactor `GenericLauncher.Shared/Database/LauncherDatabase.cs` so it no longer accepts or stores one shared connection used for both reads and writes. Replace that with:

1. one persistent writer `SqliteConnection`;
2. one `SemaphoreSlim(1, 1)` to serialize writes;
3. a stored connection string or builder used to open transient read connections;
4. one helper that configures the common connection PRAGMAs for any opened connection.

Move all existing SQL methods into explicit `...CoreAsync` methods. Write methods must acquire the write semaphore, execute the SQL on the persistent writer connection, await completion, and release the semaphore in `finally`. Read methods must create a transient read connection with `await using`, configure it, run the read query, and dispose it when done.

Keep each existing repository-level operation mapped to one lower-level SQL statement as it is today. Do not introduce transaction helpers in this change. The required semantic rule is that once a repository write method completes, the SQL statement is fully complete and observable to later reads.

### Milestone 3: update startup and caller expectations

Update `GenericLauncher.Shared/App.axaml.cs` so the composition root explicitly initializes the repository before services begin normal work. The exact location must respect Avalonia startup rules and the app’s current manual composition model. The simplest acceptable outcome is that the app awaits repository initialization during startup before any service method can use it.

Update higher-level call sites that currently compensate for repository behavior. In particular, remove repository-related `Task.Run(...)` wrappers from `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs`. The service should directly await repository methods, because the repository now owns the off-caller-thread policy. Review `AuthService` and other repository consumers to confirm there are no remaining repository-specific DB offloading wrappers.

### Milestone 4: validate behavior and document results

Run targeted build validation after the code changes. Because the repository test project is currently not discoverable by the configured test runner, use build validation first and only add or run tests if they are already reliable in the working tree. Record any inability to run meaningful tests in `Outcomes & Retrospective`.

The minimum validation commands are:

```plaintext
dotnet build LavaLauncher.sln
dotnet test LavaLauncher.sln
```

Expected results are:

1. `dotnet build LavaLauncher.sln` succeeds without introducing new database-layer compile errors.
2. `dotnet test LavaLauncher.sln` either behaves as previously documented in `AGENTS.md` with “No test is available” for `GenericLauncher.Tests`, or it succeeds if the local environment has improved since that note. Any deviation that appears unrelated to this change should be documented and escalated rather than “fixed” opportunistically.

If build validation reveals a regression in startup composition or repository method signatures, return to the earlier milestone, update this plan’s `Progress`, `Decision Log`, and `Surprises & Discoveries`, and only then continue.

## Acceptance criteria

The change is complete when all of the following are true:

1. `GenericLauncher.Shared/Database/LauncherDatabase.cs` no longer uses `AsyncRwLock` and no longer executes multiple reads on one shared connection object.
2. `GenericLauncher.Shared/Database/LauncherRepository.cs` exposes explicit `InitializeAsync(...)`, throws if regular methods are used before initialization, and owns the centralized `Task.Run(...)` policy for both reads and writes.
3. `GenericLauncher.Shared/App.axaml.cs` explicitly initializes the repository during startup in a way that surfaces initialization failures at startup time.
4. `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs` no longer wraps repository calls in `Task.Run(...)`.
5. An awaited repository write followed by an awaited repository read in the same async flow is still guaranteed to observe the completed write because the repository write does not complete early and the read uses a new connection.
6. `dotnet build LavaLauncher.sln` succeeds, and `dotnet test LavaLauncher.sln` produces no new unexpected failures beyond the known test-discovery limitation documented in this repository.

## Validation procedure

Before implementation, capture the current relevant behavior by inspecting:

```plaintext
GenericLauncher.Shared/Database/LauncherRepository.cs
GenericLauncher.Shared/Database/LauncherDatabase.cs
GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs
GenericLauncher.Shared/App.axaml.cs
```

During implementation, keep changes small and validate after each milestone where possible. After Milestone 2, the code should compile locally even if startup wiring is not finished yet. After Milestone 3, run:

```plaintext
dotnet build LavaLauncher.sln
```

If the build succeeds, then run:

```plaintext
dotnet test LavaLauncher.sln
```

Record concise evidence in this file under `Outcomes & Retrospective`, for example:

```plaintext
$ dotnet build LavaLauncher.sln
Build succeeded.

$ dotnet test LavaLauncher.sln
No test is available in GenericLauncher.Tests
```

If local output differs, paste only the short excerpt needed to explain success or failure.

## Progress

- [x] 2026-03-31 10:42 CEST — Drafted the ExecPlan after reviewing `LauncherRepository`, `LauncherDatabase`, `AsyncRwLock`, the repository call sites, and the composition root.
- [ ] Await approval before implementation.
- [ ] Reshape repository lifecycle and threading helpers.
- [ ] Refactor `LauncherDatabase` into explicit writer and read-connection paths.
- [ ] Update startup initialization and remove higher-level repository `Task.Run(...)` wrappers.
- [ ] Validate with build and test commands and capture evidence.

## Surprises & Discoveries

- `LauncherDatabase` already enables `PRAGMA journal_mode = WAL`, so the main concurrency mismatch is not missing WAL but the use of one shared `SqliteConnection` together with a reader lock that permits multiple concurrent reads.
- `LauncherRepository` currently starts initialization with `Task.Run(() => _db.EnsureCreatedAsync())` in the constructor, which hides startup failure timing and makes repository readiness implicit.
- `MinecraftLauncher.DeleteInstanceAsync(...)` currently contains multiple repository-related `Task.Run(...)` wrappers, confirming that database offloading policy has leaked out of the repository boundary.
- `LauncherDatabase.DisposeAsync()` currently disposes only the shared connection and does not own cleanup of the custom lock, which is another signal that the lifetime model is due for simplification.

## Decision Log

- 2026-03-31 10:42 CEST — Chosen connection model: one persistent writer connection plus transient pooled read connections. Rationale: this aligns with SQLite WAL behavior, avoids concurrent reads on one shared connection, and keeps the refactor smaller than a full actor or queue-based design.
- 2026-03-31 10:42 CEST — Chosen threading boundary: `LauncherRepository` owns off-caller-thread execution through centralized `Task.Run(...)` helpers. Rationale: this gives higher layers a simple contract and removes the need for repository-specific `Task.Run(...)` wrappers in callers.
- 2026-03-31 10:42 CEST — Chosen lifecycle model: explicit `InitializeAsync()` called from the composition root, with fail-fast repository guards if used before initialization. Rationale: startup failures should surface at startup, not lazily later.
- 2026-03-31 10:42 CEST — Chosen write consistency rule: awaited repository writes complete only after the statement is fully committed or failed, and later awaited reads in the same async flow must observe them. Rationale: this matches the intended semantics already described during design discussion and avoids deferred write behavior.
- 2026-03-31 10:42 CEST — Rejected alternatives: no single-consumer queue, no custom worker pool, no transaction abstraction in this first refactor. Rationale: correctness and clarity come first; these alternatives add infrastructure beyond the agreed scope.

## Outcomes & Retrospective

This section will be completed during implementation. It must summarize what shipped, what commands were run, the observed outputs, and any follow-up improvements that became obvious once the refactor was complete.
