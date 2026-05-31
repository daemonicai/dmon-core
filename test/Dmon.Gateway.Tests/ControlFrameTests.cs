using System.Text.Json;
using Dmon.Gateway.Protocol;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Tests for the Group-3 control-frame DTOs and the "gw" discriminator routing.
///
/// ADR-003 frames use "type" as their discriminator.
/// Control frames use "gw" — a field ADR-003 never emits — so the two namespaces
/// are provably disjoint.
/// </summary>
public sealed class ControlFrameTests
{
    // -------------------------------------------------------------------------
    // 3.1 — Wire shapes and discriminator values
    // -------------------------------------------------------------------------

    [Fact]
    public void AttachFrame_Serializes_ToExpectedShape()
    {
        AttachFrame frame = new() { SessionId = "sess-1", LastSeq = 42 };
        string json = ControlFrameSerializer.Serialize(frame);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("attach", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal("sess-1", doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("lastSeq").GetInt64());
    }

    [Fact]
    public void AttachedFrame_Serializes_ToExpectedShape()
    {
        AttachedFrame frame = new() { Generation = 3, HeadSeq = 7 };
        string json = ControlFrameSerializer.Serialize(frame);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("attached", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(7, doc.RootElement.GetProperty("headSeq").GetInt64());
    }

    [Fact]
    public void AckFrame_Serializes_ToExpectedShape()
    {
        AckFrame frame = new() { Id = "req-99" };
        string json = ControlFrameSerializer.Serialize(frame);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("ack", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal("req-99", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void PingFrame_Serializes_ToExpectedShape()
    {
        string json = ControlFrameSerializer.Serialize(new PingFrame());
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("ping", doc.RootElement.GetProperty("gw").GetString());
    }

    [Fact]
    public void PongFrame_Serializes_ToExpectedShape()
    {
        string json = ControlFrameSerializer.Serialize(new PongFrame());
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("pong", doc.RootElement.GetProperty("gw").GetString());
    }

    // -------------------------------------------------------------------------
    // 3.1 — Discriminator routing: "gw" present → control frame
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"gw":"attach","sessionId":"s","lastSeq":0}""", "attach")]
    [InlineData("""{"gw":"attached","generation":1,"headSeq":0}""", "attached")]
    [InlineData("""{"gw":"ack","id":"x"}""", "ack")]
    [InlineData("""{"gw":"ping"}""", "ping")]
    [InlineData("""{"gw":"pong"}""", "pong")]
    public void GetGwDiscriminator_ReturnsGwValue_ForControlFrames(string raw, string expected)
    {
        string? result = ControlFrameSerializer.GetGwDiscriminator(raw);
        Assert.Equal(expected, result);
    }

    // -------------------------------------------------------------------------
    // 3.1 — ADR-003 frames have no "gw" → discriminator returns null
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"id":"req-1","type":"run","prompt":"hello"}""")]
    [InlineData("""{"type":"agentReady","protocolVersion":"1.0","coreVersion":"0.1.0"}""")]
    [InlineData("""{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"hi","partial":false}}""")]
    [InlineData("""{"type":"response","command":"run","success":true,"data":{}}""")]
    public void GetGwDiscriminator_ReturnsNull_ForAdr003Frames(string raw)
    {
        string? result = ControlFrameSerializer.GetGwDiscriminator(raw);
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // 3.1 — "gw" and "type" cannot collide: ADR-003 never uses the "gw" field
    // -------------------------------------------------------------------------

    [Fact]
    public void ControlFrames_DoNotContainTypeField_SoTheyCannotCollideWithAdr003()
    {
        // Serialized control frames must not carry a "type" field, which is the
        // ADR-003 discriminator. If they did, a router using only "type" might
        // misidentify them.
        string[] frames =
        [
            ControlFrameSerializer.Serialize(new AttachFrame { SessionId = "s", LastSeq = 0 }),
            ControlFrameSerializer.Serialize(new AttachedFrame { Generation = 1, HeadSeq = 0 }),
            ControlFrameSerializer.Serialize(new AckFrame { Id = "x" }),
            ControlFrameSerializer.Serialize(new PingFrame()),
            ControlFrameSerializer.Serialize(new PongFrame()),
        ];

        foreach (string json in frames)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(
                doc.RootElement.TryGetProperty("type", out _),
                $"Control frame must not have a 'type' field: {json}");
        }
    }

    // -------------------------------------------------------------------------
    // Type-tolerant parsing — a non-string discriminator/id must not throw (untrusted boundary)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"gw":42}""")]
    [InlineData("""{"gw":true}""")]
    [InlineData("""{"gw":null}""")]
    [InlineData("""{"gw":{"nested":1}}""")]
    public void GetGwDiscriminator_ReturnsNull_AndDoesNotThrow_OnNonStringGw(string raw)
    {
        // A structurally-valid frame with a non-string "gw" must be treated as having no usable
        // discriminator (null), never throw — otherwise one malformed client frame crashes the
        // forwarding loop on the untrusted network boundary.
        string? result = ControlFrameSerializer.GetGwDiscriminator(raw);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("""{"id":42,"type":"run"}""")]
    [InlineData("""{"id":true,"type":"run"}""")]
    [InlineData("""{"id":null,"type":"run"}""")]
    [InlineData("""{"id":{"nested":1},"type":"run"}""")]
    public void GetCommandId_ReturnsNull_AndDoesNotThrow_OnNonStringId(string raw)
    {
        // A non-string "id" (e.g. numeric) must yield "no usable id" (null), not throw.
        string? result = ControlFrameSerializer.GetCommandId(raw);
        Assert.Null(result);
    }

    [Fact]
    public void GetCommandId_ReturnsStringId_WhenPresent()
    {
        Assert.Equal("req-7", ControlFrameSerializer.GetCommandId("""{"id":"req-7","type":"run"}"""));
    }

    // -------------------------------------------------------------------------
    // 3.2 — ParseAttach round-trips correctly
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseAttach_RoundTrips_SessionIdAndLastSeq()
    {
        string raw = """{"gw":"attach","sessionId":"my-session","lastSeq":17}""";
        AttachFrame? parsed = ControlFrameSerializer.ParseAttach(raw);

        Assert.NotNull(parsed);
        Assert.Equal("my-session", parsed.SessionId);
        Assert.Equal(17, parsed.LastSeq);
    }

    [Fact]
    public void ParseAttach_ReturnsNull_OnMalformedJson()
    {
        AttachFrame? result = ControlFrameSerializer.ParseAttach("not-json{{{");
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // Serialization uses camelCase (repo convention)
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialization_UsesCamelCase()
    {
        AttachedFrame frame = new() { Generation = 1, HeadSeq = 5 };
        string json = ControlFrameSerializer.Serialize(frame);

        // Verify the property names are camelCase, not PascalCase.
        Assert.Contains("\"generation\"", json);
        Assert.Contains("\"headSeq\"", json);
        Assert.DoesNotContain("\"Generation\"", json);
        Assert.DoesNotContain("\"HeadSeq\"", json);
    }
}
