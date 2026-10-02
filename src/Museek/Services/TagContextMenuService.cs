using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Museek.Services;

/// <summary>A per-user classic Explorer verb with one COM invocation for the entire selection.</summary>
public static class TagContextMenuService
{
    public static Guid CommandClassId { get; } = new("5A7C0D7E-9755-4F2E-ABC2-793EE90650BC");
    private const string Owner = "Museek.EditTags.v1";
    private const string Verb = "Museek.EditTags";
    private const string OwnerValue = "MuseekOwner";
    private const string ExecutableValue = "MuseekExecutable";
    private static readonly object RegistrationLock = new();
    private static string ClassPath => $@"Software\Classes\CLSID\{CommandClassId:B}";
    private static IEnumerable<string> VerbPaths => WindowsIntegrationService.SupportedExtensions
        .Select(extension => $@"Software\Classes\SystemFileAssociations\{extension}\shell\{Verb}");

    // An isolation root is a synthetic HKCU for tests; it never sends a Shell notification.
    public static void Register(string executablePath, RegistryKey? isolationRoot = null)
    {
        var executable = NormalizeExecutable(executablePath, mustExist: true);
        var root = isolationRoot ?? Registry.CurrentUser;
        lock (RegistrationLock)
        {
            var paths = VerbPaths.Append(ClassPath).ToArray();
            var previous = new Dictionary<string, KeySnapshot?>();
            foreach (var path in paths)
            {
                using var key = root.OpenSubKey(path);
                if (key is not null && key.GetValue(OwnerValue) as string != Owner)
                    throw new InvalidOperationException($"The registry key '{path}' belongs to another application.");
                previous[path] = key is null ? null : Capture(key);
            }
            try
            {
                using (var clsid = root.CreateSubKey(ClassPath))
                {
                    SetOwner(clsid, executable);
                    clsid.SetValue("", "Museek tag editor", RegistryValueKind.String);
                    using var server = clsid.CreateSubKey("LocalServer32");
                    server.SetValue("", ServerCommand(executable), RegistryValueKind.String);
                    // COM passes this unquoted path separately as CreateProcess's application name.
                    server.SetValue("ServerExecutable", executable, RegistryValueKind.String);
                }
                foreach (var path in VerbPaths)
                {
                    using var key = root.CreateSubKey(path);
                    SetOwner(key, executable);
                    key.SetValue("", "Edit Tags", RegistryValueKind.String);
                    key.SetValue("MUIVerb", "Edit Tags", RegistryValueKind.String);
                    key.SetValue("Icon", $"\"{executable}\",0", RegistryValueKind.String);
                    key.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);
                    using var command = key.CreateSubKey("command");
                    command.SetValue("", "", RegistryValueKind.String);
                    command.SetValue("DelegateExecute", CommandClassId.ToString("B"), RegistryValueKind.String);
                }
            }
            catch
            {
                // Restore existing Museek registration, including a different installed copy.
                foreach (var (path, snapshot) in previous)
                {
                    root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    if (snapshot is not null)
                    {
                        using var key = root.CreateSubKey(path);
                        Restore(key, snapshot);
                    }
                }
                NotifyShell(isolationRoot);
                throw;
            }
        }
        NotifyShell(isolationRoot);
    }

    /// <summary>No executable means an explicit global opt-out; an executable limits uninstall ownership.</summary>
    public static void Unregister(string? executablePath = null, RegistryKey? isolationRoot = null)
    {
        var executable = executablePath is null ? null : NormalizeExecutable(executablePath, mustExist: false);
        var root = isolationRoot ?? Registry.CurrentUser;
        lock (RegistrationLock)
        {
            foreach (var path in VerbPaths.Append(ClassPath))
            {
                bool owned;
                using (var key = root.OpenSubKey(path)) owned = IsOwned(key, executable);
                if (owned) root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }
        }
        NotifyShell(isolationRoot);
    }

    public static bool IsRegistered(string executablePath, RegistryKey? isolationRoot = null)
    {
        var executable = NormalizeExecutable(executablePath, mustExist: false);
        var root = isolationRoot ?? Registry.CurrentUser;
        lock (RegistrationLock)
        {
            using var clsid = root.OpenSubKey(ClassPath);
            using var server = clsid?.OpenSubKey("LocalServer32");
            if (!IsOwned(clsid, executable) || server is null || server.GetValue("") as string != ServerCommand(executable) ||
                !SamePath(server.GetValue("ServerExecutable") as string, executable)) return false;
            foreach (var path in VerbPaths)
            {
                using var key = root.OpenSubKey(path);
                using var command = key?.OpenSubKey("command");
                if (!IsOwned(key, executable) || key is null || command is null || key.GetValue("MultiSelectModel") as string != "Player" ||
                    command.GetValue("DelegateExecute") as string != CommandClassId.ToString("B") ||
                    command.GetValue("") as string != "") return false;
            }
            return true;
        }
    }

    /// <summary>Call on the App's STA dispatcher for --tag-context-server, and retain until shutdown.</summary>
    public static IDisposable RegisterServer(Action<IReadOnlyList<string>> openEditor, Guid? classId = null)
    {
        ArgumentNullException.ThrowIfNull(openEditor);
        return new ServerRegistration(openEditor, classId ?? CommandClassId);
    }

    private static string NormalizeExecutable(string path, bool mustExist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Contains('"') || !Path.GetFileName(fullPath).Equals("Museek.exe", StringComparison.OrdinalIgnoreCase) ||
            (mustExist && !File.Exists(fullPath)))
            throw new ArgumentException("Context-menu registration requires Museek.exe.", nameof(path));
        return fullPath;
    }

    private static string ServerCommand(string executable) => $"\"{executable}\" --tag-context-server";
    private static bool SamePath(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsOwned(RegistryKey? key, string? executable) => key is not null && key.GetValue(OwnerValue) as string == Owner &&
        (executable is null || SamePath(key.GetValue(ExecutableValue) as string, executable));

    private static void SetOwner(RegistryKey key, string executable)
    {
        key.SetValue(OwnerValue, Owner, RegistryValueKind.String);
        key.SetValue(ExecutableValue, executable, RegistryValueKind.String);
    }

    private sealed record KeySnapshot(Dictionary<string, (object Value, RegistryValueKind Kind)> Values,
        Dictionary<string, KeySnapshot> Children);

    private static KeySnapshot Capture(RegistryKey key)
    {
        var values = key.GetValueNames().ToDictionary(name => name,
            name => (key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name)));
        var children = new Dictionary<string, KeySnapshot>();
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name)!;
            children[name] = Capture(child);
        }
        return new KeySnapshot(values, children);
    }

    private static void Restore(RegistryKey key, KeySnapshot snapshot)
    {
        foreach (var (name, value) in snapshot.Values) key.SetValue(name, value.Value, value.Kind);
        foreach (var (name, child) in snapshot.Children)
        {
            using var childKey = key.CreateSubKey(name);
            Restore(childKey, child);
        }
    }

    private static void NotifyShell(RegistryKey? isolationRoot)
    {
        if (isolationRoot is null) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private sealed class ServerRegistration : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly CommandFactory _factory;
        private uint _cookie;

        public ServerRegistration(Action<IReadOnlyList<string>> openEditor, Guid classId)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("The tag context-menu server requires an STA message pump.");
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2)); // COINIT_APARTMENTTHREADED
            _factory = new CommandFactory(openEditor);
            try
            {
                Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref classId, _factory, 4, 1, out _cookie));
                // CLSCTX_LOCAL_SERVER + REGCLS_MULTIPLEUSE: Explorer can reuse this editor host.
            }
            catch { CoUninitialize(); throw; }
        }

        public void Dispose()
        {
            if (_cookie == 0) return;
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose the tag context-menu server on its registering STA thread.");
            var cookie = _cookie;
            _cookie = 0;
            try { Marshal.ThrowExceptionForHR(CoRevokeClassObject(cookie)); }
            finally { CoUninitialize(); GC.KeepAlive(_factory); }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IClassFactory))]
    public sealed class CommandFactory(Action<IReadOnlyList<string>> openEditor) : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            try { return QueryObject(new TagCommand(openEditor), ref interfaceId, out result); }
            catch (Exception ex) { return Marshal.GetHRForException(ex); }
        }

        public int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked) => 0;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IExecuteCommand))]
    public sealed class TagCommand(Action<IReadOnlyList<string>> openEditor) : IExecuteCommand, IObjectWithSelection
    {
        private IShellItemArray? _selection;
        private bool _noShowUi;

        public int SetSelection(IShellItemArray selection)
        {
            _selection = selection;
            return selection is null ? unchecked((int)0x80070057) : 0;
        }

        public int GetSelection(ref Guid interfaceId, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (_selection is null) return unchecked((int)0x80004005);
            try { return QueryObject(_selection, ref interfaceId, out result); }
            catch (Exception ex) { return Marshal.GetHRForException(ex); }
        }

        public int Execute()
        {
            if (_selection is null || _noShowUi) return unchecked((int)0x80070057);
            try
            {
                Marshal.ThrowExceptionForHR(_selection.GetCount(out var count));
                if (count == 0) return unchecked((int)0x80070057);
                var paths = new string[count];
                for (uint index = 0; index < count; index++)
                {
                    Marshal.ThrowExceptionForHR(_selection.GetItemAt(index, out var item));
                    Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80058000, out var path)); // SIGDN_FILESYSPATH
                    try { paths[index] = Marshal.PtrToStringUni(path) ?? throw new IOException("A selected file has no filesystem path."); }
                    finally { Marshal.FreeCoTaskMem(path); }
                }
                // A single callback preserves every selected path and selection order without command-line limits.
                openEditor(Array.AsReadOnly(paths));
                return 0;
            }
            catch (Exception ex) { return Marshal.GetHRForException(ex); }
        }

        public int SetKeyState(uint keyState) => 0;
        public int SetParameters([MarshalAs(UnmanagedType.LPWStr)] string parameters) => 0;
        public int SetPosition(NativePoint point) => 0;
        public int SetShowWindow(int show) => 0;
        public int SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noShowUi) { _noShowUi = noShowUi; return 0; }
        public int SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory) => 0;
    }

    private static int QueryObject(object value, ref Guid interfaceId, out IntPtr result)
    {
        var unknown = Marshal.GetIUnknownForObject(value);
        try { return Marshal.QueryInterface(unknown, in interfaceId, out result); }
        finally { Marshal.Release(unknown); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey { public Guid Format; public uint Id; }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    [ComImport, Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExecuteCommand
    {
        [PreserveSig] int SetKeyState(uint keyState);
        [PreserveSig] int SetParameters([MarshalAs(UnmanagedType.LPWStr)] string parameters);
        [PreserveSig] int SetPosition(NativePoint point);
        [PreserveSig] int SetShowWindow(int show);
        [PreserveSig] int SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noShowUi);
        [PreserveSig] int SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int Execute();
    }

    [ComImport, Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectWithSelection
    {
        [PreserveSig] int SetSelection(IShellItemArray selection);
        [PreserveSig] int GetSelection(ref Guid interfaceId, out IntPtr result);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetPropertyStore(uint flags, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetPropertyDescriptionList(ref PropertyKey key, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetAttributes(uint flags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem item);
        [PreserveSig] int EnumItems(out IntPtr enumerator);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid classId, [MarshalAs(UnmanagedType.IUnknown)] object factory,
        uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
