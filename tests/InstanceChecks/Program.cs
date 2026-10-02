using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Museek.Services;

namespace Museek.InstanceChecks;

internal static class Program
{
    private static int _checks;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child") return await RunChildAsync(args[1..]);
        var directory = Path.Combine(Path.GetTempPath(), "Museek instance checks 音楽 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await CheckSettingsAsync(directory);
            await CheckForwardingAndReleaseAsync(directory);
            await CheckContentionAsync(directory);
            await CheckCrashRecoveryAsync(directory);
            await CheckCancellationAsync(directory);
            using var unavailable = new SingleWindowService(NewIdentity(), directory);
            var elapsed = Stopwatch.StartNew();
            var accepted = await unavailable.TryForwardAsync(null, TimeSpan.FromMilliseconds(300));
            Check(!accepted && elapsed.Elapsed < TimeSpan.FromSeconds(3),
                "forwarding without an owner fails within its timeout");
            Console.WriteLine($"All {_checks} isolated settings and cross-process instance checks passed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            // This exact, freshly-created test directory is the only cleanup target.
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckSettingsAsync(string directory)
    {
        var settingsPath = Path.Combine(directory, "settings 日本語", "user settings.json");
        var settings = new AppSettingsService(settingsPath);
        settings.Reload();
        Check(!settings.SingleWindowMode && !File.Exists(settingsPath),
            "missing isolated settings default to disabled without creating a file");
        settings.SetSingleWindowMode(true);
        var reloaded = new AppSettingsService(settingsPath);
        Check(!reloaded.SingleWindowMode, "constructing settings leaves loading explicit");
        reloaded.Reload();
        Check(settings.SingleWindowMode && reloaded.SingleWindowMode,
            "enabled mode persists through a Unicode path and a new settings object");
        using (var reader = new ChildProcess(directory, "settings", settingsPath))
        {
            Check((await reader.ReadAsync(message => message.Kind == "Settings")).Success,
                "a separate process reads persisted enabled mode");
        }
        settings.SetSingleWindowMode(false);
        Check(reloaded.SingleWindowMode, "an existing settings object retains its value until Reload");
        reloaded.Reload();
        Check(!reloaded.SingleWindowMode, "Reload observes mode disabled by another settings object");
        using (var reader = new ChildProcess(directory, "settings", settingsPath))
        {
            Check(!(await reader.ReadAsync(message => message.Kind == "Settings")).Success,
                "a separate process reads persisted disabled mode");
        }

        settings.SetSingleWindowMode(true);
        await File.WriteAllTextAsync(settingsPath, "{ malformed settings");
        ExpectFailure(settings.Reload, "malformed settings report a loading error");
        Check(settings.SingleWindowMode, "failed Reload keeps the last valid settings state");
        settings.SetSingleWindowMode(false);
        reloaded.Reload();
        Check(!reloaded.SingleWindowMode, "a later valid save recovers from malformed settings");

        var blockedPath = Path.Combine(directory, "blocked settings.json");
        Directory.CreateDirectory(blockedPath);
        var blocked = new AppSettingsService(blockedPath);
        ExpectFailure(blocked.Reload, "a directory at the settings path reports a read error");
        ExpectFailure(() => blocked.SetSingleWindowMode(true), "an unwritable destination reports a save error");
        Check(!blocked.SingleWindowMode && Directory.Exists(blockedPath),
            "failed persistence preserves the previous mode and existing destination");
        Check(!Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Any(),
            "failed persistence removes temporary settings files");
    }

    private static async Task CheckForwardingAndReleaseAsync(string directory)
    {
        var identity = NewIdentity();
        using var primary = new ChildProcess(directory, "launch", identity, directory, "");
        Check((await primary.ReadRoleAsync()).Kind == "Owned", "the first process acquires isolated ownership");
        var unicodePath = Path.Combine(directory, "曲 ' & audio sample.wav");
        await File.WriteAllTextAsync(unicodePath, "IPC path fixture");
        using (var sender = new ChildProcess(directory, "launch", identity, directory, unicodePath))
        {
            var role = await sender.ReadRoleAsync();
            var request = await primary.ReadRequestAsync(unicodePath);
            Check(role.Kind == "Forwarded" && role.Success && request.Success,
                "a second process forwards a Unicode path with spaces and punctuation and receives ACK");
        }
        using (var sender = new ChildProcess(directory, "launch", identity, directory, ""))
        {
            var role = await sender.ReadRoleAsync();
            var request = await primary.ReadRequestAsync(null);
            Check(role.Kind == "Forwarded" && role.Success && request.Path is null,
                "a launch without a file forwards an activation-only request");
        }
        const string relativePath = "relative 音楽 audio.wav";
        using (var sender = new ChildProcess(directory, "launch", identity, directory, relativePath))
        {
            var role = await sender.ReadRoleAsync();
            await primary.ReadRequestAsync(Path.GetFullPath(relativePath, directory));
            Check(role.Kind == "Forwarded" && role.Success,
                "the sender resolves relative paths against its own working directory");
        }
        var rejectedPath = Path.Combine(directory, "reject audio.wav");
        using (var sender = new ChildProcess(directory, "launch", identity, directory, rejectedPath))
        {
            var role = await sender.ReadRoleAsync();
            var request = await primary.ReadRequestAsync(rejectedPath);
            Check(role.Kind == "Forwarded" && !role.Success && !request.Success,
                "a rejected callback returns no ACK instead of reporting a successful handoff");
        }
        primary.Send("release");
        await primary.ReadAsync(message => message.Kind == "Released");
        Check(!primary.HasExited, "disabling single-window mode releases ownership without ending its process");
        using (var replacement = new ChildProcess(directory, "launch", identity, directory, ""))
        {
            Check((await replacement.ReadRoleAsync()).Kind == "Owned",
                "another process acquires ownership after mode is disabled");
        }
        primary.Send("acquire");
        Check((await primary.ReadAsync(message => message.Kind == "Reacquired")).Success,
            "the same service can reacquire ownership when mode is enabled again");
    }

    private static async Task CheckContentionAsync(string directory)
    {
        var identity = NewIdentity();
        var gate = Path.Combine(directory, "startup gate");
        var contenders = Enumerable.Range(0, 4)
            .Select(index => new ChildProcess(directory, "gated", identity, directory, gate,
                Path.Combine(directory, $"competing 曲 {index}.wav")))
            .ToArray();
        try
        {
            foreach (var contender in contenders) await contender.ReadAsync(message => message.Kind == "Waiting");
            await File.WriteAllTextAsync(gate, "start");
            var roles = await Task.WhenAll(contenders.Select(contender => contender.ReadRoleAsync()));
            var owners = Enumerable.Range(0, roles.Length).Where(index => roles[index].Kind == "Owned").ToArray();
            Check(owners.Length == 1 && roles.Where(role => role.Kind == "Forwarded").All(role => role.Success),
                "simultaneous startups elect exactly one owner and acknowledge the other launches");
            var primary = contenders[owners.Single()];
            foreach (var index in Enumerable.Range(0, contenders.Length).Where(index => index != owners[0]))
                await primary.ReadRequestAsync(Path.Combine(directory, $"competing 曲 {index}.wav"));
            Check(true, "the elected owner receives every competing process's distinct file request");
        }
        finally { foreach (var contender in contenders) contender.Dispose(); }
    }

    private static async Task CheckCrashRecoveryAsync(string directory)
    {
        var identity = NewIdentity();
        using (var crashed = new ChildProcess(directory, "launch", identity, directory, ""))
        {
            await crashed.ReadAsync(message => message.Kind == "Owned");
            await crashed.KillAsync();
        }
        using var successor = new ChildProcess(directory, "launch", identity, directory, "");
        Check((await successor.ReadRoleAsync()).Kind == "Owned",
            "process termination releases ownership so a new launch recovers without stale-lock cleanup");
        var path = Path.Combine(directory, "after crash 音楽.wav");
        using var sender = new ChildProcess(directory, "launch", identity, directory, path);
        var role = await sender.ReadRoleAsync();
        await successor.ReadRequestAsync(path);
        Check(role.Kind == "Forwarded" && role.Success, "the recovered owner accepts subsequent file launches");
    }

    private static async Task CheckCancellationAsync(string directory)
    {
        var identity = NewIdentity();
        using var owner = new SingleWindowService(identity, directory);
        using var sender = new SingleWindowService(identity, directory);
        var receivedTokens = Channel.CreateUnbounded<CancellationToken>();
        var neverCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateDeliveries = 0;
        async Task<bool> SlowRequestAsync(CancellationToken token)
        {
            slowStarted.TrySetResult(token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                token.ThrowIfCancellationRequested();
                lateDeliveries++;
                return true;
            }
            finally { slowCompleted.TrySetResult(); }
        }
        Task<bool> Accept(string? path, CancellationToken token)
        {
            if (Path.GetFileName(path ?? "").StartsWith("slow", StringComparison.Ordinal))
                return SlowRequestAsync(token);
            if (Path.GetFileName(path ?? "").StartsWith("blocked", StringComparison.Ordinal))
            {
                receivedTokens.Writer.TryWrite(token);
                return neverCompleted.Task; // Deliberately ignores cancellation to test the receiver's own lifetime.
            }
            return Task.FromResult(true);
        }
        Check(owner.TryAcquire(Accept), "the cancellation fixture acquires isolated ownership");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectCancellationAsync(() => sender.TryForwardAsync(null, TimeSpan.FromSeconds(5), cancelled.Token),
                "a caller cancelled before forwarding receives cancellation");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = sender.TryForwardAsync(Path.Combine(directory, "blocked caller.wav"),
                TimeSpan.FromSeconds(5), cancellation.Token);
            var callbackToken = await receivedTokens.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await ExpectCancellationAsync(() => pending, "a caller can cancel an active handoff while its callback is pending");
            var elapsed = Stopwatch.StartNew();
            owner.Release();
            Check(!owner.IsPrimary && callbackToken.IsCancellationRequested && elapsed.Elapsed < TimeSpan.FromSeconds(1),
                "Release cancels the callback token and terminates promptly without awaiting an uncooperative callback");
        }
        Check(owner.TryAcquire(Accept), "ownership can be reacquired after callback cancellation");
        var waitingForAck = sender.TryForwardAsync(Path.Combine(directory, "blocked release.wav"), TimeSpan.FromSeconds(5));
        var releaseToken = await receivedTokens.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        owner.Release();
        Check(!await waitingForAck && releaseToken.IsCancellationRequested,
            "releasing the owner during an ACK wait reports a failed handoff and cancels its callback");

        Check(owner.TryAcquire(Accept), "ownership can be reacquired after an interrupted ACK wait");
        var timeout = sender.TryForwardAsync(Path.Combine(directory, "blocked timeout.wav"), TimeSpan.FromSeconds(4));
        var timeoutToken = await receivedTokens.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Check(!await timeout && timeoutToken.IsCancellationRequested,
            "a callback deadline rejects an uncompleted request and cancels its callback token");
        Check(await sender.TryForwardAsync(null, TimeSpan.FromSeconds(2)) && owner.IsPrimary,
            "the owner still accepts new requests after a callback times out");
        var shortDeadline = sender.TryForwardAsync(Path.Combine(directory, "slow handoff.wav"), TimeSpan.FromMilliseconds(700));
        var slowToken = await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!await shortDeadline && slowToken.IsCancellationRequested,
            "a sender deadline shorter than the server budget cancels the callback before fallback");
        await slowCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(lateDeliveries == 0, "an expired handoff produces no later file delivery");
    }

