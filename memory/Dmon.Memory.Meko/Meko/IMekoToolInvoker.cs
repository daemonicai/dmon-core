using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko;

/// <summary>
/// Testability seam over the live MCP client. <see cref="MekoToolInvoker"/> is the
/// real implementation; tests supply a hand-rolled fake. <c>ILongTermMemory</c>
/// implementations depend on this interface, not on <c>McpClient</c> directly.
/// </summary>
internal interface IMekoToolInvoker
{
    /// <summary>
    /// Invokes a Meko MCP tool and returns the raw tool result.
    /// </summary>
    /// <param name="tool">The tool name (e.g. <c>"memory_add"</c>).</param>
    /// <param name="args">Arguments to pass to the tool.</param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task<CallToolResult> CallToolAsync(
        string tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default);
}
