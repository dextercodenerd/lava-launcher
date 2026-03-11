using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GenericLauncher.Minecraft.Json;
using LavaLauncher;

namespace GenericLauncher.Misc;

public sealed record LauncherPlatform
{
    public string CurrentOs { get; }
    public string Architecture { get; }
    public Version OsVersion { get; }
    public string AppIdentifier { get; }
    public string AppDataPath { get; }

    internal LauncherPlatform(
        string currentOs,
        string architecture,
        Version osVersion,
        string appIdentifier,
        string appDataPath)
    {
        if (string.IsNullOrWhiteSpace(currentOs))
        {
            throw new ArgumentException("OS cannot be empty", nameof(currentOs));
        }

        if (string.IsNullOrWhiteSpace(architecture))
        {
            throw new ArgumentException("Architecture cannot be empty", nameof(architecture));
        }

        if (string.IsNullOrWhiteSpace(appIdentifier))
        {
            throw new ArgumentException("App identifier cannot be empty", nameof(appIdentifier));
        }

        if (string.IsNullOrWhiteSpace(appDataPath))
        {
            throw new ArgumentException("App data path cannot be empty", nameof(appDataPath));
        }

        CurrentOs = currentOs;
        Architecture = architecture;
        OsVersion = osVersion;
        AppIdentifier = appIdentifier;
        AppDataPath = appDataPath;
    }

    public static LauncherPlatform CreateCurrent()
    {
        var currentOs = ResolveCurrentOs();
        var architecture = NormalizeArchitecture(RuntimeInformation.ProcessArchitecture);
        var appIdentifier = currentOs == "osx"
            ? AppConfig.MacBundleIdentifier
            : Product.AssemblyName;
        var appDataRoot = Environment.GetFolderPath(currentOs == "osx"
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appDataRoot))
        {
            throw new InvalidOperationException("Failed to resolve the app data root folder for this platform.");
        }

        return new LauncherPlatform(
            currentOs,
            architecture,
            Environment.OSVersion.Version,
            appIdentifier,
            Path.Combine(appDataRoot, appIdentifier));
    }

    public bool MatchesOs(OsInfo? os)
    {
        if (os is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(os.Name) && !MatchesOsName(os.Name))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(os.Arch) && !MatchesArchitecture(os.Arch))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(os.Version) && !MatchesVersion(os.Version))
        {
            return false;
        }

        return true;
    }

    public bool MatchesOsName(string rawName)
    {
        var normalized = NormalizeOs(rawName);
        return string.Equals(CurrentOs, normalized, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesArchitecture(string rawArchitecture)
    {
        var normalized = NormalizeArchitecture(rawArchitecture);
        return string.Equals(Architecture, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesVersion(string pattern)
    {
        try
        {
            return Regex.IsMatch(OsVersion.ToString(), pattern);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ResolveCurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "osx";
        }

        return "unknown";
    }

    private static string NormalizeOs(string rawName)
    {
        return rawName.Trim().ToLowerInvariant() switch
        {
            "windows" or "win" => "windows",
            "linux" => "linux",
            "mac" or "macos" or "osx" => "osx",
            _ => rawName.Trim().ToLowerInvariant(),
        };
    }

    private static string NormalizeArchitecture(Architecture architecture)
    {
        return architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => architecture.ToString().ToLowerInvariant(),
        };
    }

    private static string NormalizeArchitecture(string rawArchitecture)
    {
        return rawArchitecture.Trim().ToLowerInvariant() switch
        {
            "x64" or "x86_64" or "amd64" => "x64",
            "x86" or "i386" or "i686" => "x86",
            "arm64" or "aarch64" => "arm64",
            "arm" => "arm",
            _ => rawArchitecture.Trim().ToLowerInvariant(),
        };
    }
}
