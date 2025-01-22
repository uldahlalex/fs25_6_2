using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution;

public class CustomWebSocketServer(
    WebSocketManager wsManager,
    ILogger<CustomWebSocketServer> logger)
{
    public void Start(WebApplication app)
    {
        var server = new WebSocketServer("ws://0.0.0.0:8181");

        server.Start(socket =>
        {
            socket.OnOpen = async () =>  await wsManager.OnNewConnection(socket);
            socket.OnClose = async () => await wsManager.OnNewConnection(socket);
            socket.OnMessage = async message =>
            {
                try
                {
                    await app.CallEventHandler(socket, message); 
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while handling message");
                }

            };
        });
    }
}