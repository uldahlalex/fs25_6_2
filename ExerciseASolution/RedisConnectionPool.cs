using StackExchange.Redis;

public class RedisConnectionPool
{
    private readonly List<IConnectionMultiplexer> _connections;
    private int _round = 0;

    public RedisConnectionPool(ConfigurationOptions config, int poolSize = 3)
    {
        _connections = new List<IConnectionMultiplexer>();
        for (int i = 0; i < poolSize; i++)
        {
            var multiplexer = ConnectionMultiplexer.Connect(config);
            multiplexer.ConnectionFailed += (sender, e) =>
            {
                Console.WriteLine($"Connection failed: {e.Exception}");
            };
            _connections.Add(multiplexer);
        }
    }

    public IConnectionMultiplexer GetConnection()
    {
        var connection = _connections[_round % _connections.Count];
        Interlocked.Increment(ref _round);
        return connection;
    }
}