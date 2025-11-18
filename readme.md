# Lava Launcher
A work-in-progress, multi-platform, free, and open-source (GPLv3) Minecraft launcher with simple controls for kids and non-tech parents.

This project is in exploration phase and the code and overall structure is changing frequently. Database schema also evolves and there won't be schema migrations any time soon, so the DB is dropped all the time. There is almost no UI and capabilities, so don't report any issues at the moment please.

Besides easy-to-use UI, we also aim at performance, so the launcher is built using C#'s native ahead-of-time (AOT) compilation that produces native binaries on all platforms. Currently tested on Windows, but Linux (Debian) and macOS support is planned too. Multiplatform GUI is made with _Avalonia UI_ and code is C# with .NET 10.

It is called Lava Launcher, but only _GenericLauncher_ project will be available in the future, because the name is not public domain. It will have the same capabilities, but not the branding, so forks will not imitate the original. The open-source part also doesn't contain the Azure Entra ID app id required for Minecraft/Microsoft login.

Below are some notes about how and why we are doing things at the moment. Some boring developer staff.

## Azure Entra ID App Client id
_Mojang/Microsoft_ reviews every new _Azure Entra ID_ app id, before it can use Minecraft APIs like to get an access token for running Minecraft with an authenticated user. Because of this, the client id is not yet included in the repository and is injected into code via `user.props` file, command line parameter, or ENV variable.

In your fork, you have to set your own Azure Entra ID app client id for the Microsoft/Minecraft login to work. Copy the `user.example.props` as `user.props` file and fill in the client id, redirect URL and app name.

## App version
The app version is defined in the `Directory.Build.props` file in the root folder.

## Logging
We are using the plain `ILogger` interface from `Microsoft.Extensions.Logging`. It is not possible to use the generic `ILogger<T>` version in AOT builds, because that is not native-AOT friendly.

## Library versions
Most of the library versions are defined in the `Directory.Build.props` file in the root folder. Mainly the ones, where the same version is used across multiple dependencies, like _Avalonia UI_.

## Database
Originally, we tried the light-weight [Dapper.AOT](https://github.com/DapperLib/DapperAOT) micro-ORM library for type-safe model. In the end we had to abandon it because of no support for AOT-friendly `TypeHandlers` for custom types conversions.

We also looked at [Nanorm](https://github.com/DamianEdwards/Nanorm), but it looks abandoned already.

We also tried the _EntityFramework_, but there were too many problems with native-AOT build, because their support is just experimental. Like compilation problems, manual steps to generate compiled models, manual steps and platform-specific shell scripts to generate and run SQL files for migrations, which are incompatible with trimming and AOT. And _EF_ itself added ~24 MiB to the final native assembly, where _Dapper_ with SQLite added just ~6 MiB.

### ADO.NET
Dapper.AOT doesn't support custom `TypeHandlers` so we have our own Dapper-like wrapper around the raw ADO.NET with SQLite provider. It adds 600 KiB over the original size of Dapper, weird, but true.

### SQLite thread-safety
SQLite allows only one writer thread, and we are using a custom `AsyncRwLock` for that. Originally we had a `ThreadSafeSqliteConnection`, that inherited and wrapped all the _Dapper_ functions, but that didn't work with AOT compilation. _Dapper.AOT_ doesn't support calling their extension methods with generic parameters, only with explicit types, thus we handle the thread-safety in the database class directly.

We aren't using Dapper anymore, because of its own AOT problems, by we won't be switching back. Ideally, we will update the code to not block readers, when there is an active write, because that is fine in SQLite's WAL mode.

## Native AOT
The final published executable, and also a release build, are native AOT compiled and trimmed. Debug builds are not AOT compiled, nor trimmed. This is mainly for _Avalonia UI_ live preview, that requires reflection and dynamic features, but also for faster build-run feedback loop.

## Trimming
Release & publish builds are trimmed, and we aim at zero warnings even for the publishing step. Some libraries are not fully AOT & trimming compatible and show some warnings, so we disabled some warnings globally, that are verified safe.

Currently disabled:
* `IL2104` trimming warning in `Directory.Build.props` file through `<NoWarn>$(NoWarn);IL2104</NoWarn>` because the SQLite library is producing it when publishing, but it is safe. This disables the warning globally though, so remove this `NoWarn` from time-to-time, and check if they fixed it.

Ideally we don't want to disable warnings globally and aim for warning-free build and publish. So remove the `NoWarn` exceptions after updating libraries, if they fixed these warnings. 

## Publish
Run the `dotnet publish` command below to create a Windows x64 application binary. It explicitly enables AOT, because it is disabled in `.csproj` for the _Avalonia UI_ preview plugin to work.

```sh
dotnet publish .\LavaLauncher.Desktop\LavaLauncher.Desktop.csproj -c Release -r win-x64 -p:PublishAot=true
```

and find the output in the `LavaLauncher\LavaLauncher.Desktop\bin\Release\net9.0\win-x64\publish\` folder.

# Disclosure
__NOT AN OFFICIAL MINECRAFT SERVICE. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT__
