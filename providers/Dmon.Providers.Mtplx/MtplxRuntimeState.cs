namespace Dmon.Providers.Mtplx;

public sealed class MtplxRuntimeState
{
    public string BaseUrl { get; set; } = string.Empty;
    public bool OwnsProcess { get; set; }
    public bool? ToolCallingVerified { get; set; }

    // Set from /v1/models when MtplxOptions.ModelId is unset; used as the probe and factory default.
    public string? ActiveModelId { get; set; }
}
