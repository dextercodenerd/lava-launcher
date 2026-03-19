# Architecture

This document describes how the launcher currently works in code. It is intentionally implementation-oriented and reflects the current state of the repository rather than an idealized future design.

Unless stated otherwise, paths below are relative to the launcher data root exposed as `LauncherPlatform.AppDataPath`. The concrete root depends on platform:

- Windows: `%LocalAppData%/<WindowsFolderName>`
- macOS: `~/Library/Application Support/<bundle-id>`
- Linux: `Environment.SpecialFolder.LocalApplicationData/<LinuxFolderName>`

## Vanilla Minecraft

Vanilla Minecraft install and launch data is managed primarily by `MinecraftVersionManager` and orchestrated by `MinecraftLauncher`.

`MinecraftVersionManager` uses the launcher-style `mc/` subtree under the app data root:

- `mc/versions/version_manifest_v2.json`
  - Cached Mojang version manifest.
- `mc/versions/<versionId>/<versionId>.json`
  - Cached vanilla version metadata downloaded from Mojang.
- `mc/versions/<versionId>/<versionId>.jar`
  - Vanilla client jar for the selected Minecraft version.
- `mc/versions/<versionId>/natives/`
  - Per-version extracted native libraries.
- `mc/assets/`
  - Shared asset indexes and downloaded asset objects.
- `mc/libraries/`
  - Shared Java libraries for Minecraft and mod loaders.

When a user creates an instance, `MinecraftLauncher.CreateInstance(...)` performs the high-level flow:

1. Creates an instance working directory under `instances/<instanceFolder>`.
2. Resolves the selected mod loader, even for vanilla.
3. Downloads the selected vanilla version metadata through `MinecraftVersionManager.DownloadVersionAsync(...)`.
4. Builds a launch snapshot from the vanilla version data.
5. Persists that snapshot into SQLite as a `MinecraftInstance`.
6. Downloads the vanilla client jar, assets, shared libraries, native libraries, Java runtime, and any mod-loader-specific libraries.
7. Marks the instance as ready.

The persisted `MinecraftInstance` is not only a user-visible record. It stores the launch-critical snapshot used later for running the game:

- `VersionId`
- `LaunchVersionId`
- `RequiredJavaVersion`
- `ClientJarPath`
- `MainClass`
- `AssetIndex`
- `ClassPath`
- `GameArguments`
- `JvmArguments`

The instance-native runtime folder is derived from the instance folder rather than persisted separately:

- `Path.Combine(instancesRoot, instance.Folder, "natives")`

At launch time, `MinecraftLauncher.LaunchInstance(...)`:

1. Authenticates the selected account.
2. Loads cached vanilla version details using `instance.VersionId`.
3. Rebuilds a launch model by replacing the cached vanilla fields with the persisted instance snapshot.
4. Uses `instances/<instanceFolder>` as the Java process working directory.
5. Builds the final Java command line.
6. Launches Java with `CliWrap`.

The classpath is built from:

- the instance `ClientJarPath`, followed by
- every relative library path from `ClassPath`, resolved under `mc/libraries/`

This means vanilla launching is driven by a combination of:

- cached Mojang metadata under `mc/versions/<versionId>/`
- shared jars under `mc/libraries/`
- per-instance working directory under `instances/`
- persisted launch snapshot in SQLite

## Mod Loader Overlays

The launcher models vanilla and modded instances with the same base shape. Mod loaders do not replace the whole pipeline. Instead, a mod loader resolves an overlay that is applied on top of the vanilla Minecraft version data.

The common contract is `IModLoaderService`, which exposes:

- `GetLoaderVersionsAsync(...)`
- `ResolveAsync(...)`
- `DownloadAsync(...)`

`ResolveAsync(...)` returns a `ResolvedModLoaderVersion`, which contains the mod-loader-specific overlay data:

