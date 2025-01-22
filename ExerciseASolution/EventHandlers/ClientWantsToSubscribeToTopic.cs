using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToSubscribToTopicDto : BaseDto
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

public class ClientWantsToSubscribeToTopic(WebSocketManager manager) : BaseEventHandler<ClientWantsToSubscribToTopicDto>
{
    public override async Task Handle(ClientWantsToSubscribToTopicDto dto, IWebSocketConnection socket)
    {
       await manager.Subscribe(socket.ConnectionInfo.Id.ToString(), dto.Topic);
        var resp = new ServerHasSubscribedClientToTopicDto
        {
            UserId = await manager.GetUserIdByConnection(socket.ConnectionInfo.Id.ToString()) ?? throw new Exception("User not found"),
            requestId = dto.RequestId,
            Topic = dto.Topic
        };
        socket.SendDto(resp);
   
    }
}