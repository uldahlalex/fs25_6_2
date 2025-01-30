using System.Collections.Concurrent;
using System.Text.Json;
using Fleck;
using StackExchange.Redis;
using WebSocketBoilerplate;

public class WebSocketManager
{
    private readonly IDatabase _redis;
    private readonly ConcurrentDictionary<string, IWebSocketConnection> _connectionIdToSocket = new();
    private readonly ConcurrentDictionary<string, string> _userIdToConnectionId = new();
    private readonly ILogger<WebSocketManager> _logger;

    public WebSocketManager(IConnectionMultiplexer redis, ILogger<WebSocketManager> logger)
    {
        _logger = logger;
        _redis = redis.GetDatabase();
    }

     public async Task OnConnect(IWebSocketConnection socket)
    {
        var connectionId = socket.ConnectionInfo.Id.ToString();
        _connectionIdToSocket.TryAdd(connectionId, socket);
        
        await _redis.HashSetAsync($"ws:connection:{connectionId}", new HashEntry[]
        {
            new("connected", "1")
        });
        
        _logger.LogInformation($"Connection established: {connectionId}");
    }

    public async Task AuthenticateConnection(string connectionId, string userId)
    {
        var connectionExists = await _redis.HashExistsAsync($"ws:connection:{connectionId}", "connected");
        if (!connectionExists)
        {
            throw new Exception($"Connection not found with ID {connectionId}");
        }

        if (_userIdToConnectionId.TryGetValue(userId, out var oldConnectionId))
        {
            if (oldConnectionId != connectionId)
            {
                if (_connectionIdToSocket.TryRemove(oldConnectionId, out var oldSocket))
                {
                     oldSocket.Close();
                }
                await _redis.KeyDeleteAsync($"ws:connection:{oldConnectionId}");
            }
        }

        await _redis.HashSetAsync($"ws:connection:{connectionId}", new HashEntry[]
        {
            new("connected", "1"),
            new("userId", userId)
        });

        _userIdToConnectionId.AddOrUpdate(userId, connectionId, (_, _) => connectionId);

        var userTopics = await _redis.SetMembersAsync($"ws:user:{userId}:topics");
        foreach (var topic in userTopics)
        {
            await Subscribe(connectionId, topic.ToString(), userId);
        }
    }

    public async Task Subscribe(string connectionId, string topic, string? userId = null)
    {
        var connectionExists = await _redis.HashExistsAsync($"ws:connection:{connectionId}", "connected");
        if (!connectionExists)
        {
            throw new Exception($"Connection not found with ID {connectionId}");
        }

        if (userId == null)
        {
            var userIdValue = await _redis.HashGetAsync($"ws:connection:{connectionId}", "userId");
            userId = userIdValue.HasValue ? userIdValue.ToString() : null;
        }

        var tasks = new List<Task>
        {
            _redis.SetAddAsync($"ws:topic:{topic}", connectionId),
            _redis.SetAddAsync($"ws:connection:{connectionId}:topics", topic)
        };

        if (!string.IsNullOrEmpty(userId))
        {
            tasks.Add(_redis.SetAddAsync($"ws:user:{userId}:topics", topic));
        }

        await Task.WhenAll(tasks);
    }

    public async Task OnDisconnect(string connectionId)
    {
        _connectionIdToSocket.TryRemove(connectionId, out _);
        
        // Find if this connection was associated with a user
        var userId = await _redis.HashGetAsync($"ws:connection:{connectionId}", "userId");
        if (userId.HasValue)
        {
            _userIdToConnectionId.TryRemove(userId.ToString(), out _);
            // Don't remove user's topic subscriptions - keep them for when they reconnect
        }
        else 
        {
            // If no user associated, clean up connection-specific subscriptions
            var topics = await _redis.SetMembersAsync($"ws:connection:{connectionId}:topics");
            var tasks = topics.Select(topic => 
                _redis.SetRemoveAsync($"ws:topic:{topic}", connectionId));
            
            await Task.WhenAll(tasks.Concat(new[] {
                _redis.KeyDeleteAsync($"ws:connection:{connectionId}:topics")
            }));
        }
        
        await _redis.KeyDeleteAsync($"ws:connection:{connectionId}");
    }

    

    public async Task Unsubscribe(string connectionId, string topic)
    {
        var userId = (await _redis.HashGetAsync($"ws:connection:{connectionId}", "userId")).ToString();
        
        var tasks = new List<Task>
        {
            _redis.SetRemoveAsync($"ws:topic:{topic}", connectionId),
            _redis.SetRemoveAsync($"ws:connection:{connectionId}:topics", topic)
        };

        if (!string.IsNullOrEmpty(userId))
        {
            tasks.Add(_redis.SetRemoveAsync($"ws:user:{userId}:topics", topic));
        }

        await Task.WhenAll(tasks);
    }

    public async Task BroadcastToTopic<T>(string topic, T message) where T : BaseDto
    {
        var connections = (await _redis.SetMembersAsync($"ws:topic:{topic}")  ).
            Select(conn => conn.ToString())
            .Where(connId => _connectionIdToSocket.ContainsKey(connId)).ToList();

            connections.ForEach(connId =>  _connectionIdToSocket[connId].SendDto(message));
    }

    public async Task<string?> GetUserIdByConnection(string connectionId)
    {
        var userId = await _redis.HashGetAsync($"ws:connection:{connectionId}", "userId");
        return userId.HasValue ? userId.ToString() : null;
    }

    public string? GetConnectionIdByUserId(string userId)
    {
        _userIdToConnectionId.TryGetValue(userId, out var connectionId);
        return connectionId;
    }
}