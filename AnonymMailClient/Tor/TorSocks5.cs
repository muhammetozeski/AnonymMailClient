using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Proxy;

namespace AnonymMailClient.Tor;

public sealed class TorUnavailableException(string message, bool recoverable = true) : Exception(message)
{
    public bool Recoverable { get; } = recoverable;
}

public sealed class SocksProtocolException(string message) : Exception(message);

public sealed class SocksReplyException(byte replyCode, string message) : Exception(message)
{
    public byte ReplyCode { get; } = replyCode;
}

public static class TorSocks5
{
    public const byte Version = 0x05;
    public const byte MethodNoAuth = 0x00;
    public const byte MethodUsernamePassword = 0x02;
    public const byte MethodNoneAcceptable = 0xFF;

    public const byte CommandConnect = 0x01;
    public const byte CommandTorResolve = 0xF0;

    public const byte AddressTypeIPv4 = 0x01;
    public const byte AddressTypeDomain = 0x03;
    public const byte AddressTypeIPv6 = 0x04;

    public const byte ReplySucceeded = 0x00;
    public const byte ReplyGeneralFailure = 0x01;
    public const byte ReplyConnectionNotAllowed = 0x02;
    public const byte ReplyNetworkUnreachable = 0x03;
    public const byte ReplyHostUnreachable = 0x04;
    public const byte ReplyConnectionRefused = 0x05;
    public const byte ReplyTtlExpired = 0x06;
    public const byte ReplyCommandNotSupported = 0x07;

    public const int MaxDomainLength = 255;
    const byte UsernamePasswordVersion = 0x01;

    static string DescribeReply(byte reply) => reply switch
    {
        ReplySucceeded => "succeeded",
        ReplyGeneralFailure => "general failure",
        ReplyConnectionNotAllowed => "connection not allowed by ruleset",
        ReplyNetworkUnreachable => "network unreachable",
        ReplyHostUnreachable => "host unreachable",
        ReplyConnectionRefused => "connection refused",
        ReplyTtlExpired => "TTL expired",
        ReplyCommandNotSupported => "command not supported",
        _ => $"unrecognized SOCKS5 reply 0x{reply:X2}"
    };

    public static async Task HandshakeAsync(Stream stream, NetworkCredential? credentials, CancellationToken cancellationToken)
    {
        byte[] methods = credentials != null
            ? [Version, 2, MethodNoAuth, MethodUsernamePassword]
            : [Version, 1, MethodNoAuth];

        await stream.WriteAsync(methods, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        byte[] greeting = new byte[2];
        await ReadExactAsync(stream, greeting, cancellationToken);
        if (greeting[0] != Version)
            throw new SocksProtocolException($"Expected SOCKS version 5, got 0x{greeting[0]:X2}.");

        byte chosen = greeting[1];
        if (chosen == MethodNoneAcceptable)
            throw new SocksProtocolException("The proxy rejected all offered authentication methods.");

        if (chosen == MethodUsernamePassword)
        {
            if (credentials == null)
                throw new SocksProtocolException("The proxy demanded authentication but no credentials were provided.");
            await SubnegotiateAuthAsync(stream, credentials, cancellationToken);
        }
        else if (chosen != MethodNoAuth)
        {
            throw new SocksProtocolException($"The proxy chose unexpected authentication method 0x{chosen:X2}.");
        }
    }

    static async Task SubnegotiateAuthAsync(Stream stream, NetworkCredential credentials, CancellationToken cancellationToken)
    {
        byte[] userBytes = Encoding.UTF8.GetBytes(credentials.UserName ?? string.Empty);
        byte[] passBytes = Encoding.UTF8.GetBytes(credentials.Password ?? string.Empty);

        if (userBytes.Length > 255 || passBytes.Length > 255)
            throw new ArgumentException("SOCKS5 credentials must fit in 255 bytes.");

        byte[] request = new byte[1 + 1 + userBytes.Length + 1 + passBytes.Length];
        request[0] = UsernamePasswordVersion;
        request[1] = (byte)userBytes.Length;
        Buffer.BlockCopy(userBytes, 0, request, 2, userBytes.Length);
        int offset = 2 + userBytes.Length;
        request[offset] = (byte)passBytes.Length;
        Buffer.BlockCopy(passBytes, 0, request, offset + 1, passBytes.Length);

        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        byte[] response = new byte[2];
        await ReadExactAsync(stream, response, cancellationToken);
        if (response[0] != UsernamePasswordVersion)
            throw new SocksProtocolException($"Expected RFC 1929 subnegotiation version 1, got 0x{response[0]:X2}.");
        if (response[1] != 0x00)
            throw new SocksReplyException(response[1], $"SOCKS5 username/password auth failed (status 0x{response[1]:X2}).");
    }

    public static async Task SendRequestAsync(Stream stream, byte command, string host, int port, CancellationToken cancellationToken)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > MaxDomainLength)
            throw new ArgumentException($"Host name '{host}' exceeds {MaxDomainLength} bytes.");

