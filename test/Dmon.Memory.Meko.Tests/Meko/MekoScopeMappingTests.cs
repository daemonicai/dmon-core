using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 6.1 / 6.3 — <see cref="MekoScopeMapping"/> contract (revised D9, live-verified 2026-05-29).
/// <c>scope</c> is the fixed constant <c>"admin"</c>; <c>run_id</c> policy maps
/// <see cref="MemoryScope.Session"/> → normalized hex session id, durable scopes → null (omit).
/// Meko applies <c>int(run_id, 16)</c>, so the emitted value must be pure hex.
/// </summary>
public sealed class MekoScopeMappingTests
{
    [Fact]
    public void AdminScope_IsAdmin()
    {
        Assert.Equal("admin", MekoScopeMapping.AdminScope);
    }

    [Theory]
    [InlineData(MemoryScope.Agent)]
    [InlineData(MemoryScope.User)]
    [InlineData(MemoryScope.Shared)]
    public void ToRunId_DurableScope_ReturnsNull(MemoryScope scope)
    {
        string? runId = MekoScopeMapping.ToRunId(scope, "sess-abc");
        Assert.Null(runId);
    }

    [Fact]
    public void ToRunId_UnknownScope_ReturnsNull()
    {
        // Any unrecognised value that is not Session → durable treatment (omit run_id).
        MemoryScope unknown = (MemoryScope)999;
        string? runId = MekoScopeMapping.ToRunId(unknown, "sess-abc");
        Assert.Null(runId);
    }

    /// <summary>
    /// Session scope with a standard hyphenated GUID session id — the production-shaped
    /// case. The emitted run_id must (a) contain no hyphens and (b) be parseable as the
    /// original GUID in "N" format (round-trips via <see cref="Guid.TryParseExact"/>).
    /// </summary>
    [Fact]
    public void ToRunId_SessionScope_HyphenatedGuid_EmitsHyphenFreeHex()
    {
        Guid original = Guid.NewGuid();
        string hyphenated = original.ToString(); // "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"

        string? runId = MekoScopeMapping.ToRunId(MemoryScope.Session, hyphenated);

        Assert.NotNull(runId);
        // Must contain no hyphens.
        Assert.DoesNotContain('-', runId);
        // Must be parseable as the original GUID in "N" format — proves it is valid hex.
        Assert.True(
            Guid.TryParseExact(runId, "N", out Guid roundTripped),
            $"run_id '{runId}' is not a valid 'N'-format GUID hex string.");
        Assert.Equal(original, roundTripped);
    }

    /// <summary>
    /// Session scope with an already-normalised "N"-format GUID — passes through unchanged.
    /// </summary>
    [Fact]
    public void ToRunId_SessionScope_NFormatGuid_PassesThrough()
    {
        Guid original = Guid.NewGuid();
        string nFormat = original.ToString("N");

        string? runId = MekoScopeMapping.ToRunId(MemoryScope.Session, nFormat);

        Assert.Equal(nFormat, runId);
        Assert.DoesNotContain('-', runId!);
        Assert.True(Guid.TryParseExact(runId, "N", out _));
    }

    /// <summary>
    /// Durable scopes never emit a run_id regardless of the session id value.
    /// </summary>
    [Theory]
    [InlineData(MemoryScope.Agent)]
    [InlineData(MemoryScope.User)]
    [InlineData(MemoryScope.Shared)]
    public void ToRunId_DurableScopes_NeverEmitRunId_EvenWithGuidSessionId(MemoryScope scope)
    {
        string hyphenatedGuid = Guid.NewGuid().ToString();
        string? runId = MekoScopeMapping.ToRunId(scope, hyphenatedGuid);
        Assert.Null(runId);
    }
}
