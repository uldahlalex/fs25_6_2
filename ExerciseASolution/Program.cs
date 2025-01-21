using System.Reflection;
using ExerciseASolution;
using Fleck;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebSocketBoilerplate;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptionsWithValidateOnStart<AppOptions>()
    .Bind(builder.Configuration.GetSection(nameof(AppOptions)));

// Register Redis connection
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AppOptions>>();
    return ConnectionMultiplexer.Connect(options.Value.DragonFlyConnectionString);
});

// Register WebSocket and state management services
builder.Services.AddSingleton<IConnectionStateManager<ChatConnectionState>, ConnectionStateManager<ChatConnectionState>>();
builder.Services.AddSingleton<IWebSocketConnectionManager, WebSocketConnectionManager>();

builder.Services.InjectEventHandlers(Assembly.GetExecutingAssembly());

var app = builder.Build();

var server = new WebSocketServer("ws://0.0.0.0:8181");
var manager = app.Services.GetRequiredService<IWebSocketConnectionManager>();
server.Start(socket =>
{
    socket.OnOpen = () => manager.Connect(socket);
    socket.OnClose = () => manager.Disconnect(socket);
    try
    {
        socket.OnMessage = async message => await app.CallEventHandler(socket, message);
    }
    catch (Exception e)
    {
        Console.WriteLine(e.Message);
        Console.WriteLine(e.InnerException);
        Console.WriteLine(e.StackTrace);
    }
    
});


app.Run();