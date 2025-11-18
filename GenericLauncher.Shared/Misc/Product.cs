using System;
using System.Reflection;

namespace GenericLauncher.Misc;

public static class Product
{
    private static readonly Lazy<Assembly?> _entryAssembly = new(Assembly.GetEntryAssembly);

    private static readonly Lazy<string> _name = new(() =>
        _entryAssembly.Value?.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "");

    private static readonly Lazy<string> _version = new(() =>
        _entryAssembly.Value?.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "");

    private static readonly Lazy<string> _assemblyName = new(() =>
        _entryAssembly.Value?.GetName().Name ?? "");

    private static readonly Lazy<string> _currentOs = new(() =>
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
    });

    public static string Name
    {
        get => _name.Value;
    }

    public static string Version
    {
        get => _version.Value;
    }

    public static string AssemblyName
    {
        get => _assemblyName.Value;
    }

    public static string CurrentOs
    {
        get => _currentOs.Value;
    }
}
