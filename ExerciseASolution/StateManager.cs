using System.Text.Json;
using ExerciseASolution;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

public interface IConnectionState
{
    DateTime LastUpdated { get; set; }
}

public interface IConnectionStateManager<TState> where TState : class, IConnectionState
{
    Task SetState(string connectionId, TState state);
    Task<TState?> GetState(string connectionId);
    Task RemoveState(string connectionId);
    Task<Dictionary<string, TState>> GetStatesForConnections(IEnumerable<string> connectionIds);
    Task<StateManagerMetrics> GetMetrics();
    Task SetStateWithExpiry(string connectionId, TState state, TimeSpan expiry);
}



public class StateManagerMetrics
{
    public int ActiveConnections { get; set; }
    public int StaleConnectionsRemoved { get; set; }
    public Dictionary<string, int> ConnectionsByTopic { get; set; } = new();
    public DateTime LastCleanupRun { get; set; }
    public TimeSpan AverageConnectionAge { get; set; }
}

public class ConnectionStateManager<TState> : IConnectionStateManager<TState>, IDisposable 
    where TState : class, IConnectionState
{
    private readonly IDatabase _db;
    private readonly string _stateKeyPrefix;
    private readonly TimeSpan _staleThreshold;
    private readonly ILogger<ConnectionStateManager<TState>> _logger;
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private bool _disposed;

    public ConnectionStateManager(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<AppOptions> options,
        ILogger<ConnectionStateManager<TState>> logger)
    {
        _db = connectionMultiplexer.GetDatabase();
        _logger = logger;
        _stateKeyPrefix = options.Value.StateKeyPrefix;
        _staleThreshold = options.Value.StaleThreshold;

        _cleanupTimer = new Timer(
            CleanupStaleData, 
            null, 
            TimeSpan.Zero, 
            options.Value.CleanupInterval
        );
    }

    private string GetStateKey(string connectionId) => $"{_stateKeyPrefix}:state:{connectionId}";

    public async Task SetState(string connectionId, TState state)
    {
        ThrowIfDisposed();
        try
        {
            state.LastUpdated = DateTime.UtcNow;
            await _db.StringSetAsync(
                GetStateKey(connectionId),
                JsonSerializer.Serialize(state)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set state for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task SetStateWithExpiry(string connectionId, TState state, TimeSpan expiry)
    {
        ThrowIfDisposed();
        try
        {
            state.LastUpdated = DateTime.UtcNow;
            await _db.StringSetAsync(
                GetStateKey(connectionId),
                JsonSerializer.Serialize(state),
                expiry
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set state with expiry for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task<TState?> GetState(string connectionId)
    {
        ThrowIfDisposed();
        try
        {
            var data = await _db.StringGetAsync(GetStateKey(connectionId));
            if (data.IsNull)
                return null;

            var state = JsonSerializer.Deserialize<TState>(data!);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get state for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task RemoveState(string connectionId)
    {
        ThrowIfDisposed();
        try
        {
            await _db.KeyDeleteAsync(GetStateKey(connectionId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove state for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task<Dictionary<string, TState>> GetStatesForConnections(IEnumerable<string> connectionIds)
    {
        ThrowIfDisposed();
        try
        {
            var batch = _db.CreateBatch();
            var tasks = connectionIds.Select(id => batch.StringGetAsync(GetStateKey(id))).ToList();
            batch.Execute();

            var results = new Dictionary<string, TState>();
            for (var i = 0; i < connectionIds.Count(); i++)
            {
                var data = await tasks[i];
                if (!data.IsNull)
                {
                    var state = JsonSerializer.Deserialize<TState>(data!);
                    if (state != null)
                    {
                        results[connectionIds.ElementAt(i)] = state;
                    }
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get states for multiple connections");
            throw;
        }
    }

    public async Task<StateManagerMetrics> GetMetrics()
    {
        ThrowIfDisposed();
        try
        {
            var metrics = new StateManagerMetrics
            {
                LastCleanupRun = DateTime.UtcNow
            };

            var keys = (RedisKey[])await _db.ExecuteAsync("KEYS", $"{_stateKeyPrefix}:state:*");
            metrics.ActiveConnections = keys.Length;

            var states = new List<TState>();
            var totalAge = TimeSpan.Zero;

            foreach (var key in keys)
            {
                var data = await _db.StringGetAsync(key);
                if (!data.IsNull)
                {
                    var state = JsonSerializer.Deserialize<TState>(data!);
                    if (state != null)
                    {
                        states.Add(state);
                        totalAge += DateTime.UtcNow - state.LastUpdated;

                        // If the state has subscribed topics (assuming ChatConnectionState)
                        if (state is ChatConnectionState chatState)
                        {
                            foreach (var topic in chatState.SubscribedTopics)
                            {
                                if (!metrics.ConnectionsByTopic.ContainsKey(topic))
                                {
                                    metrics.ConnectionsByTopic[topic] = 0;
                                }
                                metrics.ConnectionsByTopic[topic]++;
                            }
                        }
                    }
                }
            }

            if (states.Count > 0)
            {
                metrics.AverageConnectionAge = TimeSpan.FromTicks(totalAge.Ticks / states.Count);
            }

            // Get the stale connections removed from the last cleanup
            var lastCleanupData = await _db.StringGetAsync($"{_stateKeyPrefix}:metrics:last_cleanup");
            if (!lastCleanupData.IsNull)
            {
                var lastCleanupMetrics = JsonSerializer.Deserialize<StateManagerMetrics>(lastCleanupData!);
                if (lastCleanupMetrics != null)
                {
                    metrics.StaleConnectionsRemoved = lastCleanupMetrics.StaleConnectionsRemoved;
                }
            }

            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting state manager metrics");
            throw;
        }
    }

    private async void CleanupStaleData(object? state)
    {
        if (_disposed) return;

        if (!await _cleanupLock.WaitAsync(0))
        {
            return; // Another cleanup is still running
        }

        try
        {
            var staleConnectionsRemoved = 0;
            var now = DateTime.UtcNow;
            var keys = (RedisKey[])await _db.ExecuteAsync("KEYS", $"{_stateKeyPrefix}:state:*");

            foreach (var key in keys)
            {
                if (_disposed) return;

                var data = await _db.StringGetAsync(key);
                if (!data.IsNull)
                {
                    var connectionState = JsonSerializer.Deserialize<TState>(data!);
                    if (connectionState != null && now - connectionState.LastUpdated > _staleThreshold)
                    {
                        await _db.KeyDeleteAsync(key);
                        staleConnectionsRemoved++;
                        _logger?.LogInformation("Cleaned up stale state for key: {Key}", key);
                    }
                }
            }

            // Store cleanup metrics
            var metrics = new StateManagerMetrics
            {
                StaleConnectionsRemoved = staleConnectionsRemoved,
                LastCleanupRun = now
            };

            await _db.StringSetAsync(
                $"{_stateKeyPrefix}:metrics:last_cleanup",
                JsonSerializer.Serialize(metrics),
                TimeSpan.FromDays(1)
            );

            _logger?.LogInformation("Cleanup completed. Removed {Count} stale connections", staleConnectionsRemoved);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during stale data cleanup");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _cleanupTimer?.Dispose();
                _cleanupLock.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConnectionStateManager<TState>));
        }
    }
}

// Example state class
public class ChatConnectionState : IConnectionState
{
    public HashSet<string> SubscribedTopics { get; set; } = new();
    public string? UserIdentifier { get; set; }
    public DateTime LastUpdated { get; set; }
}