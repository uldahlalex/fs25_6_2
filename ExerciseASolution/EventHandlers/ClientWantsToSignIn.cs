using System.Security.Claims;
using System.Text;
using Fleck;
using JWT;
using JWT.Algorithms;
using JWT.Builder;
using JWT.Serializers;
using Microsoft.Extensions.Options;
using WebSocketBoilerplate;

namespace ExerciseASolution.EventHandlers;

public class ClientWantsToSignInDto : BaseDto
{
    public string Username { get; set; }
    public string Password { get; set; }
    public string RequestId { get; set; }
}

public class ServerAuthenticatesClientDto : BaseDto
{
    public string RequestId { get; set; }
    public string Jwt { get; set; }
}

public class ClientWantsToSignInEventHandler(SecurityService securityService, WebSocketManager webSocketManager)
    : BaseEventHandler<ClientWantsToSignInDto>
{
    public override async Task Handle(ClientWantsToSignInDto dto, IWebSocketConnection socket)
    {
        var connectionId = socket.ConnectionInfo.Id.ToString();
        var userId = Guid.NewGuid(); //No persisted user - we'll just assign an ID
        await webSocketManager.AuthenticateConnection(connectionId, userId.ToString());
        var jwt = securityService.GenerateJwt(dto.Username);
        socket.SendDto(new ServerAuthenticatesClientDto()
        {
            RequestId = dto.RequestId,
            Jwt = jwt
        });
    }


}