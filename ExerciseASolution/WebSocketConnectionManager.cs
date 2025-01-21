using Fleck;
using StackExchange.Redis;
using System.Text.Json;
using ExerciseASolution;
using Microsoft.Extensions.Options;



public interface IWebSocketConnectionManager : IDisposable
{
    Task<string> Connect(IWebSocketConnection socket);
    Task Disconnect(IWebSocketConnection socket);
    Task BroadcastMessage(string message);
    Task SendToConnection(string connectionId, string message);
    Task BroadcastToTopic(string topic, string message);
    Task SubscribeToTopic(string connectionId, string topic);
    Task UnsubscribeFromTopic(string connectionId, string topic);
    string GetConnectionId(IWebSocketConnection socket);
}



public class WebSocketConnectionManager : IWebSocketConnectionManager
{
    private readonly ConnectionMultiplexer _db;
    private readonly ISubscriber _subscriber;
    private readonly Dictionary<string, IWebSocketConnection> _localConnections;
    private readonly string _serverInstanceId;
    private readonly ILogger<WebSocketConnectionManager> _logger;
    private readonly IConnectionStateManager<ChatConnectionState> _stateManager;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public WebSocketConnectionManager(
        IOptions<AppOptions> options,
        ILogger<WebSocketConnectionManager> logger, 
        IConnectionStateManager<ChatConnectionState> connectionStateManager)
    {
        _logger = logger;
        try
        {
            _db = ConnectionMultiplexer.Connect(options.Value.DragonFlyConnectionString);
            _subscriber = _db.GetSubscriber();
            _localConnections = new Dictionary<string, IWebSocketConnection>();
            _serverInstanceId = Guid.NewGuid().ToString();

            _stateManager = connectionStateManager;

            SubscribeToServerBroadcasts();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize WebSocketConnectionManager");
            throw;
        }
    }

    private void SubscribeToServerBroadcasts()
    {
        _subscriber.Subscribe("websocket:broadcast", (channel, message) =>
        {
            BroadcastToLocalConnections(message.ToString());
        });
    }

    public async Task<string> Connect(IWebSocketConnection socket)
    {
        var connectionId = Guid.NewGuid().ToString();
        
        try
        {
            await _connectionLock.WaitAsync();
            
            // Initialize connection state
            await _stateManager.SetState(connectionId, new ChatConnectionState
            {
                SubscribedTopics = new HashSet<string>(),
                LastUpdated = DateTime.UtcNow
            });

         

            var db = _db.GetDatabase();
            await db.StringSetAsync(
                $"websocket:connection:{connectionId}",
                JsonSerializer.Serialize(new {ConnectionId = connectionId})
            );

            // Store local connection
            _localConnections[connectionId] = socket;

            // Set up message handling for this connection
            await SubscribeToConnectionMessages(connectionId);

            _logger.LogInformation("New connection established: {ConnectionId}", connectionId);
            return connectionId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish connection for {ConnectionId}", connectionId);
            await CleanupConnection(connectionId);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task Disconnect(IWebSocketConnection socket)
    {
        var connectionId = GetConnectionId(socket);
        if (connectionId == null) return;

        try
        {
            await _connectionLock.WaitAsync();
            
            // Get state to unsubscribe from topics
            var state = await _stateManager.GetState(connectionId);
            if (state != null)
            {
                foreach (var topic in state.SubscribedTopics)
                {
                    await _subscriber.UnsubscribeAsync($"topic:{topic}");
                }
            }

            await CleanupConnection(connectionId);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task BroadcastMessage(string message)
    {
        await _subscriber.PublishAsync("websocket:broadcast", message);
    }

    public async Task SendToConnection(string connectionId, string message)
    {
        await _subscriber.PublishAsync($"websocket:messages:{connectionId}", message);
    }

    public async Task BroadcastToTopic(string topic, string message)
    {
        await _subscriber.PublishAsync($"topic:{topic}", message);
    }

    public async Task SubscribeToTopic(string connectionId, string topic)
    {
        var state = await _stateManager.GetState(connectionId);
        if (state == null) return;

        try
        {
            await _connectionLock.WaitAsync();
            
            state.SubscribedTopics.Add(topic);
            await _stateManager.SetState(connectionId, state);
            
            await _subscriber.SubscribeAsync($"topic:{topic}", (channel, message) =>
            {
                if (_localConnections.TryGetValue(connectionId, out var connection))
                {
                    connection.Send(message.ToString());
                }
            });
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task UnsubscribeFromTopic(string connectionId, string topic)
    {
        var state = await _stateManager.GetState(connectionId);
        if (state == null) return;

        try
        {
            await _connectionLock.WaitAsync();
            
            state.SubscribedTopics.Remove(topic);
            await _stateManager.SetState(connectionId, state);
            
            await _subscriber.UnsubscribeAsync($"topic:{topic}");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public string GetConnectionId(IWebSocketConnection socket)
    {
        return _localConnections
            .FirstOrDefault(x => x.Value == socket)
            .Key;
    }

    private async Task SubscribeToConnectionMessages(string connectionId)
    {
        await _subscriber.SubscribeAsync(
            $"websocket:messages:{connectionId}",
            (channel, message) =>
            {
                try
                {
                    if (_localConnections.TryGetValue(connectionId, out var connection))
                    {
                        connection.Send(message.ToString());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling message for connection {ConnectionId}", connectionId);
                }
            });
    }

    private async Task CleanupConnection(string connectionId)
    {
        try
        {
            var db = _db.GetDatabase();
            await db.KeyDeleteAsync($"websocket:connection:{connectionId}");
            await _stateManager.RemoveState(connectionId);
            
            if (_localConnections.ContainsKey(connectionId))
            {
                _localConnections.Remove(connectionId);
            }
            
            await _subscriber.UnsubscribeAsync($"websocket:messages:{connectionId}");
            
            _logger.LogInformation("Connection cleaned up: {ConnectionId}", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up connection {ConnectionId}", connectionId);
        }
    }

    private void BroadcastToLocalConnections(string message)
    {
        foreach (var connection in _localConnections.Values)
        {
            try
            {
                connection.Send(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting to connection");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _subscriber?.UnsubscribeAll();
            _db?.Dispose();
            _connectionLock?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing WebSocketConnectionManager");
        }
    }

    // Optional: Add heartbeat mechanism
    public async Task UpdateConnectionHeartbeat(string connectionId)
    {
        var state = await _stateManager.GetState(connectionId);
        if (state != null)
        {
            state.LastUpdated = DateTime.UtcNow;
            await _stateManager.SetState(connectionId, state);
        }
    }

    // Optional: Add connection statistics
    public async Task<ConnectionStats> GetConnectionStats()
    {
        var db = _db.GetDatabase();
        var keys = await db.ExecuteAsync("KEYS", "websocket:connection:*");
        
        return new ConnectionStats
        {
            TotalConnections = ((RedisKey[])keys).Length,
            LocalConnections = _localConnections.Count,
            ServerInstanceId = _serverInstanceId
        };
    }
}

public class ConnectionStats
{
    public int TotalConnections { get; set; }
    public int LocalConnections { get; set; }
    public string ServerInstanceId { get; set; }
}