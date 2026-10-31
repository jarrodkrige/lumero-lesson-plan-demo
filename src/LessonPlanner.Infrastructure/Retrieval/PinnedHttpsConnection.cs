using System.Net;
using System.Net.Sockets;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure.Retrieval;

internal interface IHostAddressResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal interface IAddressConnector
{
    ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken);
}

internal sealed class PinnedHttpsConnection(IHostAddressResolver resolver, IAddressConnector connector)
{
    internal async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.Port != 443 || !CuratedCurriculumCatalogue.IsAllowedHost(endpoint.Host))
            throw new DomainException("source_url_rejected", "The connection target is not an approved HTTPS host.");

        var addresses = await resolver.ResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (addresses.Length is 0 or > 16 || addresses.Any(address => !PublicAddressPolicy.IsPublic(address)))
            throw new DomainException("source_address_rejected", "The source host did not resolve exclusively to public addresses.");

        foreach (var address in addresses.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Connecting to this IP, not the hostname, prevents a second DNS lookup/rebinding.
                return await connector.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Only already-validated addresses from this DNS snapshot may be tried.
            }
        }

        throw new DomainException("source_unavailable", "A secure connection to the curriculum source could not be established.");
    }

    internal static SocketsHttpHandler CreateHandler(CurriculumRetrievalOptions options)
    {
        var connection = new PinnedHttpsConnection(new SystemAddressResolver(), new SocketAddressConnector());
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.ConnectTimeout,
            MaxResponseHeadersLength = 16,
            MaxResponseDrainSize = 0,
            ResponseDrainTimeout = TimeSpan.Zero,
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            ActivityHeadersPropagator = null,
            ConnectCallback = (context, cancellationToken) => connection.ConnectAsync(context.DnsEndPoint, cancellationToken)
        };
    }

    private sealed class SystemAddressResolver : IHostAddressResolver
    {
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            new(Dns.GetHostAddressesAsync(host, cancellationToken));
    }

    private sealed class SocketAddressConnector : IAddressConnector
    {
        public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
