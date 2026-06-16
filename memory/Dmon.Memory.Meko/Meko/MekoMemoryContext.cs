using Dmon.Abstractions.Memory;

namespace Dmon.Memory.Meko;

/// <summary>
/// Constructs and holds the ambient <see cref="MemoryContext"/> for a Meko session.
/// Bound once per session from <see cref="MekoLongTermOptions"/>:
/// <list type="bullet">
///   <item><description><c>DatapackId</c> — from <see cref="MekoLongTermOptions.DatapackId"/> (sent only when it is a valid UUID).</description></item>
///   <item><description><c>AgentId</c> — hardcoded to <c>"dmon"</c>.</description></item>
///   <item><description><c>ConversationId</c> — the dmon session id from <see cref="MekoLongTermOptions.SessionId"/>.
///     Used as Meko's <c>run_id</c> when scope is <see cref="MemoryScope.Session"/>; the Meko
///     <c>conversation_id</c> UUID is obtained separately via <c>conversation_create</c> and
///     cached in <see cref="MekoLongTermMemory"/>.</description></item>
/// </list>
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
