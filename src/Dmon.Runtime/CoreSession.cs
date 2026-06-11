using Dmon.Protocol.Events;

namespace Dmon.Runtime;

/// <summary>
/// The live result of a successfully started and protocol-gated dmoncore process.
/// </summary>
/// <param name="Process">The running core process (owns the stdio pipes).</param>
/// <param name="AgentReady">The parsed <c>agentReady</c> event from handshake.</param>
public sealed record CoreSession(ICoreProcess Process, AgentReadyEvent AgentReady);
