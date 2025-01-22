using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToAuthenticateDto : BaseDto
{
    public string UserId { get; set; }
    public string Jwt { get; set; }
    public string requestId { get; set; }
}

public class ServerAuthenticatedClientDto : BaseDto
{
    public bool Success { get; set; }
    public List<string> Topics { get; set; } = new List<string>();
    public string UserId { get; set; }
    public string requestId { get; set; }
}

public class ClientWantsToAuthenticateEventHandler(WebSocketManager webSocketManager)
    : BaseEventHandler<ClientWantsToAuthenticateDto>
{
    public override async Task Handle(ClientWantsToAuthenticateDto dto, IWebSocketConnection socket)
    {
        if (string.IsNullOrEmpty(dto.Jwt))
        {
            socket.SendDto(new ServerAuthenticatedClientDto()
            {
                Success = false,
                requestId = dto.requestId
            });
            return;
        }

        var topics = await webSocketManager.Authenticate(socket.ConnectionInfo.Id.ToString(), dto.UserId);
        socket.SendDto((new ServerAuthenticatedClientDto()
        {
            UserId = dto.UserId,
            requestId = dto.requestId,
            Topics = topics
        }));
    }
}