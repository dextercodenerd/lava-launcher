# Mod Launchers

This document captures the target storage and launch architecture for Fabric and future mod loaders. It intentionally stays narrow: mod-loader native libraries are out of scope for this iteration because the current major loaders are using Java-side artifacts only.

## Ownership Model

Vanilla-owned data stays under `mc/`:

- `mc/versions/<versionId>/<versionId>.json`
  - Cached vanilla version metadata.
- `mc/versions/<versionId>/<versionId>.jar`
  - Vanilla client jar.
- `mc/versions/<versionId>/natives`
  - Cached extracted vanilla native libraries for that Minecraft version.
- `mc/libraries/...`
  - Vanilla shared Java libraries.
- `mc/assets/...`
  - Shared vanilla assets.

Mod-loader-owned data lives under `mc/modloaders/<loader>/`:

- `metadata/`
  - Loader metadata caches such as version lists.
- `versions/<launchVersionId>/profile.json`
  - Canonical raw launcher profile for the resolved launch version.
- `libraries/...`
  - Loader-private Java libraries.

Instance-owned runtime-native data lives under `instances/<instanceFolder>/natives`.

## Native Libraries

The launcher uses a two-level native-libraries model:

1. Vanilla natives are extracted once and cached under `mc/versions/<versionId>/natives`.
2. On instance creation or repair, the launcher recreates `instances/<instanceFolder>/natives` by copying files from that cached vanilla natives folder.
3. Launch uses the instance-local folder as `${natives_directory}`.

This keeps instance cleanup simple while avoiding repeated download and extraction of the same vanilla native archives for each instance.

## Fabric

Fabric is applied as an overlay on top of a vanilla Minecraft version:

1. Resolve the Fabric profile and store it once at `mc/modloaders/fabric/versions/<launchVersionId>/profile.json`.
2. Download Fabric-owned Java libraries into `mc/modloaders/fabric/libraries/...`.
3. Merge Fabric's main class, JVM args, game args, and classpath additions into the vanilla launch snapshot.
4. Persist the merged launch snapshot in the instance record.
5. Launch using:
   - the vanilla client jar
   - vanilla shared libraries
   - Fabric-private libraries
   - the instance-local runtime natives folder

There is no duplicate per-profile JSON copy in `metadata/`.

## Persistence And Launch Snapshot

The persisted `MinecraftInstance` launch snapshot should include:

- `VersionId`
- `LaunchVersionId`
- `ModLoader`
- `ModLoaderVersion`
- `ClientJarPath`
- `MainClass`
- `AssetIndex`
- `ClassPath`
- `GameArguments`
- `JvmArguments`

`ClassPath` stores the launch-time jar paths directly so the launcher does not need to assume that every library comes from one shared libraries root.

The instance-native runtime path is not persisted. It is derived from the instance folder as:

- `Path.Combine(instancesRoot, instance.Folder, "natives")`

## Scope Boundaries

This iteration does not add any architecture for mod-loader native libraries.

If a future loader genuinely needs its own native artifacts, that can be designed separately. For now, the implementation stays focused on:

- loader-private Java libraries
- canonical profile JSON storage
- cached vanilla natives
- per-instance runtime-native materialization
