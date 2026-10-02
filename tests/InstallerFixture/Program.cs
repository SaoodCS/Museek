using System.Text.Json;

// Harmless installer fixture: never registers real shell handlers or reads Museek settings.
// Its executable name matches the production payload so the actual installer can be tested.
var logPath = Environment.GetEnvironmentVariable("MUSEEK_INSTALLER_FIXTURE_LOG");
if (string.IsNullOrWhiteSpace(logPath)) return 81;

File.AppendAllText(logPath, JsonSerializer.Serialize(new
{
    Arguments = args,
    Directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
    Version = File.Exists(Path.Combine(AppContext.BaseDirectory, "payload-version.txt"))
        ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload-version.txt")).Trim()
        : "missing"
}) + Environment.NewLine);

if (args.Length == 3 && args[0] == "--hold")
{
    File.WriteAllText(args[1], Environment.ProcessId.ToString());
    // The harness releases this process by creating its stop file; it never kills an app.
    var deadline = DateTime.UtcNow.AddMinutes(3);
    while (!File.Exists(args[2]) && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    return File.Exists(args[2]) ? 0 : 82;
}

if (args.Length == 2 && args[1] == "--quiet" &&
    (args[0] == "--register" || args[0] == "--unregister"))
{
    if (args[0] == "--register" &&
        Environment.GetEnvironmentVariable("MUSEEK_INSTALLER_FIXTURE_FAIL_REGISTER") == "1")
        return 84;
    return 0;
}

return 83;
