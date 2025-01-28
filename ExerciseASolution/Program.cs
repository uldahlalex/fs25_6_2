using System.Reflection;
using System.Text.Json;
using ExerciseASolution;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Startup;
using WebSocketBoilerplate;

ThreadPool.SetMinThreads(250, 250); 

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptionsWithValidateOnStart<AppOptions>()
    .Bind(builder.Configuration.GetSection(nameof(AppOptions)));
builder.Services.AddSingleton<SecurityService>();
builder.Services.AddSingleton<IProxyConfig, ProxyConfig>();
var appOptions = builder.Configuration.GetSection(nameof(AppOptions)).Get<AppOptions>();
var redisConfig = new ConfigurationOptions
{
    AbortOnConnectFail = false,
    ConnectTimeout = 5000,
    SyncTimeout = 5000,
    Ssl = true,
    DefaultDatabase = 0,
    ConnectRetry = 5,
    ReconnectRetryPolicy = new ExponentialRetry(5000),
    EndPoints = { { appOptions.REDIS_HOST, 6379 } },
    User = appOptions.REDIS_USERNAME,
    Password = appOptions.REDIS_PASS
};

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var multiplexer = ConnectionMultiplexer.Connect(redisConfig);
    return multiplexer;
});
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
_ = db.StringSet("test", "Hello, World!");

app.Urls.Clear();
app.Urls.Add($"http://*:5000");
app.Run();