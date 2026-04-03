# Generalize mod-loader classpath precedence and deduping

This ExecPlan (execution plan) is a living document. The sections `Constraints`, `Tolerances`, `Risks`, `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds.

Status: COMPLETE

## Purpose / big picture

The launcher currently builds a modded Minecraft classpath by taking the vanilla classpath and appending the selected mod loader's libraries. That merge happens in shared launch code, but it only removes exact duplicate file paths. This is too weak when vanilla and a mod loader ship the same logical library from different directories or at different versions. Fabric already detects this and aborts launch with `duplicate ASM classes found on classpath`. Forge and NeoForge use the same shared merge path and can suffer the same category of bug whenever their launch libraries overlap with vanilla or with future loaders.

After this change, every mod loader that contributes `ResolvedModLoaderLibrary` entries will use one shared classpath merge rule. Logical duplicates will be identified by Maven-style artifact identity rather than absolute path, and when vanilla and a mod loader provide the same logical library, the mod loader's library will win. A user should be able to observe success by launching a modded instance whose mod loader overrides a vanilla dependency and seeing the instance start instead of failing during bootstrap because the same package appears twice on the classpath.

This plan also includes explicit documentation of the precedence rule. A future reader should not need to reverse-engineer the behavior from tests or infer it from order-dependent code. The code and project documentation should both state clearly that mod-loader-provided libraries override vanilla libraries when the logical artifact identity matches.

## Constraints

- Keep manual composition intact. Do not introduce dependency-injection containers, runtime discovery, or reflection-driven service composition.
- Preserve trimming and AOT safety. Do not add reflection-heavy helpers, dynamic package scanners, or new external dependencies.
- Do not special-case Fabric, Forge, or NeoForge in the shared precedence logic. The merge rule must be generic for any current or future mod loader that returns `ResolvedModLoaderLibrary`.
- Do not change database schema. Persisting repaired classpaths must reuse the existing `MinecraftInstances` table shape.
- Do not silently alter unrelated launch behavior such as JVM argument selection, natives extraction, or instance folder layout.
- Do not mutate or remove unrelated user work already present in the working tree, especially the existing changes in `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsView.axaml`, `GenericLauncher.Shared/Screens/InstanceDetails/InstanceDetailsViewModel.cs`, and `GenericLauncher.Tests/Modrinth/InstanceDetailsRefreshGateTest.cs`.
- Keep non-Maven or unparseable classpath entries conservative. If the helper cannot confidently derive a logical library identity, it must fall back to full-path identity rather than guessing.
- Document the precedence rule in code and in repository documentation. The rule is part of behavior, not an incidental implementation detail.

If satisfying the goal requires violating one of these constraints, stop, record the conflict in `Decision Log`, and ask for direction.

## Tolerances (exception triggers)

- Scope: if implementation requires touching more than 8 files or more than 350 net lines of code, stop and ask whether to widen scope.
- Interface: if any public API used outside the Minecraft launch path must change shape, stop and ask before proceeding.
- Persistence: if repairing stored classpaths requires a schema migration or a data backfill outside normal runtime flows, stop and ask.
- Loader semantics: if Forge or NeoForge reveal loader-specific precedence rules that conflict with the proposed generic rule, stop and present the conflict with evidence.
- Path parsing: if a reliable Maven-style identity cannot be derived from enough real classpath entries to make the helper safe, stop and present a narrower fallback design.
- Testing: if targeted launch or classpath tests still fail after 3 focused iterations, stop and report the failing cases plus the current hypothesis.
- Validation: if `dotnet build LavaLauncher.sln` or the relevant test command fails for reasons that appear unrelated to this work, capture the failure and stop after confirming it is unrelated.

## Risks

- Risk: some loader-provided entries may not follow the exact same path pattern as vanilla Maven artifacts.
  Severity: medium
  Likelihood: medium
  Mitigation: derive identity from Maven-style relative paths when possible, and fall back to full-path identity when parsing is uncertain.
- Risk: stored instance classpaths may already contain bad merged entries from prior installs.
  Severity: high
  Likelihood: high
  Mitigation: normalize persisted classpaths before launch and write the repaired list back through the repository when it changes.
- Risk: preserving classifier-specific artifacts incorrectly could break natives handling or loader bootstrap.
  Severity: high
  Likelihood: medium
  Mitigation: make classifier part of the logical identity, so `artifact` and `artifact:natives-*` remain distinct.
- Risk: a generic helper could accidentally reverse precedence if ordering is implemented incorrectly.
  Severity: high
  Likelihood: medium
  Mitigation: implement and test explicit last-wins semantics, and document that mod-loader libraries are appended after vanilla specifically so the loader wins on collision.
- Risk: future mod loaders may provide their own internally deduped libraries but still rely on shared merge behavior against vanilla.
  Severity: medium
  Likelihood: high
  Mitigation: keep the precedence helper in shared launch code rather than in one loader service, and describe the contract in terms of `ResolvedModLoaderLibrary`.

## Progress

- [x] 2026-03-31 20:25 CEST Drafted the initial Fabric-focused plan after tracing the shared merge path and verifying the duplicate ASM failure against local cached metadata.
- [x] 2026-03-31 20:34 CEST Expanded the design to cover Forge, NeoForge, and future mod loaders by anchoring the change to the shared `ApplyModLoaderToVersion(...)` path.
- [x] 2026-03-31 20:45 CEST Chose repository documentation as part of scope so the precedence rule is explicit and durable.
- [x] 2026-03-31 20:55 CEST Wrote this ExecPlan into `docs/mod-loader-classpath-precedence-execplan.md`.
- [x] 2026-04-03 Approved for implementation by the user request to execute the plan step by step.
- [x] 2026-04-03 Added a shared `MinecraftClassPath` helper that derives Maven-style logical identities from `libraries/...` paths and applies last-wins normalization.
- [x] 2026-04-03 Wired the helper into `ApplyModLoaderToVersion(...)` and launch-time persisted-classpath normalization in `MinecraftLauncher`.
- [x] 2026-04-03 Added narrow repository/database support for persisting repaired classpaths via `SetMinecraftInstanceClassPathAsync`.
- [x] 2026-04-03 Documented the precedence rule in code comments and in `mod_launchers.md`.
- [x] 2026-04-03 Added targeted tests for logical identity extraction, loader-wins merging, classifier preservation, launch-time repair writeback, and the database-only classpath update path.
- [x] 2026-04-03 Validation succeeded with `dotnet build LavaLauncher.sln`, `dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Minecraft"`, and `dotnet test LavaLauncher.sln`.
- [x] 2026-04-03 Updated this ExecPlan with implementation results and evidence.