    private static async Task ExpectCancellationAsync(Func<Task<bool>> action, string description)
    {
        var cancelled = false;
        try { await action(); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, description);
    }

    private static async Task<int> RunChildAsync(string[] args)
    {
        try
        {
            if (args[0] == "settings")
            {
                var settings = new AppSettingsService(args[1]);
                settings.Reload();
                Publish(new ChildMessage("Settings", settings.SingleWindowMode));
                return 0;
            }
            var identity = args[1];
            var lockDirectory = args[2];
            var path = args[^1].Length == 0 ? null : args[^1];
            if (args[0] == "gated")
            {
                Publish(new ChildMessage("Waiting", true));
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(args[3]))
                {
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Startup gate did not open.");
                    await Task.Delay(20);
                }
            }
            using var service = new SingleWindowService(identity, lockDirectory);
            Task<bool> Accept(string? requestedPath)
            {
                var accept = !(Path.GetFileName(requestedPath ?? "").StartsWith("reject", StringComparison.Ordinal));
                Publish(new ChildMessage("Request", accept, requestedPath));
                return Task.FromResult(accept);
            }
            if (!service.TryAcquire(Accept))
            {
                var accepted = await service.TryForwardAsync(path, TimeSpan.FromSeconds(6));
                Publish(new ChildMessage("Forwarded", accepted));
                return 0;
            }
            Publish(new ChildMessage("Owned", service.IsPrimary));
            while (await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } command)
            {
                if (command == "quit") break;
                if (command == "release")
                {
                    service.Release();
                    Publish(new ChildMessage("Released", !service.IsPrimary));
                }
                else if (command == "acquire") Publish(new ChildMessage("Reacquired", service.TryAcquire(Accept)));
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static string NewIdentity() => "Museek.InstanceChecks." + Guid.NewGuid().ToString("N");

    private static void Publish(ChildMessage message) => Console.WriteLine(JsonSerializer.Serialize(message));

    private static void ExpectFailure(Action action, string description)
    {
        Exception? failure = null;
        try { action(); }
        catch (Exception exception) { failure = exception; }
        Check(failure is not null, description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        _checks++;
        Console.WriteLine("PASS: " + description);
    }

    private sealed record ChildMessage(string Kind, bool Success, string? Path = null);

    private sealed class ChildProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _error;
        private readonly List<ChildMessage> _buffered = [];

        public bool HasExited => _process.HasExited;

        public ChildProcess(string workingDirectory, params string[] arguments)
        {
            var executable = Environment.ProcessPath ?? throw new Exception("Cannot locate the check executable.");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            // Works through both the generated apphost and `dotnet InstanceChecks.dll`.
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--child");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new Exception("Cannot launch the isolated check child.");
            _error = _process.StandardError.ReadToEndAsync();
        }

        public void Send(string command)
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }

        public Task<ChildMessage> ReadRoleAsync() => ReadAsync(message => message.Kind is "Owned" or "Forwarded");

        public Task<ChildMessage> ReadRequestAsync(string? path)
            => ReadAsync(message => message.Kind == "Request" && message.Path == path);

        public async Task<ChildMessage> ReadAsync(Func<ChildMessage, bool> predicate)
        {
            var buffered = _buffered.FindIndex(message => predicate(message));
            if (buffered >= 0)
            {
                var match = _buffered[buffered];
                _buffered.RemoveAt(buffered);
                return match;
            }
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
                if (line is null) throw new Exception("Check child exited before its expected reply: " + await _error);
                var message = JsonSerializer.Deserialize<ChildMessage>(line)
                    ?? throw new Exception("Invalid check child reply: " + line);
                if (predicate(message)) return message;
                _buffered.Add(message);
            }
        }

        public async Task KillAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    Send("quit");
                    if (!_process.WaitForExit(8_000))
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(8_000);
                    }
                }
            }
            finally { _process.Dispose(); }
        }
    }
}
