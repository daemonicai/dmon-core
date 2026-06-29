namespace Dmon.Providers.Mlx;

public sealed class MlxRuntimeState
{
    public string BaseUrl { get; set; } = string.Empty;
    public bool OwnsProcess { get; set; }
    public bool? ToolCallingVerified { get; set; }
}
