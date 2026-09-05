using System.Reflection;
using System.Runtime.InteropServices;
using FuseDotNet;

namespace Shoko.VFS.FUSE.Fuse;

internal sealed class FuseLibraryUnavailableException()
    : InvalidOperationException("The FUSE native library resolver is unavailable.");

/// <summary>Resolves FuseDotNet's logical fuse3 import to a versioned Linux runtime.</summary>
internal sealed class FuseNativeLibraryResolver
{
    internal delegate bool LibraryLoader(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath,
        out IntPtr handle);

    private static readonly string[] Candidates = ["libfuse3.so.3", "libfuse3.so.4", "libfuse3.so"];
    private readonly Func<bool> _isLinux;
    private readonly LibraryLoader _loader;
    private readonly Action<Assembly, DllImportResolver> _register;
    private readonly object _lock = new();
    private IntPtr _loadedHandle;
    private int _registrationState;

    internal FuseNativeLibraryResolver(
        Func<bool> isLinux,
        LibraryLoader loader,
        Action<Assembly, DllImportResolver>? register = null)
    {
        _isLinux = isLinux;
        _loader = loader;
        _register = register ?? NativeLibrary.SetDllImportResolver;
    }

    internal void Register(Assembly assembly)
    {
        if (!_isLinux())
            return;

        lock (_lock)
        {
            if (_registrationState == 1)
                return;
            if (_registrationState == 2)
                throw new FuseLibraryUnavailableException();

            try
            {
                _register(assembly, Resolve);
                _registrationState = 1;
            }
            catch (InvalidOperationException)
            {
                // NativeLibrary.SetDllImportResolver has no composition API.
                _registrationState = 2;
                throw new FuseLibraryUnavailableException();
            }
        }
    }

    internal IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!_isLinux() || !string.Equals(libraryName, "fuse3", StringComparison.Ordinal))
            return IntPtr.Zero;

        lock (_lock)
        {
            if (_loadedHandle != IntPtr.Zero)
                return _loadedHandle;

            foreach (string candidate in Candidates)
            {
                if (_loader(candidate, assembly, searchPath, out IntPtr handle) && handle != IntPtr.Zero)
                {
                    // The handle is intentionally retained for the process lifetime.
                    _loadedHandle = handle;
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    private static readonly FuseNativeLibraryResolver ProcessResolver = new(
        OperatingSystem.IsLinux,
        NativeLibrary.TryLoad);

    internal static void EnsureRegistered()
    {
        if (OperatingSystem.IsLinux())
            ProcessResolver.Register(typeof(FuseService).Assembly);
    }
}
