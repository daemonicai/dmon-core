using Dmon.Core.Rpc;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Tests.Rpc;

public sealed class ParseCommandTests
{
    // ── Valid commands ────────────────────────────────────────────────────────

    [Fact]
    public void ParseCommand_ValidAbort_ReturnsParsedCommand()
    {
        CommandParse result = CommandDispatcher.ParseCommand(@"{""id"":""r1"",""type"":""turn.abort""}");

        ParsedCommand parsed = Assert.IsType<ParsedCommand>(result);
        TurnAbortCommand cmd = Assert.IsType<TurnAbortCommand>(parsed.Command);
        Assert.Equal("r1", cmd.Id);
    }

    [Fact]
    public void ParseCommand_ValidSubmit_ReturnsParsedCommandWithPayload()
    {
        CommandParse result = CommandDispatcher.ParseCommand(@"{""id"":""r1"",""type"":""turn.submit"",""message"":""hi""}");

        ParsedCommand parsed = Assert.IsType<ParsedCommand>(result);
        TurnSubmitCommand cmd = Assert.IsType<TurnSubmitCommand>(parsed.Command);
        Assert.Equal("r1", cmd.Id);
        Assert.Equal("hi", cmd.Message);
    }

    // ── malformedCommand ─────────────────────────────────────────────────────

    [Fact]
    public void ParseCommand_MalformedJson_ReturnsMalformedCommandFault()
    {
        CommandParse result = CommandDispatcher.ParseCommand("{not json");

        ParseFault fault = Assert.IsType<ParseFault>(result);
        Assert.Equal("malformedCommand", fault.Error.Code);
        Assert.True(fault.Error.Recoverable);
    }

    // ── missingType ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseCommand_MissingType_ReturnsMissingTypeFault()
    {
        CommandParse result = CommandDispatcher.ParseCommand(@"{""id"":""r1""}");

        ParseFault fault = Assert.IsType<ParseFault>(result);
        Assert.Equal("missingType", fault.Error.Code);
        Assert.True(fault.Error.Recoverable);
    }

    // ── unknownCommand ───────────────────────────────────────────────────────

    [Fact]
    public void ParseCommand_UnknownType_ReturnsUnknownCommandFault()
    {
        CommandParse result = CommandDispatcher.ParseCommand(@"{""id"":""r1"",""type"":""does.not.exist""}");

        ParseFault fault = Assert.IsType<ParseFault>(result);
        Assert.Equal("unknownCommand", fault.Error.Code);
        Assert.True(fault.Error.Recoverable);
    }

    // ── Totality: non-object JSON roots and empty/whitespace input ───────────
    // ParseCommand must never throw for any string. Non-object valid-JSON roots
    // and empty/whitespace input must all return ParseFault (never throw), recoverable.

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"foo\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void ParseCommand_NonObjectJsonRoot_ReturnsMalformedCommandFaultNeverThrows(string line)
    {
        CommandParse result = CommandDispatcher.ParseCommand(line);

        ParseFault fault = Assert.IsType<ParseFault>(result);
        Assert.Equal("malformedCommand", fault.Error.Code);
        Assert.True(fault.Error.Recoverable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseCommand_EmptyOrWhitespace_ReturnsFaultNeverThrows(string line)
    {
        CommandParse result = CommandDispatcher.ParseCommand(line);

        ParseFault fault = Assert.IsType<ParseFault>(result);
        Assert.True(fault.Error.Recoverable);
    }

    // ── Intentional nuance: known type + missing required payload ────────────
    // Design D3: a known type whose required fields are absent is treated as
    // unknownCommand (recoverable), not internalError — because STJ throws a
    // JsonException when required properties are missing, which lands in the
    // Deserialize<Command> catch arm.

    [Fact]
    public void ParseCommand_KnownTypeWithMissingRequiredField_ReturnsUnknownCommandFaultRecoverable()
    {
        // turn.submit requires "message"; omitting it causes STJ to throw JsonException.
        CommandParse result = CommandDispatcher.ParseCommand(@"{""id"":""r1"",""type"":""turn.submit""}");

        ParseFault fault = Assert.IsType<ParseFault>(result);
        Assert.Equal("unknownCommand", fault.Error.Code);
        Assert.True(fault.Error.Recoverable);
    }
}
