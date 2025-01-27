using System.Reflection;
using System.Text.Json;
using ExerciseASolution;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Startup;
using WebSocketBoilerplate;

// Add this before creating the ConnectionMultiplexer
ThreadPool.SetMinThreads(250, 250); // Adjust values based on your needs

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptionsWithValidateOnStart<AppOptions>()
    .Bind(builder.Configuration.GetSection(nameof(AppOptions)));
builder.Services.AddSingleton<SecurityService>();
builder.Services.AddSingleton<IProxyConfig, ProxyConfig>();
var appOptions = builder.Configuration.GetSection(nameof(AppOptions)).Get<AppOptions>();

var redisConfig = new ConfigurationOptions
{
    AbortOnConnectFail = false,
    ConnectTimeout = 15000,
    SyncTimeout = 15000,
    Ssl = true,
    DefaultDatabase = 0,
    ConnectRetry = 5,
    ReconnectRetryPolicy = new ExponentialRetry(5000),
    KeepAlive = 60,
    // For Render.com Redis, you don't need VPC settings
    // Just use the provided connection string
    AllowAdmin = false,
    ClientName = "MyApp",
    ResolveDns = true // Important for cloud hosting
};

// Parse the connection string from Render.com
if (appOptions.DragonFlyConnectionString.StartsWith("rediss://"))
{
    var uri = new Uri(appOptions.DragonFlyConnectionString);
    redisConfig.EndPoints.Add(uri.Host, uri.Port);
    
    var userInfo = uri.UserInfo.Split(':');
    if (userInfo.Length > 1)
    {
        redisConfig.User = userInfo[0];      
        redisConfig.Password = userInfo[1];   
    }
}
builder.Services.AddSingleton<RedisConnectionPool>(sp => 
    new RedisConnectionPool(redisConfig));

builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
    sp.GetRequiredService<RedisConnectionPool>().GetConnection());

builder.Services.AddSingleton<WebSocketManager>();
builder.Services.AddSingleton<CustomWebSocketServer>();

builder.Services.InjectEventHandlers(Assembly.GetExecutingAssembly());

var app = builder.Build();
var opts = app.Services.GetRequiredService<IOptionsMonitor<AppOptions>>().CurrentValue;

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("APPOPTIONS START:");
logger.LogInformation(JsonSerializer.Serialize(opts));
logger.LogInformation("APPOPTIONS END");

app.Services.GetRequiredService<CustomWebSocketServer>().Start(app);
var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
var db = redis.GetDatabase();
var result = db.StringSet("test", "Hello, World!");


app.Run();