        byte[] request = new byte[4 + 1 + hostBytes.Length + 2];
        request[0] = Version;
        request[1] = command;
        request[2] = 0x00; // Reserved
        request[3] = AddressTypeDomain;
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);

        int portOffset = 5 + hostBytes.Length;
        request[portOffset] = (byte)((port >> 8) & 0xFF);
        request[portOffset + 1] = (byte)(port & 0xFF);

        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        await ReadReplyAsync(stream, cancellationToken);
    }

    public static async Task<IPAddress> ResolveAsync(Stream stream, string host, CancellationToken cancellationToken)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > MaxDomainLength)
            throw new ArgumentException($"Host name '{host}' exceeds {MaxDomainLength} bytes.");

        byte[] request = new byte[4 + 1 + hostBytes.Length + 2];
        request[0] = Version;
        request[1] = CommandTorResolve;
        request[2] = 0x00;
        request[3] = AddressTypeDomain;
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);

        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        return await ReadResolveReplyAsync(stream, cancellationToken);
    }

    static async Task ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken);

        if (header[0] != Version)
            throw new SocksProtocolException($"Expected SOCKS version 5, got 0x{header[0]:X2}.");

        byte reply = header[1];
        if (reply != ReplySucceeded)
            throw new SocksReplyException(reply, DescribeReply(reply));

        await SkipBoundAddressAsync(stream, header[3], cancellationToken);
    }

    static async Task<IPAddress> ReadResolveReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken);

        if (header[0] != Version)
            throw new SocksProtocolException($"Expected SOCKS version 5, got 0x{header[0]:X2}.");

        byte reply = header[1];
        if (reply != ReplySucceeded)
            throw new SocksReplyException(reply, DescribeReply(reply));

        byte atyp = header[3];
        byte[] addrBytes = atyp switch
        {
            AddressTypeIPv4 => new byte[4],
            AddressTypeIPv6 => new byte[16],
            AddressTypeDomain => throw new SocksProtocolException("Tor returned a domain name for a RESOLVE request."),
            _ => throw new SocksProtocolException($"Unrecognized address type 0x{atyp:X2} in SOCKS5 reply.")
        };

        await ReadExactAsync(stream, addrBytes, cancellationToken);

        byte[] portBytes = new byte[2];
        await ReadExactAsync(stream, portBytes, cancellationToken);

        return new IPAddress(addrBytes);
    }

    static async Task SkipBoundAddressAsync(Stream stream, byte atyp, CancellationToken cancellationToken)
    {
        int bytesToSkip = atyp switch
        {
            AddressTypeIPv4 => 4 + 2,
            AddressTypeIPv6 => 16 + 2,
            AddressTypeDomain => -1,
            _ => throw new SocksProtocolException($"Unrecognized address type 0x{atyp:X2} in SOCKS5 reply.")
        };

        if (bytesToSkip > 0)
        {
            byte[] discard = new byte[bytesToSkip];
            await ReadExactAsync(stream, discard, cancellationToken);
            return;
        }

        byte[] lenByte = new byte[1];
        await ReadExactAsync(stream, lenByte, cancellationToken);
        byte[] domainAndPort = new byte[lenByte[0] + 2];
        await ReadExactAsync(stream, domainAndPort, cancellationToken);
    }

    static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException($"SOCKS5 stream ended unexpectedly after {total}/{buffer.Length} bytes.");
            total += read;
        }
    }
}

public sealed class TorSocks5Client(string proxyHost, int proxyPort, NetworkCredential? credentials = null) : IProxyClient, IDisposable
{
    readonly NetworkCredential? credentials = credentials;

    public string ProxyHost { get; } = proxyHost;
    public int ProxyPort { get; } = proxyPort;
    public NetworkCredential ProxyCredentials => credentials!;
    public IPEndPoint? LocalEndPoint { get; set; }

    public void Dispose() { }

    public Stream Connect(string host, int port, CancellationToken cancellationToken = default) =>
        Task.Run(() => ConnectAsync(host, port, cancellationToken), cancellationToken).GetAwaiter().GetResult();

    public Stream Connect(string host, int port, int timeout, CancellationToken cancellationToken = default) =>
        Task.Run(() => ConnectAsync(host, port, timeout, cancellationToken), cancellationToken).GetAwaiter().GetResult();

    public async Task<Stream> ConnectAsync(string host, int port, int timeout, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            return await ConnectAsync(host, port, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connecting to {host}:{port} through {ProxyHost}:{ProxyPort} timed out.");
        }
    }

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        NetworkStream? stream = null;
        try
        {
            if (LocalEndPoint != null) socket.Bind(LocalEndPoint);
            await socket.ConnectAsync(ProxyHost, ProxyPort, cancellationToken);

            stream = new NetworkStream(socket, ownsSocket: true);
            await TorSocks5.HandshakeAsync(stream, credentials, cancellationToken);
            await TorSocks5.SendRequestAsync(stream, TorSocks5.CommandConnect, host, port, cancellationToken);
            return stream;
        }
        catch
        {
            if (stream != null) await stream.DisposeAsync();
            else socket.Dispose();
            throw;
        }
    }
}
