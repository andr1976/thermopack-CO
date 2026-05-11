using System.Runtime.InteropServices;

namespace ThermoPack.Core;

/// <summary>
/// Dynamically loads the thermopack shared library and resolves Fortran symbols
/// with automatic name-mangling detection (gfortran vs ifort).
/// </summary>
public class ThermoPackLibrary : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    // Name mangling scheme (detected at load time)
    private string _prefix = "";
    private string _module = "";
    private string _postfix = "";
    private string _postfixNoModule = "";

    public bool IsLoaded => _handle != IntPtr.Zero;

    // Windows native methods
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    // Linux native methods
    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr DlOpen(string? fileName, int flags);

    [DllImport("libdl.so.2", EntryPoint = "dlsym")]
    private static extern IntPtr DlSym(IntPtr handle, string symbol);

    [DllImport("libdl.so.2", EntryPoint = "dlclose")]
    private static extern int DlClose(IntPtr handle);

    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr DlError();

    private const int RTLD_NOW = 2;
    private const int RTLD_GLOBAL = 0x100;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Loads the thermopack shared library from the specified directory.
    /// </summary>
    public void Load(string directory)
    {
        if (_handle != IntPtr.Zero) return;

        var candidates = IsWindows
            ? new[] { "thermopack.dll", "libthermopack.dll" }
            : new[] { "libthermopack.so", "libthermopack.dylib" };

        foreach (var name in candidates)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;

            _handle = IsWindows
                ? LoadLibrary(path)
                : DlOpen(path, RTLD_NOW | RTLD_GLOBAL);

            if (_handle != IntPtr.Zero) break;
        }

        if (_handle == IntPtr.Zero)
            throw new DllNotFoundException(
                $"Could not load thermopack library from {directory}. " +
                $"Tried: {string.Join(", ", candidates)}");

        DetectNameMangling();
    }

    /// <summary>
    /// Resolves a module-level Fortran symbol using the detected mangling scheme.
    /// Format: prefix + module + module_separator + method + postfix
    /// </summary>
    public IntPtr GetModuleSymbol(string module, string method)
    {
        var name = _prefix + module + _module + method + _postfix;
        return ResolveSymbol(name);
    }

    /// <summary>
    /// Resolves a non-module Fortran symbol (global function).
    /// </summary>
    public IntPtr GetGlobalSymbol(string method)
    {
        var name = method + _postfixNoModule;
        return ResolveSymbol(name);
    }

    /// <summary>
    /// Resolves a C-bound symbol (BIND(C), no mangling).
    /// </summary>
    public IntPtr GetCSymbol(string name)
    {
        return ResolveSymbol(name);
    }

    /// <summary>
    /// Gets a delegate for a module-level Fortran function.
    /// </summary>
    public T GetModuleDelegate<T>(string module, string method) where T : Delegate
    {
        var ptr = GetModuleSymbol(module, method);
        if (ptr == IntPtr.Zero)
            throw new EntryPointNotFoundException(
                $"Could not find symbol for {module}::{method} " +
                $"(tried: {_prefix}{module}{_module}{method}{_postfix})");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>
    /// Gets a delegate for a C-bound function.
    /// </summary>
    public T GetCDelegate<T>(string name) where T : Delegate
    {
        var ptr = GetCSymbol(name);
        if (ptr == IntPtr.Zero)
            throw new EntryPointNotFoundException($"Could not find C-bound symbol: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private IntPtr ResolveSymbol(string name)
    {
        if (_handle == IntPtr.Zero) return IntPtr.Zero;
        return IsWindows ? GetProcAddress(_handle, name) : DlSym(_handle, name);
    }

    /// <summary>
    /// Detects the Fortran name-mangling scheme by trial and error.
    /// Mirrors map_platform_specifics.py logic.
    /// </summary>
    private void DetectNameMangling()
    {
        string[] prefixes = { "__", "" };
        string[] modules = { "_MOD_", "_mp_", "_" };
        string[] postfixes = { "", "_" };

        const string testModule = "thermopack_var";
        const string testMethod = "add_eos";

        bool found = false;
        foreach (var pre in prefixes)
        {
            foreach (var mod in modules)
            {
                foreach (var post in postfixes)
                {
                    var symbol = pre + testModule + mod + testMethod + post;
                    var ptr = ResolveSymbol(symbol);
                    if (ptr != IntPtr.Zero)
                    {
                        _prefix = pre;
                        _module = mod;
                        _postfix = post;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
            if (found) break;
        }

        if (!found)
            throw new InvalidOperationException(
                "Could not detect Fortran name-mangling scheme in thermopack library.");

        // Detect non-module postfix using a global symbol
        _postfixNoModule = "_"; // Default
        foreach (var post in postfixes)
        {
            var symbol = "thermopack_getkij" + post;
            var ptr = ResolveSymbol(symbol);
            if (ptr != IntPtr.Zero)
            {
                _postfixNoModule = post;
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            if (IsWindows)
                FreeLibrary(_handle);
            else
                DlClose(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
