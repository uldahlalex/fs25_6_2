namespace ExerciseASolution;

public class AppOptions
{
    public required string DragonFlyConnectionString { get; set; }
    public string StateKeyPrefix { get; set; } = "websocket";
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public string JwtSecret { get; set; }
}