## Surprises & Discoveries

- The current duplicate-library bug is not located in any Fabric-specific merge code. The shared merge in `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs` appends loader libraries and calls `Distinct` on full file paths, which means different folders and versions of the same logical library both survive.
- The local cached Fabric profile for `fabric-loader-0.18.5-1.21.4` explicitly includes `org.ow2.asm:asm:9.9`, while vanilla `1.21.4` includes `org.ow2.asm:asm:9.6`. This confirms the reported crash is deterministic classpath construction behavior, not a bad mod jar.
- Forge and NeoForge already resolve launch libraries through `ResolvedModLoaderLibrary` and the same shared merge path, so a Fabric-only fix would leave the architectural bug in place.
- The repository currently has no dedicated method for updating an instance's stored `ClassPath` without touching unrelated fields, so self-healing persisted instances requires a small repository/database addition even though no schema change is needed.
- The repo already keeps architectural notes in `mod_launchers.md` and `docs/`, which makes a short documentation update a better fit than leaving the precedence rule only in inline code comments.
- The safest reliable parser boundary in this codebase is the existing `libraries/` folder contract. Anchoring logical identity extraction to the last `libraries` path segment avoids accidentally treating arbitrary absolute-path prefixes as Maven group ids.
- The repository guidance captured on 2026-03-11 about `dotnet test LavaLauncher.sln` reporting `No test is available` no longer matches the current environment. On 2026-04-03 the full solution test command discovered and passed 214 tests.

## Decision Log

- Decision: the precedence rule is generic, not Fabric-specific.
  Rationale: the bug sits in shared launch code and all current mod loaders flow through the same merge contract.
  Date/Author: 2026-03-31 Codex.
- Decision: when vanilla and a mod loader provide the same logical library identity, the mod loader's library wins.
  Rationale: the mod loader resolves the launch environment it expects, and Fabric already demonstrates that leaving both copies on the classpath is incorrect.
  Date/Author: 2026-03-31 Codex.
