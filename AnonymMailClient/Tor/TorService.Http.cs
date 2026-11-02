using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AnonymMailClient.Tor;

public static partial class TorService
{
    /// <summary>
    /// Creates a completely isolated HttpClient for anonymous Tor communication.
    /// Guarantees:
    /// 1. Zero cookies: Cookie handling is disabled (UseCookies = false, CookieContainer = null).
    /// 2. Zero connection reuse / pooling: PooledConnectionLifetime = TimeSpan.Zero.
    /// 3. Unique Tor circuit per connection: Every single TCP connection uses fresh cryptographically random SOCKS5 credentials,
    ///    forcing Tor (via IsolateSOCKSAuth) to establish a distinct circuit and route through a separate exit IP.
    /// 4. Disposed handler: Disposing the client immediately closes and destroys the underlying socket and state.
    /// </summary>
    public static HttpClient CreateIsolatedHttpClient(string? isolationKey = null, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            CookieContainer = null,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            EnableMultipleHttp2Connections = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            ConnectCallback = async (context, token) =>
            {
                var endpoint = await RequireEndpointAsync(token);
                // FRESH random SOCKS5 auth per connection! Tor will assign a brand-new circuit and exit IP!
                var credentials = NewRandomCredentials();
                var socks = new TorSocks5Client(endpoint.Host, endpoint.Port, credentials);
                return await socks.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token);
            }
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; rv:128.0) Gecko/20100101 Firefox/128.0");
        return client;
    }

    public static void ReleaseAccountCircuit(string? isolationKey = null)
    {
        // No persistent circuits are retained; every connection is already ephemeral and isolated.
    }
}
