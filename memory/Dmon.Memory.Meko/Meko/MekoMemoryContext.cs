using Dmon.Abstractions.Memory;

namespace Dmon.Memory.Meko;

/// <summary>
/// Constructs and holds the ambient <see cref="MemoryContext"/> for a Meko session.
/// Bound once per session from <see cref="MekoLongTermOptions"/>:
/// <list type="bullet">
///   <item><description><c>DatapackId</c> — from <see cref="MekoLongTermOptions.DatapackId"/>.</description></item>
///   <item><description><c>AgentId</c> — hardcoded to <c>"dmon"</c>.</description></item>
///   <item><description><c>ConversationId</c> — from <see cref="MekoLongTermOptions.SessionId"/>.</description></item>
/// </list>
/// <c>run_id</c> is NEVER set (D9).
/// </summary>
internal sealed class MekoMemoryContext
{
    private const string AgentId = "dmon";

    public MemoryContext Context { get; }

    public MekoMemoryContext(MekoLongTermOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Context = new MemoryContext(
            DatapackId: options.DatapackId,
            AgentId: AgentId,
            ConversationId: options.SessionId);
    }
}
