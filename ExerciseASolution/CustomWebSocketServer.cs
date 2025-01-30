using ExerciseASolution.EventHandlers;
using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution;

public class CustomWebSocketServer(
    WebSocketManager wsManager,
    ILogger<CustomWebSocketServer> logger)
{
    public void Start(WebApplication app)
    {
        var server = new WebSocketServer("ws://0.0.0.0:8080");

        server.Start(socket =>
        {
            socket.OnOpen = async () =>  await wsManager.OnConnect(socket);
            socket.OnClose = async () => await wsManager.OnDisconnect(socket.ConnectionInfo.Id.ToString());
            socket.OnMessage = async message =>
            {
                try
                {
                    await app.CallEventHandler(socket, message); 
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while handling message");
                    socket.SendDto(new ServerSendsErrorMessagesDto()
                    {
                        Error = e.Message
                    });
                }

            };
        });
    }
}