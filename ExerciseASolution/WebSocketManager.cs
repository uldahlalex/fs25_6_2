using System.Collections.Concurrent;
using System.Text.Json;
using ExerciseASolution.EventHandlers;
using Fleck;
using StackExchange.Redis;
using WebSocketBoilerplate;

public class WebSocketManager
{
    private readonly IDatabase _redis;
    private readonly ConcurrentDictionary<string, IWebSocketConnection> _connections = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _userToConnectionsMap = new();
    private readonly ILogger<WebSocketManager> _logger;

    public WebSocketManager(IConnectionMultiplexer redis, ILogger<WebSocketManager> logger)
    {
        _logger = logger;
        _redis = redis.GetDatabase();
    }

    public async Task OnNewConnection(IWebSocketConnection socket)
    {
     
        _connections.TryAdd(socket.ConnectionInfo.Id.ToString(), socket);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!_userToConnectionsMap.Values.Any(set => set.Contains(socket.ConnectionInfo.Id.ToString())))
            {
                await CloseConnection(socket.ConnectionInfo.Id.ToString(), "Authentication timeout");
            }
        });
    }

    public async Task<List<string>> Authenticate(string connectionId, string userId)
    {
        if (!_connections.TryGetValue(connectionId, out var socket))
        {
            throw new KeyNotFoundException("Could not find connection");
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

        _logger.LogInformation("User {UserId} authenticated. Connection ID: {ConnectionId}", userId, connectionId);

        return previousTopics.Select(t => t.ToString()).ToList();

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

    public string GetUserIdByConnectionId(string connectionId)
    {
        return _redis.HashGet($"ws:conn:{connectionId}", "userId");
    }

 private readonly List<Topic> _defaultTopics = new()
    {
        new Topic { Id = "general", Name = "General", Description = "General discussion channel", Category = "Public" },
        new Topic { Id = "announcements", Name = "Announcements", Description = "Important updates and announcements", Category = "Public" },
        new Topic { Id = "support", Name = "Support", Description = "Technical support channel", Category = "Support" },
        new Topic { Id = "feedback", Name = "Feedback", Description = "Product feedback and suggestions", Category = "Support" },
        new Topic { Id = "dev-updates", Name = "Development Updates", Description = "Latest development news", Category = "Development" },
        new Topic { Id = "bugs", Name = "Bug Reports", Description = "Report and track bugs", Category = "Development" }
    };

    public async Task SeedExampleTopicsToRedis()
    {
        foreach (var topic in _defaultTopics)
        {
            await _redis.HashSetAsync($"ws:topic:{topic.Id}", new HashEntry[]
            {
                new("name", topic.Name),
                new("description", topic.Description),
                new("category", topic.Category)
            });
        }
    }

    public async Task<List<Topic>> GetAllTopics()
    {
        var topics = new List<Topic>();
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var keys = server.Keys(pattern: "ws:topic:*");

        foreach (var key in keys)
        {
            var topicHash = await _redis.HashGetAllAsync(key);
            var topicId = key.ToString().Split(':').Last();
            
            topics.Add(new Topic
            {
                Id = topicId,
                Name = topicHash.FirstOrDefault(x => x.Name == "name").Value,
                Description = topicHash.FirstOrDefault(x => x.Name == "description").Value,
                Category = topicHash.FirstOrDefault(x => x.Name == "category").Value
            });
        }

        return topics;
    }

    public async Task<List<string>> GetTopicSubscribers(string topicId)
    {
        var subscribers = await _redis.SetMembersAsync($"ws:topics:{topicId}");
        return subscribers.Select(s => s.ToString()).ToList();
    }

    public async Task<List<string>> GetUserSubscriptions(string userId)
    {
        var subscriptions = await _redis.SetMembersAsync($"ws:user:{userId}:topics");
        return subscriptions.Select(s => s.ToString()).ToList();
    }

    // Enhanced Subscribe method with validation
    public async Task Subscribe(string connectionId, string topicId)
    {
        var topicExists = await _redis.KeyExistsAsync($"ws:topic:{topicId}");
        if (!topicExists)
        {
            throw new ArgumentException($"Topic {topicId} does not exist");
        }

        var userId =  GetUserIdByConnectionId(connectionId);
        if (string.IsNullOrEmpty(userId))
        {
            throw new UnauthorizedAccessException("User must be authenticated to subscribe to topics");
        }

        await Task.WhenAll(
            _redis.SetAddAsync($"ws:topics:{topicId}", connectionId),
            _redis.SetAddAsync($"ws:user:{userId}:topics", topicId)
        );

        // Notify user of successful subscription
        if (_connections.TryGetValue(connectionId, out var socket))
        {
            var message = new
            {
                type = "subscription_success",
                topicId = topicId,
                message = $"Successfully subscribed to {topicId}"
            };
            await socket.Send(JsonSerializer.Serialize(message));
        }
    }

    // Enhanced BroadcastToTopic method with message structure
    public async Task BroadcastToTopic(string topicId, string userId, object payload)
    {
        var message = new
        {
            type = "topic_message",
            topicId = topicId,
            userId = userId,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = payload
        };

        var messageJson = JsonSerializer.Serialize(message);
        var subscriberIds = await _redis.SetMembersAsync($"ws:topics:{topicId}");
        
        var tasks = subscriberIds
            .Select(id => id.ToString())
            .Where(id => _connections.ContainsKey(id))
            .Select(id => _connections[id].Send(messageJson));

        await Task.WhenAll(tasks);

        // Optionally store message history
        await _redis.ListRightPushAsync($"ws:topic:{topicId}:messages", messageJson);
        await _redis.ListTrimAsync($"ws:topic:{topicId}:messages", -100, -1); // Keep last 100 messages
    }

    // Get recent messages for a topic
    public async Task<List<string>> GetRecentMessages(string topicId, int count = 50)
    {
        var messages = await _redis.ListRangeAsync($"ws:topic:{topicId}:messages", -count, -1);
        return messages.Select(m => m.ToString()).ToList();
    }
}

public class Topic
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Category { get; set; }
}
