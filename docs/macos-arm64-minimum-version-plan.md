# Platform-Specific Minimum Version Plan

## Summary
- Set a platform-specific Minecraft floor instead of trying to preserve old-version support on Apple Silicon.
- New decision: `macOS + arm64` will only support Minecraft `1.19+`.
- Reason: `1.18` and `1.18.2` already require Java 17, but their official metadata still uses LWJGL `3.2.1/3.2.2` and has no `natives-macos-arm64`. `1.19` is the first stable release whose official metadata shows Java 17 plus LWJGL `3.3.1` with `natives-macos-arm64`.
- Rosetta is out of scope. Do not build or preserve an x64-on-arm64 compatibility path for macOS.

## Version Floor Matrix
| Platform | Minimum supported MC version | Policy |
| --- | --- | --- |
| macOS arm64 | `1.19` | Supported |
| macOS x64 | unchanged | Keep existing legacy support behavior |
| Windows x64 | unchanged | Keep existing legacy support behavior |
| Linux x64 | unchanged | Keep existing legacy support behavior |
| Windows arm64 | unchanged in this plan | Separate audit later if needed |
| Linux arm64 | unchanged in this plan | Separate audit later if needed |

## Key Changes
- Add a `MinecraftPlatformSupportPolicy` that evaluates `(os, hostArch, mcVersion)` and returns `Supported` or `Unsupported(minVersion, reason)`.
- Gate instance creation before Java install or Minecraft download. On `macOS arm64`, versions `<1.19` must fail early with a clear UI message.
- Keep Java runtime resolution platform-aware:
  - `macOS arm64` only needs official ARM64 Java 17/21 lanes for supported versions.
  - Do not attempt Java 8 or Java 16 installs for unsupported macOS ARM64 versions.
  - Other platforms keep existing Java version logic.
- Keep native selection architecture-aware, but remove the need for mixed-architecture fallback on `macOS arm64`.
- Surface the same policy in all relevant UI entry points:
  - new instance install
  - instance details / relaunch of existing copied instances
  - any future version picker or import flow
- Keep a small metadata-backed compatibility audit for macOS ARM64 so the floor remains explicit and testable.

## Public Interfaces / Types
- Add a support-policy type with at least:
  - `PlatformSupportResult`
  - `IsSupported`
  - `MinimumSupportedVersion`
  - `Reason`
- Add a platform-key type or equivalent input to policy evaluation:
  - `Os`
  - `Architecture`
- Make install/launch paths depend on the support policy before calling Java resolution.

## Test Plan
- Unit test policy results:
  - `macOS arm64 + 1.17.1 => unsupported, min=1.19`
  - `macOS arm64 + 1.18.2 => unsupported, min=1.19`
  - `macOS arm64 + 1.19 => supported`
  - `macOS x64 + 1.18.2 => unchanged/allowed`
  - `Windows x64 + 1.17.1 => unchanged/allowed`
- Unit test that unsupported `macOS arm64` versions never call Java 8/16 resolution.
- UI test or view-model test that unsupported installs show a concrete message instead of crashing.
- Manual validation on Apple Silicon:
  - `1.18.2` is blocked before download/install
  - `1.19` installs with ARM64 Java 17 and launches
  - current latest stable still works as control

## Assumptions And Defaults
- Stable release floor matters more than snapshot support; use `1.19`, not a 22w snapshot, as the public minimum.
- “Full support” for `macOS arm64` means:
  - official ARM64-capable Java 17+
  - official Minecraft metadata with macOS ARM64 natives
  - no Rosetta dependency
- As of March 31, 2026, official Temurin ARM64 builds are available for Java 17 and 21, so `macOS arm64` can stay on a clean ARM64 JDK path for supported versions.

## Evidence
- Official Minecraft `1.18.json`: Java 17, LWJGL `3.2.1/3.2.2`, no `natives-macos-arm64`
  - <https://piston-meta.mojang.com/v1/packages/7367ea8b7cad7c7830192441bb2846be0d2ceeac/1.18.json>
- Official Minecraft `1.18.2.json`: Java 17, LWJGL `3.2.1/3.2.2`, no `natives-macos-arm64`
  - <https://piston-meta.mojang.com/v1/packages/334b33fcba3c9be4b7514624c965256535bd7eba/1.18.2.json>
- Official Minecraft `1.19.json`: Java 17, LWJGL `3.3.1`, includes `natives-macos-arm64`
  - <https://piston-meta.mojang.com/v1/packages/14bbfb25fb1c1c798e3c9b9482b081a78d1f3a9d/1.19.json>
- Official Adoptium API showing macOS ARM64 JDK availability for Java 17 and 21
  - <https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=aarch64&image_type=jdk&os=mac&vendor=eclipse>
  - <https://api.adoptium.net/v3/assets/latest/21/hotspot?architecture=aarch64&image_type=jdk&os=mac&vendor=eclipse>