- Decision: logical identity will be based on Maven-style artifact identity in the form `group:artifact[:classifier]@extension`, explicitly ignoring version.
  Rationale: the collision we need to collapse is same artifact family across versions and directories, while classifier and extension remain behaviorally significant.
  Date/Author: 2026-03-31 Codex.
- Decision: if identity cannot be parsed confidently, fall back to full-path identity.
  Rationale: conservative behavior is safer than over-deduping a non-Maven entry.
  Date/Author: 2026-03-31 Codex.
- Decision: Maven-style identity extraction is anchored to the last `libraries` path segment instead of scanning arbitrary path suffixes.
  Rationale: vanilla and current mod-loader services already store Java launch libraries under `.../libraries/...`, and using that contract keeps parsing conservative for persisted absolute paths.
  Date/Author: 2026-04-03 Codex.
- Decision: already-persisted broken classpaths should self-heal on next launch and then be written back to the database.
  Rationale: users should not need to reinstall instances just to receive a classpath construction fix.
  Date/Author: 2026-03-31 Codex.
- Decision: the precedence rule must be documented in repository docs as well as code.
  Rationale: this is an intentional contract that future loader work must preserve.
  Date/Author: 2026-03-31 Codex.

## Outcomes & Retrospective

Implementation stayed within the planned scope. The code changes live in `GenericLauncher.Shared/Minecraft/MinecraftClassPath.cs`, `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs`, `GenericLauncher.Shared/Database/LauncherRepository.cs`, and `GenericLauncher.Shared/Database/LauncherDatabase.cs`. The helper now derives logical identities from `libraries/...` Maven-style paths, ignores version when deduping, and preserves classifier and extension so natives remain distinct. `ApplyModLoaderToVersion(...)` uses that helper for install-time merging, and `LaunchInstance(...)` now normalizes persisted classpaths before launch and writes repaired values back through the new narrow repository/database update.

Validation covered both behavior and persistence support. `GenericLauncher.Tests/Minecraft/MinecraftClassPathTest.cs` proves that `org/ow2/asm/asm/9.6/asm-9.6.jar` and loader-owned `.../asm/9.9/asm-9.9.jar` collapse to one logical entry with the loader jar retained, and that Fabric-, Forge-, and NeoForge-shaped library roots all use the same shared rule. The same test file also proves classifier jars remain distinct and that launch-time normalization persists repairs only when needed. `GenericLauncher.Tests/Database/LauncherDatabaseTest.cs` now verifies that `SetMinecraftInstanceClassPathAsync(...)` updates only the stored `ClassPath`.

All planned validation commands succeeded on 2026-04-03 11:25 CEST:

- `dotnet build LavaLauncher.sln`
- `dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Minecraft"`
- `dotnet test LavaLauncher.sln`

The full solution test run passed with 214 tests, so the earlier repository note about missing test discovery should be treated as stale. Documentation in `mod_launchers.md` now explicitly states that mod-loader libraries override vanilla libraries on logical collisions and that unparseable entries fall back to full-path deduping.

## Context and orientation

The relevant code lives in the shared Minecraft launch and mod-loader layers. `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs` owns instance installation and launch preparation. The shared method `ApplyModLoaderToVersion(...)` currently merges vanilla and mod-loader libraries into one classpath. That is the architectural choke point for this change.

`GenericLauncher.Shared/Minecraft/MinecraftVersionManager.cs` builds the vanilla classpath from Mojang's version manifest. Its output is a `List<string>` of absolute jar paths rooted under the shared Minecraft libraries folder. That vanilla classpath is currently correct on its own and should remain the source of truth for vanilla-only launches.

`GenericLauncher.Shared/Minecraft/ModLoaders/ResolvedModLoaderLibrary.cs` is the common record used by Fabric, Forge, and NeoForge loader services to describe launch libraries. `GenericLauncher.Shared/Minecraft/ModLoaders/Fabric/FabricModLoaderService.cs`, `GenericLauncher.Shared/Minecraft/ModLoaders/Forge/ForgeModLoaderService.cs`, and `GenericLauncher.Shared/Minecraft/ModLoaders/NeoForge/NeoForgeModLoaderService.cs` all produce these records, but none of them should own the precedence policy against vanilla. They should keep returning loader libraries, and the shared launch layer should decide how those libraries override or coexist with vanilla.

