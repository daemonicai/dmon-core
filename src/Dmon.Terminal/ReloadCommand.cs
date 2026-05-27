namespace Dmon.Terminal;

/// <summary>
/// Client-side marker — not sent to core. Triggers a core restart via /reload.
/// </summary>
internal sealed record ReloadCommand;
