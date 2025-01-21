using Fleck;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToEnterRoomDto : BaseDto
{
}

public class ServerAddsClientToRoomDto : BaseDto
{
}

public class ClientWantsToEnterRoomEventHandler : BaseEventHandler<ClientWantsToEnterRoomDto>
{
    public override Task Handle(ClientWantsToEnterRoomDto dto, IWebSocketConnection socket)
    {
        var resp = new ServerAddsClientToRoomDto();
        socket.SendDto(resp);
        return Task.CompletedTask;
    }
}