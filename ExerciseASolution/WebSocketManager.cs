using System.Collections.Concurrent;
using System.Text.Json;
using Fleck;
using StackExchange.Redis;

public class WebSocketManager
{
    private readonly IDatabase _redis;
    private readonly ConcurrentDictionary<string, IWebSocketConnection> _sockets = new();
    private readonly ILogger<WebSocketManager> _logger;

    public WebSocketManager(IConnectionMultiplexer redis, ILogger<WebSocketManager> logger)
    {
        _logger = logger;
        _redis = redis.GetDatabase();
    }
    
 
    public async Task OnConnect(IWebSocketConnection socket)
    {
        var connectionId = socket.ConnectionInfo.Id.ToString();
        _sockets.TryAdd(connectionId, socket);
        
        // Simply mark the connection as active
        await _redis.StringSetAsync($"ws:connection:{connectionId}", "1");
        
        _logger.LogInformation($"Connection established: {connectionId}");
    }

    public async Task OnDisconnect(string connectionId)
    {
        _sockets.TryRemove(connectionId, out _);
        
        // Get all topics this connection was subscribed to
        var topics = await _redis.SetMembersAsync($"ws:connection:{connectionId}:topics");
        
        // Remove connection from all topics and cleanup
        var tasks = topics.Select(topic => 
            _redis.SetRemoveAsync($"ws:topic:{topic}", connectionId));
        
        await Task.WhenAll(tasks.Concat(new[] {
            _redis.KeyDeleteAsync($"ws:connection:{connectionId}:topics"),
            _redis.KeyDeleteAsync($"ws:connection:{connectionId}")
        }));
    }
    
    public async Task Subscribe(string connectionId, string topic)
    {
        var connectionExists = await _redis.StringGetAsync($"ws:connection:{connectionId}");
        if (!connectionExists.HasValue)
        {
            throw new Exception($"Connection not found with ID {connectionId}");
        }

        await Task.WhenAll(
            _redis.SetAddAsync($"ws:topic:{topic}", connectionId),
            _redis.SetAddAsync($"ws:connection:{connectionId}:topics", topic)
        );
    }

    public async Task Unsubscribe(string connectionId, string topic)
    {
        await Task.WhenAll(
            _redis.SetRemoveAsync($"ws:topic:{topic}", connectionId),
            _redis.SetRemoveAsync($"ws:connection:{connectionId}:topics", topic)
        );
    }

    public async Task BroadcastToTopic(string topic, object message)
    {
        var connections = await _redis.SetMembersAsync($"ws:topic:{topic}");
        var json = JsonSerializer.Serialize(message);
        
        var tasks = connections
            .Select(conn => conn.ToString())
            .Where(connId => _sockets.ContainsKey(connId))
            .Select(connId => _sockets[connId].Send(json));
        
        await Task.WhenAll(tasks);
    }

    public async Task<string[]> GetTopicsForConnection(string connectionId)
    {
        var topics = await _redis.SetMembersAsync($"ws:connection:{connectionId}:topics");
        return topics.Select(t => t.ToString()).ToArray();
    }

    public async Task<string[]> GetTopicSubscribers(string topic)
    {
        var subscribers = await _redis.SetMembersAsync($"ws:topic:{topic}");
        return subscribers.Select(s => s.ToString()).ToArray();
    }
}