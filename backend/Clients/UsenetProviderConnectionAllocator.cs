using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients;

public sealed class UsenetProviderConnectionAllocator
{
    private readonly UsenetProviderConfig[] _providers;
    private readonly int[] _liveConnections;
    private readonly object _sync = new();
    private int _nextIndex;

    public UsenetProviderConnectionAllocator(IEnumerable<UsenetProviderConfig> providers)
    {
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        if (_providers.Length == 0)
        {
            throw new ArgumentException("At least one usenet provider must be configured.", nameof(providers));
        }

        _liveConnections = new int[_providers.Length];
    }

    public int TotalConnections => _providers.Sum(provider => provider.Connections);

    public ValueTask<INntpClient> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        int providerIndex;
        UsenetProviderConfig provider;

        lock (_sync)
        {
            providerIndex = ReserveProviderIndex();
            provider = _providers[providerIndex];
            _liveConnections[providerIndex]++;
        }

        return CreateProviderConnectionAsync(providerIndex, provider, cancellationToken);
    }

    private int ReserveProviderIndex()
    {
        for (var attempt = 0; attempt < _providers.Length; attempt++)
        {
            var index = (_nextIndex + attempt) % _providers.Length;
            if (_liveConnections[index] < _providers[index].Connections)
            {
                _nextIndex = index + 1;
                return index;
            }
        }

        throw new InvalidOperationException("No available usenet provider capacity.");
    }

    private async ValueTask<INntpClient> CreateProviderConnectionAsync(
        int providerIndex,
        UsenetProviderConfig provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = await UsenetStreamingClient.CreateNewConnection(
                provider.Host,
                provider.Port,
                provider.UseSsl,
                provider.User,
                provider.Pass,
                cancellationToken);

            return new ProviderScopedNntpClient(connection, () => Release(providerIndex));
        }
        catch
        {
            Release(providerIndex);
            throw;
        }
    }

    private void Release(int providerIndex)
    {
        lock (_sync)
        {
            if (_liveConnections[providerIndex] > 0)
            {
                _liveConnections[providerIndex]--;
            }
        }
    }

    private sealed class ProviderScopedNntpClient : INntpClient
    {
        private readonly INntpClient _inner;
        private readonly Action _onDispose;
        private int _disposed;

        public ProviderScopedNntpClient(INntpClient inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
        }

        public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
        {
            return _inner.ConnectAsync(host, port, useSsl, cancellationToken);
        }

        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
        {
            return _inner.AuthenticateAsync(user, pass, cancellationToken);
        }

        public Task<Usenet.Nntp.Responses.NntpStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
        {
            return _inner.StatAsync(segmentId, cancellationToken);
        }

        public Task<Streams.YencHeaderStream> GetSegmentStreamAsync(string segmentId, CancellationToken cancellationToken)
        {
            return _inner.GetSegmentStreamAsync(segmentId, cancellationToken);
        }

        public Task<Usenet.Yenc.YencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
        {
            return _inner.GetSegmentYencHeaderAsync(segmentId, cancellationToken);
        }

        public Task<long> GetFileSizeAsync(Usenet.Nzb.NzbFile file, CancellationToken cancellationToken)
        {
            return _inner.GetFileSizeAsync(file, cancellationToken);
        }

        public Task<Usenet.Nntp.Responses.NntpDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            return _inner.DateAsync(cancellationToken);
        }

        public Task WaitForReady(CancellationToken cancellationToken)
        {
            return _inner.WaitForReady(cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                _inner.Dispose();
            }
            finally
            {
                _onDispose();
            }
        }
    }
}
