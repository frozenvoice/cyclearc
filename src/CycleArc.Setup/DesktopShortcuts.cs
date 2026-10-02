using System.Runtime.InteropServices;

namespace CycleArc.Setup;

/// <summary>
/// The package always creates a Start Menu shortcut. The desktop copy is an install-time
/// choice, and ownership is established by both its exact name and its target.
/// </summary>
internal static unsafe class DesktopShortcuts
{
    private const string LinkName = "CycleArc.lnk";
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterface = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileInterface = new("0000010b-0000-0000-C000-000000000046");

    public static bool IsPresent(string installRoot, string? desktopDirectory = null)
    {
        var link = Path.Combine(DirectoryPath(desktopDirectory, Environment.SpecialFolder.DesktopDirectory), LinkName);
        return IsOwned(link, installRoot);
    }

    public static void Apply(string installRoot, bool enabled, string? desktopDirectory = null,
        string? programsDirectory = null)
    {
        var desktop = DirectoryPath(desktopDirectory, Environment.SpecialFolder.DesktopDirectory);
        var link = Path.Combine(desktop, LinkName);
        var owned = IsOwned(link, installRoot);
        if (!enabled)
        {
            if (owned) File.Delete(link);
            return;
        }

        if (File.Exists(link) && !owned)
            throw new IOException("The desktop already contains a CycleArc shortcut owned by another application or installation.");

        var programs = DirectoryPath(programsDirectory, Environment.SpecialFolder.Programs);
        var source = Path.Combine(programs, LinkName);
        if (!File.Exists(source) || !IsOwned(source, installRoot))
            throw new IOException("The installed CycleArc Start Menu shortcut is missing or points to another installation.");

        Directory.CreateDirectory(desktop);
        // Keep the engine's icon and application identity, but point the desktop entry
        // at the stable launcher rather than its versioned current executable.
        using var shortcut = new ShellLink();
        shortcut.Load(source);
        shortcut.SetPath(Path.Combine(Path.GetFullPath(installRoot), "CycleArc.exe"));
        var staged = Path.Combine(desktop, ".CycleArc-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            shortcut.Save(staged);
            File.Move(staged, link, overwrite: owned);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static bool IsOwned(string link, string installRoot)
    {
        if (!File.Exists(link)) return false;
        var target = TryReadTarget(link);
        if (target is null) return false;
        try
        {
            var root = Path.GetFullPath(installRoot);
            return SamePath(target, Path.Combine(root, "CycleArc.exe"))
                || SamePath(target, Path.Combine(root, "current", "CycleArc.exe"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            if (!Path.IsPathFullyQualified(first) || !Path.IsPathFullyQualified(second)) return false;
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string DirectoryPath(string? specified, Environment.SpecialFolder folder)
    {
        var path = specified ?? Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException($"The {folder} folder has no absolute path.");
        return Path.GetFullPath(path);
    }

    internal static string? TryReadTarget(string link)
    {
        try
        {
            using var shellLink = new ShellLink();
            shellLink.Load(link);
            return shellLink.GetPath();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Used by isolated tests to make genuine .lnk files. Production loads the Velopack
    // link so its icon, working directory and other shell metadata stay intact.
    internal static void CreateLink(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var shellLink = new ShellLink();
        shellLink.SetPath(target);
        shellLink.Save(link);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid classId, nint outer, uint classContext,
        in Guid interfaceId, out nint instance);

    private sealed class ShellLink : IDisposable
    {
        private nint _link;
        private nint _persist;
        private readonly bool _uninitialize;
        private bool _disposed;

        public ShellLink()
        {
            var hr = CoInitializeEx(0, 0); // MTA; an existing STA is equally usable.
            if (hr < 0 && hr != RpcEChangedMode) Marshal.ThrowExceptionForHR(hr);
            _uninitialize = hr >= 0;
            try
            {
                Marshal.ThrowExceptionForHR(CoCreateInstance(in ShellLinkClass, 0, 1,
                    in ShellLinkInterface, out _link));
                var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)_link)[0];
                var iid = PersistFileInterface;
                nint persist = 0;
                Marshal.ThrowExceptionForHR(query(_link, &iid, &persist));
                _persist = persist;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Load(string file)
        {
            var load = (delegate* unmanaged[Stdcall]<nint, char*, uint, int>)(*(nint**)_persist)[5];
            fixed (char* name = file)
                Marshal.ThrowExceptionForHR(load(_persist, name, 0));
        }

        public string GetPath()
        {
            var path = new char[32768];
            var getPath = (delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)(*(nint**)_link)[3];
            fixed (char* buffer = path)
                Marshal.ThrowExceptionForHR(getPath(_link, buffer, path.Length, 0, 4)); // SLGP_RAWPATH
            var end = Array.IndexOf(path, '\0');
            return new string(path, 0, end < 0 ? path.Length : end);
        }

        public void SetPath(string path)
        {
            var setPath = (delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)_link)[20];
            fixed (char* name = path)
                Marshal.ThrowExceptionForHR(setPath(_link, name));
        }

        public void Save(string file)
        {
            var save = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)_persist)[6];
            fixed (char* name = file)
                Marshal.ThrowExceptionForHR(save(_persist, name, 1));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_persist != 0)
            {
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)_persist)[2];
                release(_persist);
                _persist = 0;
            }
            if (_link != 0)
            {
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)_link)[2];
                release(_link);
                _link = 0;
            }
            if (_uninitialize) CoUninitialize();
        }
    }
}
