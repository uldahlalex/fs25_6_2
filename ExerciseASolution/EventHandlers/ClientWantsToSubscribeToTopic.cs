using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToSubscribToTopicDto : BaseDto
{
    public string Jwt { get; set; }
    public string Topic { get; set; }
    public string RequestId { get; set; }
}

public class ServerHasSubscribedClientToTopicDto : BaseDto
{
    public string Topic { get; set; }
    public string UserId { get; set; }
    public string requestId { get; set; }
}

public class ClientWantsToSubscribeToTopic(WebSocketManager manager, SecurityService securityService) : BaseEventHandler<ClientWantsToSubscribToTopicDto>
{
    public override async Task Handle(ClientWantsToSubscribToTopicDto dto, IWebSocketConnection socket)
    {
        var connectionId = socket.ConnectionInfo.Id.ToString();
        await manager.Subscribe(connectionId, dto.Topic);
        
        var resp = new ServerHasSubscribedClientToTopicDto
        {
            UserId = connectionId, 
            requestId = dto.RequestId,
            Topic = dto.Topic
        };
        socket.SendDto(resp);
   
    }
}