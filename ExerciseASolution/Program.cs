using System.Reflection;
using System.Text.Json;
using ExerciseASolution;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Startup;
using WebSocketBoilerplate;

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
    DefaultDatabase = 0  
};

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

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var multiplexer = ConnectionMultiplexer.Connect(redisConfig);
    multiplexer.ConnectionFailed += (sender, e) =>
    {
        Console.WriteLine($"Connection failed: {e.Exception}");
    };
    return multiplexer;
});
builder.Services.AddSingleton<WebSocketManager>();
builder.Services.AddSingleton<CustomWebSocketServer>();

builder.Services.InjectEventHandlers(Assembly.GetExecutingAssembly());

var app = builder.Build();
var opts = app.Services.GetRequiredService<IOptionsMonitor<AppOptions>>().CurrentValue;
Console.WriteLine(JsonSerializer.Serialize(opts));
app.Urls.Clear();
const int restPort = 5000;
const int wsPort = 8181;
var publicPort = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080");
app.Urls.Add($"http://0.0.0.0:{restPort}");
//app.Services.GetRequiredService<IProxyConfig>().StartProxyServer(publicPort, restPort, wsPort);

app.Services.GetRequiredService<CustomWebSocketServer>().Start(app);
var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
var db = redis.GetDatabase();
var result = db.StringSet("test", "Hello, World!");
Console.WriteLine(result);

app.MapGet("/", () => "Hello, World!");

app.Run();

//Example WS connection: https://fs25-267099996159.europe-north1.run.app