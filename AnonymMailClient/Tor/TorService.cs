using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MailKit.Net.Proxy;

namespace AnonymMailClient.Tor;

public enum TorEndpointSource
{
    AppManaged,
    Configured,
    SystemDaemon,
    TorBrowser
}

public sealed record TorEndpoint(string Host, int Port, TorEndpointSource Source)
{
    public override string ToString() => $"{Host}:{Port} ({Source})";
}

public static partial class TorService
{
    const bool IsLogEnabled = true;

    const int DaemonSocksPort = 9050;
    const int BrowserSocksPort = 9150;

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    static readonly SemaphoreSlim discoveryGate = new(1, 1);

    static TorEndpoint? current;
    public static TorEndpoint? Current
    {
        get
        {
            if (current is { Source: TorEndpointSource.AppManaged } appManaged && TorProcess.OwnSocksPort != appManaged.Port)
                current = null;
            return current;
        }
        private set => current = value;
    }

    public static string? LastError { get; private set; }

    #region Circuit isolation

    public static NetworkCredential NewRandomCredentials()
    {
        return new NetworkCredential(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
    }

    public static int CircuitEpochOf(string? isolationKey = null) => 1;

    public static int RotateCircuit(string? isolationKey = null)
    {
        Log("Tor circuit rotated (every connection receives fresh isolated credentials).", isRun: IsLogEnabled);
        return 1;
    }

    public static NetworkCredential IsolationCredentials(string? isolationKey = null) => NewRandomCredentials();

    public static IProxyClient CreateProxy(TorEndpoint endpoint, string? isolationKey = null)
    {
        var creds = NewRandomCredentials();
        return new TorSocks5Client(endpoint.Host, endpoint.Port, creds);
    }

    #endregion

    #region Discovery and Startup

    public static async Task<TorEndpoint> RequireEndpointAsync(CancellationToken cancellationToken)
    {
        if (Current is { } cached && await ProbeAsync(cached, cancellationToken))
            return cached;

        await discoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (Current is { } doubleChecked && await ProbeAsync(doubleChecked, cancellationToken))
                return doubleChecked;

            // 1. Try currently running managed tor
            if (TorProcess.OwnSocksPort is { } ownPort)
            {
                var candidate = new TorEndpoint("127.0.0.1", ownPort, TorEndpointSource.AppManaged);
                if (await ProbeAsync(candidate, cancellationToken))
                {
                    Current = candidate;
                    return candidate;
                }
            }

            // 2. Try standard system daemon / Tor browser ports
            foreach (var (port, src) in new[] { (DaemonSocksPort, TorEndpointSource.SystemDaemon), (BrowserSocksPort, TorEndpointSource.TorBrowser) })
            {
                var ep = new TorEndpoint("127.0.0.1", port, src);
                if (await ProbeAsync(ep, cancellationToken) && await VerifyTorAsync(ep, cancellationToken))
                {
                    Current = ep;
                    Log($"Using existing Tor endpoint: {ep}", isRun: IsLogEnabled);
                    return ep;
                }
            }

            // 3. Start managed tor process
            Log("Starting managed Tor process...", isRun: IsLogEnabled);
            int newPort = await TorProcess.StartAsync(cancellationToken);
            var managedEp = new TorEndpoint("127.0.0.1", newPort, TorEndpointSource.AppManaged);

            if (await ProbeAsync(managedEp, cancellationToken))
            {
                Current = managedEp;
                Log($"Managed Tor is ready at {managedEp}", isRun: IsLogEnabled);
                return managedEp;
            }

            throw new TorUnavailableException("Managed Tor started but SOCKS port did not answer.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log($"RequireEndpointAsync failed: {ex.Message}", LogLevel.Error, isRun: IsLogEnabled);
            throw;
        }
        finally
        {
            discoveryGate.Release();
        }
    }

    static async Task<bool> ProbeAsync(TorEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            await socket.ConnectAsync(endpoint.Host, endpoint.Port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task<bool> VerifyTorAsync(TorEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TorSocks5Client(endpoint.Host, endpoint.Port);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var stream = await client.ConnectAsync("check.torproject.org", 80, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