- `LaunchVersionId`
- `LoaderVersionId`
- optional `MainClassOverride`
- `ExtraJvmArguments`
- `ExtraGameArguments`
- additional `Libraries`

`MinecraftLauncher.ApplyModLoaderToVersion(...)` merges this overlay into the base vanilla version:

- appends mod-loader libraries to the vanilla classpath entries
- appends extra game arguments
- appends extra JVM arguments
- overrides the main class when the mod loader provides one

The result is a merged launch snapshot. That merged snapshot is what gets stored in `MinecraftInstance` and later reused during launch.

This is an important architectural point: the launcher does not mutate Mojang's cached version JSON into a new derived version file. Instead, it keeps Mojang's vanilla data cached as-is and persists the merged launch state separately in the database.

`LaunchVersionId` is persisted separately from `VersionId` for the same reason. The base version remains the vanilla Minecraft version used to load cached Mojang metadata, while the launch-visible version name can be changed by the mod loader. For vanilla these are typically the same; for Fabric they are not.

Vanilla itself is implemented as a no-op overlay:

- `LaunchVersionId` is the vanilla Minecraft version id
- no extra libraries are added
- no arguments are added
- no main class override is applied

## Fabric

Fabric is implemented by `FabricModLoaderService` and wired in `App.axaml.cs`.

The launcher separates Fabric-owned data from vanilla-owned data:

- `mc/modloaders/fabric/metadata/`
  - Loader metadata cache such as `loader_versions.json`.
- `mc/modloaders/fabric/versions/<launchVersionId>/profile.json`
  - The canonical raw Fabric launcher profile used to resolve a concrete launch version.
- `mc/modloaders/fabric/libraries/...`
  - Fabric-owned Java libraries downloaded from Fabric's Maven coordinates.

Vanilla-owned data remains outside the Fabric folder:

- `mc/versions/<versionId>/<versionId>.jar`
  - Vanilla client jar.
- `mc/libraries/...`
  - Vanilla shared Java libraries.
- `mc/versions/<versionId>/natives`
  - Cached extracted vanilla native libraries for that Minecraft version.

Each instance gets its own runtime-native folder:

- `instances/<instanceFolder>/natives`
  - A disposable runtime copy of the cached vanilla native libraries.

When resolving Fabric for a Minecraft version, `FabricModLoaderService.ResolveAsync(...)`:

1. Loads cached or remote Fabric loader versions from `https://meta.fabricmc.net/v2/versions/loader`.
2. Selects the requested loader version, or the first stable one.
3. Builds a combined launch version id in the form `fabric-loader-<loaderVersion>-<minecraftVersion>`.
4. Downloads the Fabric launcher profile JSON and stores it once as `versions/<launchVersionId>/profile.json`.
5. Parses the profile for:
   - `mainClass`
   - string-only JVM arguments
   - string-only game arguments
   - library Maven coordinates
6. Resolves Fabric library file paths under `mc/modloaders/fabric/libraries/...`.
7. Returns the resolved overlay as `ResolvedModLoaderVersion`.

The final Fabric launch path works like this:

1. Download or reuse cached vanilla Minecraft metadata, jars, assets, libraries, and version natives.
2. Recreate `instances/<instanceFolder>/natives` by copying files from `mc/versions/<versionId>/natives`.
3. Resolve the Fabric profile into overlay data.
4. Merge Fabric into the vanilla launch model:
   - replace main class with Fabric's main class
   - append Fabric JVM arguments
   - append Fabric game arguments
   - append Fabric library jar paths to the classpath
5. Persist the merged result into the instance record.
6. Launch Java against:
   - the vanilla client jar
   - the merged classpath containing both vanilla and Fabric jar paths
   - the instance-local `natives` folder
   - the Fabric-provided main class and arguments

Fabric is still launched as an overlay on top of vanilla Minecraft, not as a separately executed Fabric-specific launcher jar. The Fabric-specific folder stores the raw profile and Fabric-owned Java libraries, while vanilla still provides the client jar and cached native binaries.
