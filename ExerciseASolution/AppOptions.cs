namespace ExerciseASolution;

public class AppOptions
{
    public string REDIS_HOST { get; set; }
    public string REDIS_USERNAME { get; set; }
    public string REDIS_PASS { get; set; }
    public string StateKeyPrefix { get; set; } = "websocket";
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public string JwtSecret { get; set; }
}