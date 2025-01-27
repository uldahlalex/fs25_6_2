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
builder.Services.AddSingleton<IProxyConfig, ProxyConfig>();


var appOptions = builder.Configuration.GetSection(nameof(AppOptions)).Get<AppOptions>();
var redisConfig = new ConfigurationOptions
{
    AbortOnConnectFail = false
};

//For production deployment with gcloud avoid using comma separated connectionstring
if (appOptions.DragonFlyConnectionString.StartsWith("rediss://") ||
    appOptions.DragonFlyConnectionString.StartsWith("redis://"))
{
    redisConfig.Ssl = appOptions.DragonFlyConnectionString.StartsWith("rediss://");
    redisConfig.EndPoints.Add(appOptions.DragonFlyConnectionString);
}
else
{
    redisConfig = ConfigurationOptions.Parse(appOptions.DragonFlyConnectionString);
}

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConfig));
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
app.Services.GetRequiredService<IProxyConfig>().StartProxyServer(publicPort, restPort, wsPort);
app.Services.GetRequiredService<CustomWebSocketServer>().Start(app);

app.Run();

//Example WS connection: https://fs25-267099996159.europe-north1.run.app