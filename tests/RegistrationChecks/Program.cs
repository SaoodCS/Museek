using System.Text;
using Microsoft.Win32;
using Museek.Services;

namespace Museek.RegistrationChecks;

internal static class Program
{
    private const string ProgId = "Museek.Audio";
    private const string Capabilities = @"Software\Museek\Capabilities";
    private const string ProgCommand = @"Software\Classes\Museek.Audio\shell\open\command";
    private const string AppCommand = @"Software\Classes\Applications\Museek.exe\shell\open\command";
    private static int _checks;

    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Museek registration checks 音楽 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = MakeExecutable(directory, "first copy & music");
            var second = MakeExecutable(directory, "second copy");
            CheckRegistrationAndUnregister(first);
            CheckOtherCopy(first, second);
            CheckIndependentOwnership(first, second);
            CheckValidation(directory, first);
            Console.WriteLine($"All {_checks} isolated registration checks passed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            // This exact directory was freshly created by this check run.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckRegistrationAndUnregister(string executable)
    {
        using var fixture = new RegistryFixture();
        var root = fixture.Root;
        SeedNeighbors(root);
        WindowsIntegrationService.Register(executable, root);
        var expectedCommand = Command(executable);
        Check(Read(root, ProgCommand, "") as string == expectedCommand, "quoted ProgID command targets this copy");
        Check(Read(root, AppCommand, "") as string == expectedCommand, "Applications command targets this copy");
        Check(Read(root, @"Software\Museek", "ExecutablePath") as string == executable, "owner marker records this copy");
        Check(Read(root, @"Software\RegisteredApplications", "Museek") as string == Capabilities,
            "Museek appears in RegisteredApplications");
        Check(Read(root, Capabilities, "ApplicationName") as string == "Museek", "capabilities name is Museek");
        Check(Read(root, Capabilities, "ApplicationIcon") as string == $"\"{executable}\",0", "capabilities use the installed icon");
        foreach (var extension in WindowsIntegrationService.SupportedExtensions)
        {
            Check(Read(root, $@"{Capabilities}\FileAssociations", extension) as string == ProgId &&
                Read(root, @"Software\Classes\Applications\Museek.exe\SupportedTypes", extension) as string == "" &&
                Read(root, $@"Software\Classes\{extension}\OpenWithProgids", ProgId) is byte[] bytes && bytes.Length == 0,
                $"{extension} is available through capabilities, supported types, and Open With");
        }
        CheckDefaultsAndNeighbors(root);

        // Include an extension Windows or the user associated later, outside our original list.
        Write(root, @"Software\Classes\.customAudio\OpenWithProgids", ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        Write(root, @"Software\Classes\.customAudio\OpenWithProgids", "Other.Custom", "preserve");
        WindowsIntegrationService.Unregister(executable.ToUpperInvariant(), root);
        Check(!Exists(root, @"Software\Classes\Museek.Audio"), "unregister removes this copy's ProgID case-insensitively");
        Check(!Exists(root, @"Software\Classes\Applications\Museek.exe"), "unregister removes this copy's Applications entry");
        Check(!Exists(root, Capabilities), "unregister removes owned capabilities");
        Check(Read(root, @"Software\RegisteredApplications", "Museek") is null, "unregister removes only Museek's registered value");
        Check(Read(root, @"Software\Museek", "ExecutablePath") is null &&
            Read(root, @"Software\Museek", "OtherPreference") as string == "keep",
            "unregister removes the marker while retaining unrelated preference values");
        Check(WindowsIntegrationService.SupportedExtensions.All(extension =>
            Read(root, $@"Software\Classes\{extension}\OpenWithProgids", ProgId) is null),
            "all supported extension Open With values are removed");
        Check(Read(root, @"Software\Classes\.customAudio\OpenWithProgids", ProgId) is null &&
            Read(root, @"Software\Classes\.customAudio\OpenWithProgids", "Other.Custom") as string == "preserve",
            "later custom extension loses only Museek's Open With value");
        CheckDefaultsAndNeighbors(root);
        var after = Snapshot(root);
        WindowsIntegrationService.Unregister(executable, root);
        Check(Snapshot(root) == after, "repeating unregister is harmless");

        using var empty = new RegistryFixture();
        WindowsIntegrationService.Register(executable, empty.Root);
        WindowsIntegrationService.Unregister(executable, empty.Root);
        Check(!Exists(empty.Root, @"Software\Museek"), "the now-empty Museek owner container is removed");
    }

    private static void CheckOtherCopy(string first, string second)
    {
        using var fixture = new RegistryFixture();
        SeedNeighbors(fixture.Root);
        WindowsIntegrationService.Register(first, fixture.Root);
        WindowsIntegrationService.Register(second, fixture.Root);
        Check(Read(fixture.Root, ProgCommand, "") as string == Command(second) &&
            Read(fixture.Root, @"Software\Museek", "ExecutablePath") as string == second,
            "registering an update retargets the handler and owner marker");
        var before = Snapshot(fixture.Root);
        WindowsIntegrationService.Unregister(first, fixture.Root);
        Check(Snapshot(fixture.Root) == before, "uninstalling the old copy preserves the new copy's entire registration");
        WindowsIntegrationService.Unregister(second, fixture.Root);
        Check(!Exists(fixture.Root, @"Software\Classes\Museek.Audio") && !Exists(fixture.Root, Capabilities),
            "the new owner can subsequently unregister itself");
        CheckDefaultsAndNeighbors(fixture.Root);
    }

    private static void CheckIndependentOwnership(string first, string second)
    {
        using (var fixture = new RegistryFixture())
        {
            WindowsIntegrationService.Register(first, fixture.Root);
            Write(fixture.Root, ProgCommand, "", Command(second));
            WindowsIntegrationService.Unregister(first, fixture.Root);
            Check(Read(fixture.Root, ProgCommand, "") as string == Command(second),
                "a different copy's ProgID command survives independently of the main owner marker");
            Check(Read(fixture.Root, @"Software\Classes\.mp3\OpenWithProgids", ProgId) is byte[],
                "Open With entries survive when the ProgID belongs to another copy");
            Check(!Exists(fixture.Root, @"Software\Classes\Applications\Museek.exe") && !Exists(fixture.Root, Capabilities),
                "other entries still owned by this copy are removed");
        }
        using (var fixture = new RegistryFixture())
        {
            WindowsIntegrationService.Register(first, fixture.Root);
            Write(fixture.Root, AppCommand, "", Command(second));
            Write(fixture.Root, @"Software\Museek", "ExecutablePath", second);
            WindowsIntegrationService.Unregister(first, fixture.Root);
            Check(Read(fixture.Root, AppCommand, "") as string == Command(second) && Exists(fixture.Root, Capabilities),
                "a foreign Applications command and capabilities owner remain intact");
            Check(Read(fixture.Root, @"Software\RegisteredApplications", "Museek") as string == Capabilities,
                "foreign capabilities keep their RegisteredApplications value");
            Check(!Exists(fixture.Root, @"Software\Classes\Museek.Audio"), "independently owned ProgID is still removed");
        }
        using (var fixture = new RegistryFixture())
        {
            WindowsIntegrationService.Register(first, fixture.Root);
            Write(fixture.Root, @"Software\RegisteredApplications", "Museek", @"Software\DifferentApp\Capabilities");
            WindowsIntegrationService.Unregister(first, fixture.Root);
            Check(Read(fixture.Root, @"Software\RegisteredApplications", "Museek") as string == @"Software\DifferentApp\Capabilities",
                "a replaced RegisteredApplications value is preserved");
        }
        using (var fixture = new RegistryFixture())
        {
            WindowsIntegrationService.Register(first, fixture.Root);
            fixture.Root.DeleteSubKeyTree(@"Software\Museek");
            WindowsIntegrationService.Unregister(first, fixture.Root);
            Check(!Exists(fixture.Root, @"Software\Classes\Museek.Audio") && !Exists(fixture.Root, @"Software\Classes\Applications\Museek.exe"),
                "missing main owner marker does not prevent command-owned entries from being removed");
            Check(Read(fixture.Root, @"Software\RegisteredApplications", "Museek") as string == Capabilities,
                "missing marker conservatively retains the registered capabilities reference");
        }
    }

    private static void CheckValidation(string directory, string first)
    {
        using var fixture = new RegistryFixture();
        var before = Snapshot(fixture.Root);
        ExpectFailure(() => WindowsIntegrationService.Register("", fixture.Root), "register rejects an empty executable");
        ExpectFailure(() => WindowsIntegrationService.Register(Path.Combine(directory, "Museek.exe"), fixture.Root), "register rejects a missing executable");
        var wrongName = Path.Combine(directory, "Other.exe");
        File.WriteAllText(wrongName, "fixture");
        ExpectFailure(() => WindowsIntegrationService.Register(wrongName, fixture.Root), "register rejects a different application name");
        ExpectFailure(() => WindowsIntegrationService.Unregister(wrongName, fixture.Root), "unregister rejects a different application name");
        Check(Snapshot(fixture.Root) == before, "invalid arguments leave isolated registration untouched");
        WindowsIntegrationService.Register(first, fixture.Root);
        File.Delete(first);
        WindowsIntegrationService.Unregister(first, fixture.Root);
        Check(!Exists(fixture.Root, @"Software\Classes\Museek.Audio") && !Exists(fixture.Root, Capabilities),
            "unregister can clean up a known executable path after the file was removed");
    }

    private static void SeedNeighbors(RegistryKey root)
    {
        Write(root, @"Software\Classes\.mp3", "", "Other.Audio");
        Write(root, @"Software\Classes\.mp3\OpenWithProgids", "Other.Audio", "keep");
        Write(root, @"Software\Classes\Other.Audio", "", "other player");
        Write(root, @"Software\RegisteredApplications", "OtherPlayer", @"Software\OtherPlayer\Capabilities");
        Write(root, @"Software\Museek", "OtherPreference", "keep");
        Write(root, @"Software\Museek\OtherSettings", "Value", "keep child");
        Write(root, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp3\UserChoice", "ProgId", "Other.Audio");
        Write(root, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp3\UserChoice", "Hash", "original hash");
    }

    private static void CheckDefaultsAndNeighbors(RegistryKey root)
    {
        Check(Read(root, @"Software\Classes\.mp3", "") as string == "Other.Audio" &&
            Read(root, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp3\UserChoice", "ProgId") as string == "Other.Audio" &&
            Read(root, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp3\UserChoice", "Hash") as string == "original hash",
            "extension defaults and Explorer UserChoice remain unchanged");
        Check(Read(root, @"Software\Classes\.mp3\OpenWithProgids", "Other.Audio") as string == "keep" &&
            Read(root, @"Software\Classes\Other.Audio", "") as string == "other player" &&
            Read(root, @"Software\RegisteredApplications", "OtherPlayer") as string == @"Software\OtherPlayer\Capabilities",
            "neighboring players' registry entries remain unchanged");
        Check(Read(root, @"Software\Museek\OtherSettings", "Value") as string == "keep child",
            "unrelated Museek settings subkeys remain unchanged");
    }

    private static string MakeExecutable(string directory, string name)
    {
        var path = Path.Combine(directory, name, "Museek.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "registration-only fixture");
        return path;
    }

    private static string Command(string executable) => $"\"{executable}\" \"%1\"";
    private static object? Read(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
    private static bool Exists(RegistryKey root, string path) { using var key = root.OpenSubKey(path); return key is not null; }
    private static void Write(RegistryKey root, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, kind);
    }
    private static string Snapshot(RegistryKey root)
    {
        var snapshot = new StringBuilder();
        Capture(root, "", snapshot);
        return snapshot.ToString();
    }
    private static void Capture(RegistryKey root, string path, StringBuilder snapshot)
    {
        snapshot.AppendLine(path);
        foreach (var name in root.GetValueNames().OrderBy(name => name, StringComparer.Ordinal))
        {
            var value = root.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            snapshot.AppendLine($"{name}|{root.GetValueKind(name)}|{(value is byte[] bytes ? Convert.ToHexString(bytes) : value)}");
        }
        foreach (var name in root.GetSubKeyNames().OrderBy(name => name, StringComparer.Ordinal))
        {
            using var child = root.OpenSubKey(name)!;
            Capture(child, path + "\\" + name, snapshot);
        }
    }
    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { Check(true, message); return; }
        throw new InvalidOperationException(message);
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException("Check failed: " + message);
        _checks++;
    }

    private sealed class RegistryFixture : IDisposable
    {
        private readonly string _path = @"Software\Museek.Tests\Registration\" + Guid.NewGuid().ToString("N");
        public RegistryKey Root { get; }
        public RegistryFixture() => Root = Registry.CurrentUser.CreateSubKey(_path);
        public void Dispose()
        {
            Root.Dispose();
            // Only this freshly created, unique synthetic HKCU namespace is removed.
            Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        }
    }
}
