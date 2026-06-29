namespace Dmon.Providers.Mlx;

/// <summary>
/// DI service keys for the two registered MLX runtimes.
/// Group 6 (daemon scheduler) resolves runtimes by these keys for warm/stop.
/// Group 7 (daemon routing) uses them in <c>sp.MlxClient(key)</c> for client construction.
/// </summary>
public static class MlxRuntimeKeys
{
    public const string Firstline = "firstline";
    public const string Escalation = "escalation";
}
