using System.Security.Authentication;
using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToSubscribeToTopicDto : BaseDto
{
    public string Topic { get; set; }
    public string RequestId { get; set; }
}

public class ServerHasSubscribedClientToTopicDto : BaseDto
{
    public string Topic { get; set; }
    public string UserId { get; set; }
    public string requestId { get; set; }
}

public class ClientWantsToSubscribeToTopic(WebSocketManager webSocketManager, SecurityService securityService) : BaseEventHandler<ClientWantsToSubscribeToTopicDto>
{
    public override async Task Handle(ClientWantsToSubscribeToTopicDto dto, IWebSocketConnection socket)
    {
        var userIdByConnection = await webSocketManager.GetUserIdByConnection(socket.ConnectionInfo.Id.ToString()) ??
                                 throw new AuthenticationException("User not authenticated!!");
        await webSocketManager.Subscribe(socket.ConnectionInfo.Id.ToString(), dto.Topic);
        
        var resp = new ServerHasSubscribedClientToTopicDto
        {
            UserId = userIdByConnection, 
            requestId = dto.RequestId,
            Topic = dto.Topic
        };
        socket.SendDto(resp);
   
    }
}