using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using RipperWorks.Core;

namespace RipperWorks.Infrastructure;

// RF-03: DpapiNexusCredentialStore replaced by DpapiProtectedCredentialStore.

public sealed class NxmProtocolRegistration : INxmProtocolRegistration
{
    private const string KeyPath = @"Software\Classes\nxm";

    public void Register(string executablePath)
    {
        using var root = Registry.CurrentUser.CreateSubKey(KeyPath);
        root.SetValue("", "URL:Nexus Mods Protocol");
        root.SetValue("URL Protocol", "");
        using var icon = root.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{executablePath}\",0");
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{executablePath}\" \"%1\"");
    }

    public void Unregister() =>
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);

    public bool IsRegistered(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            KeyPath + @"\shell\open\command");
        var command = key?.GetValue("") as string;
        return command?.Contains(
            executablePath,
            StringComparison.OrdinalIgnoreCase) == true;
    }
}

public sealed class SingleInstanceCoordinator(
    string instanceName = "RipperWorks.SingleInstance.v1")
    : ISingleInstanceCoordinator
{
    private readonly string _mutexName = @"Local\" + instanceName;
    private readonly string _pipeName = instanceName;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listener;

    public event EventHandler<InstanceMessage>? MessageReceived;

    public async Task<bool> StartAsync(
        string? argument,
        CancellationToken cancellationToken = default)
    {
        if (_mutex is not null || _listenerCancellation is not null)
            throw new InvalidOperationException(
                "The single-instance listener is already running.");
        _mutex = new Mutex(false, _mutexName, out var isPrimary);
        if (isPrimary)
        {
            _listenerCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            _listener = Task.Run(
                () => ListenAsync(_listenerCancellation.Token),
                CancellationToken.None);
            return true;
        }
        _mutex.Dispose();
        _mutex = null;
        await SendAsync(
            string.IsNullOrWhiteSpace(argument)
                ? "__ACTIVATE__"
                : argument,
            cancellationToken);
        return false;
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                var value = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                MessageReceived?.Invoke(
                    this,
                    new InstanceMessage(
                        value,
                        value == "__ACTIVATE__"));
            }
            catch (OperationCanceledException) when (
                token.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token);
            }
        }
    }

    private async Task SendAsync(string value, CancellationToken token)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(200, token);
                await using var writer = new StreamWriter(client)
                {
                    AutoFlush = true
                };
                await writer.WriteLineAsync(value.AsMemory(), token);
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            await Task.Delay(100, token);
        }
        throw new IOException(
            "The running RipperWorks instance did not accept the NXM link.");
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        var listenerCancellation = _listenerCancellation;
        var listener = _listener;
        var mutex = _mutex;
        _listenerCancellation = null;
        _listener = null;
        _mutex = null;

        if (listenerCancellation is not null)
        {
            await listenerCancellation.CancelAsync()
                .ConfigureAwait(false);
            if (listener is not null)
            {
                try
                {
                    await listener.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            listenerCancellation.Dispose();
        }
        mutex?.Dispose();
    }

    public async ValueTask DisposeAsync() =>
        await StopAsync();
}
