using JWT;
using JWT.Algorithms;
using JWT.Builder;
using JWT.Serializers;
using Microsoft.Extensions.Options;

namespace ExerciseASolution;

public class SecurityService(IOptionsMonitor<AppOptions> optionsMonitor)
{
    public string GenerateJwt(string username)
        => new JwtBuilder()
            .WithAlgorithm(new HMACSHA256Algorithm())
            .WithSecret(optionsMonitor.CurrentValue.JwtSecret)
            .WithUrlEncoder(new JwtBase64UrlEncoder())
            .WithJsonSerializer(new JsonNetSerializer())
            .AddClaim("username", username)
            .Encode();


    public void VerifyJwtOrThrow(string jwt)
        => new JwtBuilder()
            .WithAlgorithm(new HMACSHA256Algorithm()) 
            .WithSecret(optionsMonitor.CurrentValue.JwtSecret)
            .WithUrlEncoder(new JwtBase64UrlEncoder()) 
            .WithJsonSerializer(new JsonNetSerializer())
            .MustVerifySignature().Decode(jwt);
}