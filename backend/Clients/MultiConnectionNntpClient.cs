using NzbWebDAV.Clients.Connections;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using Serilog;
using Usenet.Exceptions;
using Usenet.Nntp.Responses;
using Usenet.Nzb;
using Usenet.Yenc;

namespace NzbWebDAV.Clients;

public class MultiConnectionNntpClient(ConnectionPool<INntpClient> connectionPool) : INntpClient
{
    private static readonly TimeSpan ConnectionReadyTimeout = TimeSpan.FromSeconds(30);

    private ConnectionPool<INntpClient> _connectionPool = connectionPool;

    public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public Task<NntpStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunWithConnection(connection => connection.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public Task<NntpDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunWithConnection(connection => connection.DateAsync(cancellationToken), cancellationToken);
    }

    public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunWithConnection(connection => connection.GetSegmentStreamAsync(segmentId, cancellationToken), cancellationToken);
    }

    public Task<YencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        return RunWithConnection(connection => connection.GetSegmentYencHeaderAsync(segmentId, cancellationToken), cancellationToken);
    }

    public Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        return RunWithConnection(connection => connection.GetFileSizeAsync(file, cancellationToken), cancellationToken);
    }

    public async Task WaitForReady(CancellationToken cancellationToken)
    {
        await using var connectionLock = await _connectionPool.GetConnectionLockAsync(cancellationToken);
    }

    private async Task<T> RunWithConnection<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken,
        int retries = 1
    )
    {
        var connectionLock = await _connectionPool.GetConnectionLockAsync(cancellationToken);
        try
        {
            var result = await task(connectionLock.Connection);

            ScheduleReadinessRelease(connectionLock, cancellationToken);
            return result;
        }
        catch (NntpException e)
        {
            // we want to replace the underlying connection in cases of NntpExceptions.
            connectionLock.Replace();
            connectionLock.Dispose();

            // and try again with a new connection (max 1 retry)
            if (retries > 0)
                return await RunWithConnection<T>(task, cancellationToken, retries - 1);

            throw;
        }
        catch (OperationCanceledException)
        {
            connectionLock.Dispose();
            throw;
        }
        catch (Exception e) when (e.IsNonRetryableDownloadException())
        {
            connectionLock.Dispose();
            throw;
        }
        catch (Exception)
        {
            connectionLock.Replace();
            connectionLock.Dispose();

            if (retries > 0)
                return await RunWithConnection(task, cancellationToken, retries - 1);

            throw;
        }
    }

    public void UpdateConnectionPool(ConnectionPool<INntpClient> connectionPool)
    {
        var oldConnectionPool = _connectionPool;
        _connectionPool = connectionPool;
        oldConnectionPool.Dispose();
    }

    public void Dispose()
    {
        _connectionPool.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ScheduleReadinessRelease(
        ConnectionLock<INntpClient> connectionLock,
        CancellationToken cancellationToken)
    {
        var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readinessCts.CancelAfter(ConnectionReadyTimeout);

        Task readinessTask;
        try
        {
            readinessTask = connectionLock.Connection.WaitForReady(readinessCts.Token);
        }
        catch (Exception ex)
        {
            readinessCts.Dispose();
            Log.Warning(
                ex,
                "Failed to await NNTP connection readiness; replacing hung connection.");
            connectionLock.Replace();
            connectionLock.Dispose();
            return;
        }

        _ = readinessTask.ContinueWith(
            t =>
            {
                readinessCts.Dispose();

                if (t.IsCanceled)
                {
                    Log.Warning(
                        "NNTP connection readiness wait cancelled or timed out after {Timeout}; replacing hung connection.",
                        ConnectionReadyTimeout);
                    connectionLock.Replace();
                }
                else if (t.IsFaulted)
                {
                    Log.Warning(
                        t.Exception?.GetBaseException(),
                        "NNTP connection readiness wait faulted; replacing hung connection.");
                    connectionLock.Replace();
                }

                connectionLock.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