Persisted instances are stored through `GenericLauncher.Shared/Database/LauncherRepository.cs` and `GenericLauncher.Shared/Database/LauncherDatabase.cs`, with the classpath saved in the `MinecraftInstance.ClassPath` field defined in `GenericLauncher.Shared/Database/Model/MinecraftInstance.cs`. Because old installs may already have broken merged classpaths persisted, launch-time normalization must work both for newly installed instances and for previously stored ones.

Repository documentation relevant to this change already exists in `mod_launchers.md` and under `docs/`. This plan assumes a short project-level note will be added to one of those files so future work on loader support preserves the rule that mod-loader libraries override vanilla on logical collisions.

## Plan of work

Stage A is a focused refactor of the shared classpath merge logic. Add a new internal helper near the Minecraft launch or mod-loader shared layer with two responsibilities: derive a logical library key from a Maven-style jar path, and merge two classpath sequences using stable order with last-wins replacement on logical collisions. The helper must preserve unrelated entry order, preserve classifier distinctions, and fall back to full-path identity when parsing is uncertain.

Stage B is wiring. Replace the current path-based `Distinct` merge in `ApplyModLoaderToVersion(...)` with the shared helper. Then add launch-time normalization for persisted instance classpaths before placeholders are expanded into the final `-cp` argument. This stage must also add a small repository/database method that updates only the stored `ClassPath` for an instance when launch-time normalization repairs it. No schema change is allowed.

Stage C is documentation. Add concise code comments at the helper or merge call site stating that mod-loader libraries win over vanilla libraries on logical identity collisions. Add a short project-level documentation update, most likely in `mod_launchers.md`, describing the same rule in loader-agnostic terms so future loader implementations do not accidentally reintroduce path-based merging.

Stage D is validation. Add targeted tests for logical identity extraction, generic merge semantics, persisted-classpath repair, and loader-shaped regression coverage. The tests should not depend on live network data. They should use synthetic classpath entries or existing local fixture patterns to demonstrate that only one logical ASM jar survives and that classifier jars remain distinct.

Each stage ends with validation. Do not move from merge wiring to documentation or cleanup until the targeted tests for the merge helper are in place and passing locally, because precedence errors are easy to introduce through ordering mistakes.

## Concrete steps

1. Inspect the current shared merge and persistence code.

   ```plaintext
   rg -n "ApplyModLoaderToVersion|ClassPath|ResolvedModLoaderLibrary|SetMinecraftInstanceStateAsync" GenericLauncher.Shared
   ```

   Expected outcome: locate the shared merge path in `MinecraftLauncher`, the vanilla classpath builder in `MinecraftVersionManager`, and the current repository/database write surface for `MinecraftInstance`.

2. Add the shared classpath identity and merge helper in the Minecraft shared layer, then wire it into `ApplyModLoaderToVersion(...)` and persisted launch normalization.

   ```plaintext
   dotnet build LavaLauncher.sln
   ```

   Expected outcome: the solution builds, proving the helper integration and repository update method compile cleanly.

3. Add or update targeted tests that prove mod-loader precedence and persisted-classpath repair.

   ```plaintext
   dotnet test LavaLauncher.sln --filter "FullyQualifiedName~GenericLauncher.Tests.Minecraft"
   ```

   Expected outcome: the new classpath-related Minecraft tests pass. At minimum there should be coverage showing loader-wins behavior for duplicate logical artifacts and preservation of classifier-specific artifacts.

4. Run broader validation.

   ```plaintext
   dotnet test LavaLauncher.sln
   ```

   Expected outcome: the full solution test command succeeds, or if the environment still exhibits the known test-runner limitation from repository guidance, that limitation is recorded verbatim in `Outcomes & Retrospective`.

5. Update documentation and capture evidence in this ExecPlan.

   ```plaintext
   git diff -- docs/mod-loader-classpath-precedence-execplan.md mod_launchers.md
   ```

   Expected outcome: the diff shows the repository documentation now states that mod-loader libraries override vanilla libraries on logical collisions.

## Validation and acceptance

Acceptance is behavioral, not just structural. The change is complete when the following can be demonstrated:

1. A generic merge of vanilla and mod-loader classpaths keeps only the mod-loader jar when both represent the same logical artifact identity.
2. Classifier-specific artifacts such as natives remain distinct from the base artifact and are not collapsed incorrectly.
3. A previously persisted broken classpath list is normalized before launch, and the repaired list is written back to the database if it changed.
4. Fabric, Forge, and NeoForge-shaped inputs all use the same shared precedence rule without loader-specific merge branches.
5. Repository documentation and code comments both state that mod-loader libraries win over vanilla libraries on logical collisions.

Quality criteria:

- Tests: new targeted Minecraft launch or mod-loader tests pass, including a regression for duplicate ASM-style entries.
- Build: `dotnet build LavaLauncher.sln` succeeds.
- Full validation: `dotnet test LavaLauncher.sln` succeeds, or any pre-existing discoverability limitation is captured exactly rather than hidden.
- Safety: no schema migration is introduced, and no unrelated files in the dirty working tree are reverted.

## Idempotence and recovery

The helper and documentation work are naturally idempotent. Re-running the implementation should not duplicate entries or rewrite documentation inconsistently. Launch-time classpath repair must also be idempotent: once a stored classpath has been normalized and persisted, subsequent launches should detect no further change and skip unnecessary database writes.

If a validation step fails halfway through, the safe recovery path is to keep the helper and tests in sync, fix the failing stage, and rerun the narrowest relevant command first. Do not attempt to repair persisted instances with ad hoc scripts or manual database edits in this change.

## Artifacts and notes

The key evidence to capture during implementation is:

- the old merge code that used path-based `Distinct`;
- the new helper or test proving that `org/ow2/asm/asm/9.6/asm-9.6.jar` and `org/ow2/asm/asm/9.9/asm-9.9.jar` collapse to one logical entry, with the mod loader's entry retained;
- a test or log proving that a persisted mixed classpath is rewritten to the repaired form;
- the documentation snippet that states the precedence rule explicitly.

Concise example of the intended behavior:

```plaintext
vanilla:    .../mc/libraries/org/ow2/asm/asm/9.6/asm-9.6.jar
mod loader: .../mc/modloaders/fabric/libraries/org/ow2/asm/asm/9.9/asm-9.9.jar
result:     .../mc/modloaders/fabric/libraries/org/ow2/asm/asm/9.9/asm-9.9.jar
```

## Interfaces and dependencies

The shared helper should live in the Minecraft shared layer and expose a small internal surface, for example one method that normalizes a classpath sequence and one method that merges vanilla plus mod-loader entries. The final names may differ, but the helper must accept plain classpath strings and optionally loader metadata when needed, because the existing launch path persists classpaths as `List<string>` in `MinecraftInstance`.

No new package dependency is allowed. Reuse existing types:

- `MinecraftVersionManager.Version` in `GenericLauncher.Shared/Minecraft/MinecraftVersionManager.cs` for in-memory launch data.
- `ResolvedModLoaderLibrary` in `GenericLauncher.Shared/Minecraft/ModLoaders/ResolvedModLoaderLibrary.cs` for loader-provided launch libraries.
- `MinecraftInstance` in `GenericLauncher.Shared/Database/Model/MinecraftInstance.cs` for persisted classpaths.
- `LauncherRepository` and `LauncherDatabase` for the new narrow persistence update used by launch-time repair.

The key files expected to change are:

1. `GenericLauncher.Shared/Minecraft/MinecraftLauncher.cs`.
2. One new or updated shared helper file in `GenericLauncher.Shared/Minecraft/` or `GenericLauncher.Shared/Minecraft/ModLoaders/`.
3. `GenericLauncher.Shared/Database/LauncherRepository.cs`.
4. `GenericLauncher.Shared/Database/LauncherDatabase.cs`.
5. `GenericLauncher.Tests/Minecraft/` test files covering classpath merge and normalization.
6. `mod_launchers.md` for the project-level precedence note.

If implementation reveals that one additional shared model helper is needed, keep it internal and local to the Minecraft launch area.

## Revision note

2026-03-31: Created the first draft of this ExecPlan. It generalizes the earlier Fabric-only plan to all current and future mod loaders that use `ResolvedModLoaderLibrary`, adds explicit repository-documentation work so the precedence rule is not implicit, and includes self-healing of already-persisted broken classpaths as part of the implementation scope.
