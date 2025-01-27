using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToBroadcastToTopicDto : BaseDto
{
    public string Jwt { get; set; }
    public string Message { get; set; }
    public string RequestId { get; set; }
    public string Topic { get; set; }
}
public class ServerConfirmsDto : BaseDto
{
    public string RequestId { get; set; }
}

public class ClientWantsToBroadcastMessageEventHandler(WebSocketManager webSocketManager, SecurityService securityService) : BaseEventHandler<ClientWantsToBroadcastToTopicDto>
{
    public override async Task Handle(ClientWantsToBroadcastToTopicDto dto, IWebSocketConnection socket)
    {
        securityService.VerifyJwtOrThrow(dto.Jwt);
        await webSocketManager.BroadcastToTopic(dto.Topic, dto);
        socket.SendDto(new ServerConfirmsDto(){RequestId = dto.RequestId});
    }
}