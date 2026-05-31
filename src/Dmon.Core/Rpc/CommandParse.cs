using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

internal abstract record CommandParse;

internal sealed record ParsedCommand(Command Command) : CommandParse;

internal sealed record ParseFault(ErrorEvent Error) : CommandParse;
