using Fleck;
using StackExchange.Redis;
using System.Text.Json;
using ExerciseASolution;
using Microsoft.Extensions.Options;


public interface IWebSocketConnectionManager
{
    Task<string> Connect(IWebSocketConnection socket);
    Task Disconnect(IWebSocketConnection socket);
    Task BroadcastMessage(string message);
    Task SendToConnection(string connectionId, string message);
    string GetConnectionId(IWebSocketConnection socket);
}

public class WebSocketConnectionManager : IWebSocketConnectionManager
{
    private readonly ConnectionMultiplexer _db;
    private readonly ISubscriber _subscriber;
    private readonly Dictionary<string, IWebSocketConnection> _localConnections;
    private readonly string _serverInstanceId;
    private readonly ILogger<WebSocketConnectionManager> _logger;

    public class ConnectionMetadata
    {
        public string ConnectionId { get; set; }
        public string ServerInstanceId { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public WebSocketConnectionManager(
        IOptions<AppOptions> options,
        ILogger<WebSocketConnectionManager> logger)
    {
        _logger = logger;
        try
        {
            _db = ConnectionMultiplexer.Connect(options.Value.DragonFlyConnectionString);
            _subscriber = _db.GetSubscriber();
            _localConnections = new Dictionary<string, IWebSocketConnection>();
            _serverInstanceId = Guid.NewGuid().ToString();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize WebSocketConnectionManager");
            throw;
        }
    }

    public async Task<string> Connect(IWebSocketConnection socket)
    {
        var connectionId = Guid.NewGuid().ToString();
        
        // Store connection metadata in DragonflyDB
        var metadata = new ConnectionMetadata
        {
            ConnectionId = connectionId,
            ServerInstanceId = _serverInstanceId,
            LastSeen = DateTime.UtcNow
        };

        var db = _db.GetDatabase();
        await db.StringSetAsync(
            $"websocket:connection:{connectionId}",
            JsonSerializer.Serialize(metadata)
        );

        // Keep track of local connections
        _localConnections[connectionId] = socket;

        // Subscribe to messages for this connection
        await _subscriber.SubscribeAsync($"websocket:messages:{connectionId}", 
            (channel, message) =>
            {
                if (_localConnections.TryGetValue(connectionId, out var conn))
                {
                    conn.Send(message.ToString());
                }
            });

        return connectionId;
    }

    public async Task Disconnect(IWebSocketConnection socket)
    {
        // Find and remove the connection
        var connectionId = _localConnections.FirstOrDefault(x => x.Value == socket).Key;
        if (connectionId != null)
        {
            var db = _db.GetDatabase();
            await db.KeyDeleteAsync($"websocket:connection:{connectionId}");
            _localConnections.Remove(connectionId);
            await _subscriber.UnsubscribeAsync($"websocket:messages:{connectionId}");
        }
    }

    public async Task BroadcastMessage(string message)
    {
        var db = _db.GetDatabase();
        var connectionKeys = await db.ExecuteAsync("KEYS", "websocket:connection:*");
        
        foreach (var key in (RedisKey[])connectionKeys)
        {
            var connectionData = await db.StringGetAsync(key);
            if (!connectionData.IsNull)
            {
                var metadata = JsonSerializer.Deserialize<ConnectionMetadata>(connectionData);
                await _subscriber.PublishAsync($"websocket:messages:{metadata.ConnectionId}", message);
            }
        }
    }

    public async Task SendToConnection(string connectionId, string message)
    {
        await _subscriber.PublishAsync($"websocket:messages:{connectionId}", message);
    }

    public string GetConnectionId(IWebSocketConnection socket)
    {
        return _localConnections.FirstOrDefault(x => x.Value == socket).Key;
    }
}