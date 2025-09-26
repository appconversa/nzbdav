using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Clients.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Websocket;
using Usenet.Nntp.Responses;
using Usenet.Nzb;

namespace NzbWebDAV.Clients;

public class UsenetStreamingClient
{
    private readonly INntpClient _client;
    private readonly WebsocketManager _websocketManager;

    public UsenetStreamingClient(ConfigManager configManager, WebsocketManager websocketManager)
    {
        // initialize private members
        _websocketManager = websocketManager;

        // initialize the nntp-client
        var connectionPool = BuildConnectionPool(configManager.GetUsenetProviders());
        var multiConnectionClient = new MultiConnectionNntpClient(connectionPool);
        var cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 8192 });
        _client = new CachingNntpClient(multiConnectionClient, cache);

        // when config changes, update the connection-pool
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.host") &&
                !configEventArgs.ChangedConfig.ContainsKey("usenet.port") &&
                !configEventArgs.ChangedConfig.ContainsKey("usenet.use-ssl") &&
                !configEventArgs.ChangedConfig.ContainsKey("usenet.user") &&
                !configEventArgs.ChangedConfig.ContainsKey("usenet.pass") &&
                !configEventArgs.ChangedConfig.ContainsKey("usenet.connections") &&
                !configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            var providers = configManager.GetUsenetProviders();
            var newConnectionPool = BuildConnectionPool(providers);
            multiConnectionClient.UpdateConnectionPool(newConnectionPool);
        };
    }

    public async Task<bool> CheckNzbFileHealth(NzbFile nzbFile, CancellationToken cancellationToken = default)
    {
        var childCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = nzbFile.Segments
            .Select(x => x.MessageId.Value)
            .Select(x => _client.StatAsync(x, childCt.Token))
            .ToHashSet();

        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);

            var completedResult = await completedTask;
            if (completedResult.ResponseType != NntpStatResponseType.ArticleExists)
            {
                await childCt.CancelAsync();
                return false;
            }
        }

        return true;
    }

    public async Task<NzbFileStream> GetFileStream(NzbFile nzbFile, int concurrentConnections, CancellationToken ct)
    {
        var segmentIds = nzbFile.Segments.Select(x => x.MessageId.Value).ToArray();
        var fileSize = await _client.GetFileSizeAsync(nzbFile, cancellationToken: ct);
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections);
    }

    public NzbFileStream GetFileStream(string[] segmentIds, long fileSize, int concurrentConnections)
    {
        return new NzbFileStream(segmentIds, fileSize, _client, concurrentConnections);
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, CancellationToken cancellationToken)
    {
        return _client.GetSegmentStreamAsync(segmentId, cancellationToken);
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return _client.GetFileSizeAsync(file, cancellationToken);
    }

    private ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory
    )
    {
        var connectionPool = new ConnectionPool<INntpClient>(maxConnections, connectionFactory);
        connectionPool.OnConnectionPoolChanged += OnConnectionPoolChanged;
        var args = new ConnectionPool<INntpClient>.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        OnConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    private void OnConnectionPoolChanged(object? _, ConnectionPool<INntpClient>.ConnectionPoolChangedEventArgs args)
    {
        var message = $"{args.Live}|{args.Max}|{args.Idle}";
        _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message);
    }

    private ConnectionPool<INntpClient> BuildConnectionPool(IReadOnlyList<UsenetProviderConfig> providers)
    {
        var allocator = new UsenetProviderConnectionAllocator(providers);
        var maxConnections = Math.Max(allocator.TotalConnections, 1);
        return CreateNewConnectionPool(maxConnections, allocator.CreateConnectionAsync);
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        string host,
        int port,
        bool useSsl,
        string user,
        string pass,
        CancellationToken cancellationToken
    )
    {
        var connection = new ThreadSafeNntpClient();
        if (!await connection.ConnectAsync(host, port, useSsl, cancellationToken))
            throw new CouldNotConnectToUsenetException("Could not connect to usenet host. Check connection settings.");
        if (!await connection.AuthenticateAsync(user, pass, cancellationToken))
            throw new CouldNotLoginToUsenetException("Could not login to usenet host. Check username and password.");
        return connection;
    }
}