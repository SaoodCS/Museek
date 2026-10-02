using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Museek.Services;

/// <summary>Registers Museek as an available handler without changing the user's defaults.</summary>
public static class WindowsIntegrationService
{
    private const string ApplicationName = "Museek";
    private const string ProgId = "Museek.Audio";
    private const string CapabilitiesPath = @"Software\Museek\Capabilities";

    // One list supplies file pickers, command-line filtering, and Windows registration.
    // Individual files can still use an unsupported codec or DRM inside a supported container.
    public static IReadOnlyList<string> SupportedExtensions { get; } = Array.AsReadOnly(new[]
    {
        ".mp3", ".flac", ".wav", ".wave", ".m4a", ".m4b", ".m4r", ".aac", ".alac",
        ".ogg", ".oga", ".opus", ".spx", ".wma", ".aif", ".aiff", ".aifc", ".ape",
        ".wv", ".tta", ".tak", ".mpc", ".mpp", ".mp+", ".mp1", ".mp2", ".ac3",
        ".eac3", ".dts", ".amr", ".awb", ".au", ".snd", ".caf", ".mka", ".weba",
        ".dsf", ".dff"
    });

    // An isolation root represents a synthetic HKCU for tests and never notifies Explorer.
    public static void Register(string executablePath, RegistryKey? isolationRoot = null)
    {
        var fullPath = NormalizeExecutable(executablePath, mustExist: true);
        var root = isolationRoot ?? Registry.CurrentUser;
        var command = $"\"{fullPath}\" \"%1\"";
        var icon = $"\"{fullPath}\",0";

        using (var progId = root.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "Museek audio file", RegistryValueKind.String);
            progId.SetValue("FriendlyTypeName", "Museek audio file", RegistryValueKind.String);
            using var defaultIcon = progId.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue("", icon, RegistryValueKind.String);
            using var open = progId.CreateSubKey(@"shell\open");
            open.SetValue("", "Play with Museek", RegistryValueKind.String);
            using var openCommand = open.CreateSubKey("command");
            openCommand.SetValue("", command, RegistryValueKind.String);
        }

        using (var application = root.CreateSubKey(@"Software\Classes\Applications\Museek.exe"))
        {
            application.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
            using var openCommand = application.CreateSubKey(@"shell\open\command");
            openCommand.SetValue("", command, RegistryValueKind.String);
            using var supportedTypes = application.CreateSubKey("SupportedTypes");
            foreach (var extension in SupportedExtensions)
                supportedTypes.SetValue(extension, "", RegistryValueKind.String);
        }

        using (var capabilities = root.CreateSubKey(CapabilitiesPath))
        {
            capabilities.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
            capabilities.SetValue("ApplicationDescription", "Play your local music and audio files with Museek.", RegistryValueKind.String);
            capabilities.SetValue("ApplicationIcon", icon, RegistryValueKind.String);
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var extension in SupportedExtensions)
                associations.SetValue(extension, ProgId, RegistryValueKind.String);
        }

        foreach (var extension in SupportedExtensions)
        {
            using var openWith = root.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var registered = root.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(ApplicationName, CapabilitiesPath, RegistryValueKind.String);

        // This marker lets the uninstaller preserve registration belonging to another copy.
        using (var application = root.CreateSubKey(@"Software\Museek"))
            application.SetValue("ExecutablePath", fullPath, RegistryValueKind.String);

        NotifyShell(isolationRoot);
    }

    /// <summary>Removes only associations owned by this Museek executable, preserving user defaults.</summary>
    public static void Unregister(string executablePath, RegistryKey? isolationRoot = null)
    {
        var fullPath = NormalizeExecutable(executablePath, mustExist: false);
        var root = isolationRoot ?? Registry.CurrentUser;
        var command = $"\"{fullPath}\" \"%1\"";
        var ownsProgId = HasValue(root, $@"Software\Classes\{ProgId}\shell\open\command", "", command);
        var ownsApplication = HasValue(root, @"Software\Classes\Applications\Museek.exe\shell\open\command", "", command);
        var ownsCapabilities = HasValue(root, @"Software\Museek", "ExecutablePath", fullPath);

        if (ownsProgId)
        {
            // Custom extensions may have gained our ProgID after installation. Leave each extension
            // and every other application's entries intact, including default and UserChoice values.
            using var classes = root.OpenSubKey(@"Software\Classes", writable: true);
            if (classes is not null)
                foreach (var extension in classes.GetSubKeyNames())
                {
                    if (!extension.StartsWith('.')) continue;
                    using var openWith = classes.OpenSubKey($@"{extension}\OpenWithProgids", writable: true);
                    openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                }
            root.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        }

        if (ownsApplication)
            root.DeleteSubKeyTree(@"Software\Classes\Applications\Museek.exe", throwOnMissingSubKey: false);

        if (ownsCapabilities)
        {
            using (var registered = root.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                if (SameValue(registered?.GetValue(ApplicationName), CapabilitiesPath))
                    registered!.DeleteValue(ApplicationName, throwOnMissingValue: false);
            root.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
            var empty = false;
            using (var application = root.OpenSubKey(@"Software\Museek", writable: true))
            {
                if (application is not null)
                {
                    application.DeleteValue("ExecutablePath", throwOnMissingValue: false);
                    empty = application.SubKeyCount == 0 && application.ValueCount == 0;
                }
            }
            if (empty) root.DeleteSubKey(@"Software\Museek", throwOnMissingSubKey: false);
        }
        NotifyShell(isolationRoot);
    }

    private static string NormalizeExecutable(string executablePath, bool mustExist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Contains('"') ||
            !Path.GetFileName(fullPath).Equals("Museek.exe", StringComparison.OrdinalIgnoreCase) ||
            (mustExist && !File.Exists(fullPath)))
            throw new ArgumentException(mustExist ? "Registration requires an existing Museek.exe." :
                "Unregistration requires the path of Museek.exe.", nameof(executablePath));
        return fullPath;
    }

    private static bool HasValue(RegistryKey root, string keyPath, string valueName, string expected)
    {
        using var key = root.OpenSubKey(keyPath);
        return SameValue(key?.GetValue(valueName), expected);
    }

    private static bool SameValue(object? value, string expected) => value is string text &&
        text.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void NotifyShell(RegistryKey? isolationRoot)
    {
        if (isolationRoot is null) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    public static void OpenDefaultAppsSettings()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(ApplicationName),
            UseShellExecute = true
        });
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
