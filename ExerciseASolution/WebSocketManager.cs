using System.Collections.Concurrent;
using System.Text.Json;
using Fleck;
using StackExchange.Redis;

public class WebSocketManager
{
    private readonly IDatabase _redis;
    private readonly ConcurrentDictionary<string, IWebSocketConnection> _connections = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _userToConnectionsMap = new();

    public WebSocketManager(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    public async Task OnNewConnection(IWebSocketConnection socket)
    {
        var connectionId = Guid.NewGuid().ToString();
        _connections.TryAdd(connectionId, socket);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!_userToConnectionsMap.Values.Any(set => set.Contains(connectionId)))
            {
                await CloseConnection(connectionId, "Authentication timeout");
            }
        });
    }

    public async Task Authenticate(string connectionId, string userId)
    {
        if (!_connections.TryGetValue(connectionId, out var socket))
        {
            return;
        }

        // Add new connection to user's set of connections
        _userToConnectionsMap.AddOrUpdate(
            userId,
            new HashSet<string> { connectionId },
            (_, existingSet) =>
            {
                existingSet.Add(connectionId);
                return existingSet;
            });

        // Store in Redis using Sets to maintain multiple connections
        await Task.WhenAll(
            _redis.SetAddAsync($"ws:user:{userId}:connections", connectionId),
            _redis.HashSetAsync($"ws:conn:{connectionId}", new HashEntry[]
            {
                new HashEntry("userId", userId),
                new HashEntry("connectedAt", DateTime.UtcNow.Ticks)
            })
        );

        // Restore user's previous topics for this connection
        var previousTopics = await _redis.SetMembersAsync($"ws:user:{userId}:topics");
        foreach (var topic in previousTopics)
        {
            await Subscribe(connectionId, topic.ToString());
        }

        await socket.Send(JsonSerializer.Serialize(new 
        { 
            type = "auth_success",
            userId = userId,
            topics = previousTopics.Select(t => t.ToString()).ToList()
        }));
    }

    private async Task CloseConnection(string connectionId, string reason)
    {
        if (_connections.TryGetValue(connectionId, out var socket))
        {
            var userId = await _redis.HashGetAsync($"ws:conn:{connectionId}", "userId");
            
            if (!userId.IsNull)
            {
                // Remove this connection from user's set of connections
                if (_userToConnectionsMap.TryGetValue(userId.ToString(), out var connections))
                {
                    connections.Remove(connectionId);
                    if (connections.Count == 0)
                    {
                        _userToConnectionsMap.TryRemove(userId.ToString(), out _);
                    }
                }
                
                await _redis.SetRemoveAsync($"ws:user:{userId}:connections", connectionId);
            }

            try
            {
                await socket.Send(JsonSerializer.Serialize(new { type = "close", reason }));
                socket.Close();
            }
            catch { }
        }
    }

    public async Task OnDisconnect(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out _))
        {
            var userId = await _redis.HashGetAsync($"ws:conn:{connectionId}", "userId");
            
            if (!userId.IsNull)
            {
                if (_userToConnectionsMap.TryGetValue(userId.ToString(), out var connections))
                {
                    connections.Remove(connectionId);
                    if (connections.Count == 0)
                    {
                        _userToConnectionsMap.TryRemove(userId.ToString(), out _);
                    }
                }
            }

            await CleanupConnection(connectionId);
        }
    }

    private async Task CleanupConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out _))
        {
            await Task.WhenAll(
                _redis.KeyDeleteAsync($"ws:conn:{connectionId}"),
                _redis.SetRemoveAsync("ws:topics:*", connectionId)
            );
        }
    }

  public async Task Subscribe(string connectionId, string topic)
    {
        // Add connection to topic
        await _redis.SetAddAsync($"ws:topics:{topic}", connectionId);
        
        // Get userId for this connection
        var userId = await _redis.HashGetAsync($"ws:conn:{connectionId}", "userId");
        if (!userId.IsNull)
        {
            // Store in user's permanent topic list
            await _redis.SetAddAsync($"ws:user:{userId}:topics", topic);
        }
    }

    public async Task Unsubscribe(string connectionId, string topic)
    {
        // Remove connection from topic
        await _redis.SetRemoveAsync($"ws:topics:{topic}", connectionId);
        
        // Get userId for this connection
        var userId = await _redis.HashGetAsync($"ws:conn:{connectionId}", "userId");
        if (!userId.IsNull)
        {
            // Check if any other connections from this user are still subscribed
            var userConnections = await _redis.SetMembersAsync($"ws:user:{userId}:connections");
            var hasOtherSubscriptions = false;
            
            foreach (var conn in userConnections)
            {
                if (conn != connectionId)
                {
                    var isSubscribed = await _redis.SetContainsAsync($"ws:topics:{topic}", conn);
                    if (isSubscribed)
                    {
                        hasOtherSubscriptions = true;
                        break;
                    }
                }
            }

            // Only remove from user's topics if no other connections are subscribed
            if (!hasOtherSubscriptions)
            {
                await _redis.SetRemoveAsync($"ws:user:{userId}:topics", topic);
            }
        }
    }

    public async Task BroadcastToTopic(string topic, string message)
    {
        var subscriberIds = await _redis.SetMembersAsync($"ws:topics:{topic}");
        
        var tasks = subscriberIds
            .Select(id => id.ToString())
            .Where(id => _connections.ContainsKey(id))
            .Select(id => _connections[id].Send(message));

        await Task.WhenAll(tasks);
    }
    // Helper to get all connections for a user
    public IEnumerable<string> GetConnectionsByUserId(string userId)
    {
        return _userToConnectionsMap.TryGetValue(userId, out var connections) 
            ? connections 
            : Enumerable.Empty<string>();
    }
}