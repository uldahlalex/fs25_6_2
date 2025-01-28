using System.Runtime.Serialization;
using System.Security.Authentication;
using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToBroadcastToTopicDto : BaseDto
{
    public string Message { get; set; }
    public string RequestId { get; set; }
    public string Topic { get; set; }
}

public class ServerConfirmsDto : BaseDto
{
    public string RequestId { get; set; }
}

public class ServerBroadcastsMessageDto : BaseDto
{
    public string Message { get; set; }
    public string Sender { get; set; }
    public string Topic { get; set; }
}

public class ClientWantsToBroadcastMessageEventHandler(
    WebSocketManager webSocketManager,
    SecurityService securityService) : BaseEventHandler<ClientWantsToBroadcastToTopicDto>
{
    public override async Task Handle(ClientWantsToBroadcastToTopicDto dto, IWebSocketConnection socket)
    {
        var userIdByConnection = await webSocketManager.GetUserIdByConnection(socket.ConnectionInfo.Id.ToString()) ??
                                 throw new AuthenticationException("User not authenticated!!");

        var broadcast = new ServerBroadcastsMessageDto()
        {
            Sender = userIdByConnection,
            Message = dto.Message,
            Topic = dto.Topic
        };
        await webSocketManager.BroadcastToTopic(dto.Topic, broadcast);
        socket.SendDto(new ServerConfirmsDto()
        {
            RequestId = dto.RequestId
        });
    }
}