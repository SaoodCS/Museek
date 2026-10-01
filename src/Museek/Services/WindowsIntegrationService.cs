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

    public static void Register(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath) ||
            !string.Equals(Path.GetFileName(fullPath), "Museek.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Registration requires an existing Museek.exe.", nameof(executablePath));
        }

        var command = $"\"{fullPath}\" \"%1\"";
        var icon = $"\"{fullPath}\",0";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
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

        using (var application = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Museek.exe"))
        {
            application.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
            using var openCommand = application.CreateSubKey(@"shell\open\command");
            openCommand.SetValue("", command, RegistryValueKind.String);
            using var supportedTypes = application.CreateSubKey("SupportedTypes");
            foreach (var extension in SupportedExtensions)
                supportedTypes.SetValue(extension, "", RegistryValueKind.String);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
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
            using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(ApplicationName, CapabilitiesPath, RegistryValueKind.String);

        // This marker lets the uninstaller preserve registration belonging to another copy.
        using (var application = Registry.CurrentUser.CreateSubKey(@"Software\Museek"))
            application.SetValue("ExecutablePath", fullPath, RegistryValueKind.String);

        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
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
