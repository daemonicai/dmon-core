namespace Dmon.Providers.LlamaCpp;

public sealed class LlamaCppRuntimeState
{
    public int Port { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public bool? ToolCallingVerified { get; set; }
}
