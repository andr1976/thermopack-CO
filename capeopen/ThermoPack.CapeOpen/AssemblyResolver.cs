using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ThermoPack.CapeOpen;

/// <summary>
/// Hooks AppDomain.AssemblyResolve and pre-loads native DLLs from
/// the managed assembly's directory.
/// </summary>
public static class AssemblyResolver
{
    private static bool _initialized;
    private static string? _baseDir;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";

        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

        // Pre-load native DLL if on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var nativeDll = Path.Combine(_baseDir, "thermopack.dll");
            if (File.Exists(nativeDll))
                LoadLibraryW(nativeDll);
        }
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        if (_baseDir == null) return null;

        var name = new AssemblyName(args.Name).Name;
        if (name == null) return null;

        var path = Path.Combine(_baseDir, name + ".dll");
        if (File.Exists(path))
            return Assembly.LoadFrom(path);

        return null;
    }
}
