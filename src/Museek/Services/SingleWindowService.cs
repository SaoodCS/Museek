using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Museek.Services;

public sealed class SingleWindowService : IDisposable
{
    private const int MaximumPathBytes = 131_072;
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);
    private readonly object _sync = new();
    private readonly string _pipeName;
    private readonly string _lockPath;
    private PrimaryLease? _lease;
    private bool _disposed;

    public SingleWindowService(string? applicationId = null, string? lockDirectory = null)
    {
        applicationId ??= "Museek.SingleWindow.v1";
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var identity = GetUserSessionIdentity();
        var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes($"{applicationId}\0{identity}")));
        _pipeName = $"Museek-{hash}";
        _lockPath = Path.Combine(Path.GetFullPath(lockDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Museek", "InstanceLocks")), $"{hash}.lock");
    }

    public bool IsPrimary
    {
        get { lock (_sync) return _lease is not null; }
    }

    public bool TryAcquire(Func<string?, Task<bool>> acceptRequest)
    {
        ArgumentNullException.ThrowIfNull(acceptRequest);
        return TryAcquire((path, _) => acceptRequest(path));
    }

    public bool TryAcquire(Func<string?, CancellationToken, Task<bool>> acceptRequest)
    {
        ArgumentNullException.ThrowIfNull(acceptRequest);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lease is not null)
                return true;

            Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
            FileStream ownership;
            try
            {
                // Keep the file after release: its open handle, not its existence, owns the endpoint.
                ownership = new FileStream(_lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                return false;
            }

            try { _lease = new PrimaryLease(_pipeName, ownership, acceptRequest); }
            catch
            {
                ownership.Dispose();
                throw;
            }
            return true;
        }
    }

    public async Task<bool> TryForwardAsync(string? path, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A positive forwarding timeout is required.");

        var normalizedPath = path is null ? null : Path.GetFullPath(path);
        var payload = normalizedPath is null ? Array.Empty<byte>() : Utf8.GetBytes(normalizedPath);
        if (payload.Length > MaximumPathBytes)
            throw new ArgumentException("The file path is too long to forward.", nameof(path));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var expiryTicks = DateTime.UtcNow.Add(timeout).Ticks;
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            GrantForegroundPermission(pipe);
            var header = new byte[sizeof(int) + sizeof(long)];
            BinaryPrimitives.WriteInt32LittleEndian(header, normalizedPath is null ? -1 : payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(sizeof(int)), expiryTicks);
            await pipe.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            if (payload.Length != 0)
                await pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            var acknowledgment = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgment, deadline.Token).ConfigureAwait(false);
            return acknowledgment[0] == 1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Release()
    {
        lock (_sync)
        {
            var lease = _lease;
            _lease = null;
            lease?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            var lease = _lease;
            _lease = null;
            lease?.Dispose();
        }
    }

    private static string GetUserSessionIdentity()
    {
        using var process = Process.GetCurrentProcess();
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return $"{identity.User?.Value ?? identity.Name}:{process.SessionId}:{elevated}";
        }
        return $"{Environment.UserName}:{process.SessionId}";
    }

    private static void GrantForegroundPermission(NamedPipeClientStream pipe)
    {
        if (OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId)
            && processId != 0)
            _ = AllowSetForegroundWindow(processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    private sealed class PrimaryLease : IDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan AcknowledgmentMargin = TimeSpan.FromMilliseconds(100);
        private readonly FileStream _ownership;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenRegistration _cancelPipe;
        private readonly Task _receiver;

        public PrimaryLease(string pipeName, FileStream ownership,
            Func<string?, CancellationToken, Task<bool>> acceptRequest)
        {
            _ownership = ownership;
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _cancelPipe = _lifetime.Token.Register(() => _pipe.Dispose());
            // The pipe is listening before acquisition returns, even if this task starts later.
            _receiver = Task.Run(() => ReceiveAsync(acceptRequest));
        }

        private async Task ReceiveAsync(Func<string?, CancellationToken, Task<bool>> acceptRequest)
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    await _pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        deadline.CancelAfter(RequestTimeout);
                        await HandleRequestAsync(acceptRequest, deadline, Stopwatch.GetTimestamp())
                            .ConfigureAwait(false);
                    }
                    catch (IOException) { }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        if (!_lifetime.IsCancellationRequested)
                            _pipe.Disconnect();
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
            catch (IOException) when (_lifetime.IsCancellationRequested) { }
        }

        private async Task HandleRequestAsync(Func<string?, CancellationToken, Task<bool>> acceptRequest,
            CancellationTokenSource deadline, long startedAt)
        {
            var token = deadline.Token;
            var header = new byte[sizeof(int) + sizeof(long)];
            await _pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var expiryTicks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(sizeof(int)));
            var accepted = false;
            if (length is >= -1 and <= MaximumPathBytes && expiryTicks >= DateTime.MinValue.Ticks
                && expiryTicks <= DateTime.MaxValue.Ticks)
            {
                var remaining = TimeSpan.FromTicks(expiryTicks - DateTime.UtcNow.Ticks) - AcknowledgmentMargin;
                var serverRemaining = RequestTimeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining > serverRemaining)
                    remaining = serverRemaining;
                if (remaining <= TimeSpan.Zero)
                {
                    await _pipe.WriteAsync(new byte[] { 0 }, token).ConfigureAwait(false);
                    return;
                }
                deadline.CancelAfter(remaining);
                string? path = null;
                if (length >= 0)
                {
                    var payload = new byte[length];
                    await _pipe.ReadExactlyAsync(payload, token).ConfigureAwait(false);
                    try { path = Utf8.GetString(payload); }
                    catch (DecoderFallbackException)
                    {
                        await _pipe.WriteAsync(new byte[] { 0 }, token).ConfigureAwait(false);
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();
                try
                {
                    // Cancel the await even if a queued UI callback cannot run during shutdown.
                    accepted = await acceptRequest(path, token).WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { accepted = false; }
            }
            await _pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, token).ConfigureAwait(false);
            await _pipe.FlushAsync(token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            try { _receiver.GetAwaiter().GetResult(); }
            finally
            {
                _cancelPipe.Dispose();
                _pipe.Dispose();
                _lifetime.Dispose();
                _ownership.Dispose();
            }
        }
    }
}
