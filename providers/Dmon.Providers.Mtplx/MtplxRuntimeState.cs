namespace Dmon.Providers.Mtplx;

public sealed class MtplxRuntimeState
{
    public string BaseUrl { get; set; } = string.Empty;
    public bool OwnsProcess { get; set; }
    public bool? ToolCallingVerified { get; set; }
}
