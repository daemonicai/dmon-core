using Dmon.Abstractions.Profiles;
using Dmon.Core.Profiles;

namespace Dmon.Core.Tests.Profiles;

/// <summary>
/// Verifies the built-in <c>coding</c> profile for Group 7.1.
/// </summary>
/// <remarks>
/// The byte-for-byte persona assertion is a design-risk guard: any future edit to
/// <see cref="BuiltInProfiles.CodingPersona"/> must break this test deliberately.
/// The canonical text is embedded here as an explicit expected literal — if the
/// constant drifts, the test fails and the author must update both.
/// </remarks>
public sealed class BuiltInProfilesTests
{
    // ── 7.1 — byte-for-byte persona guard ───────────────────────────────────

    [Fact]
    public void CodingPersona_MatchesEmbeddedCanonicalLiteral()
    {
        // Any change to the coding persona must also update this literal.
        // That is the point: the two must agree byte-for-byte.
        const string expected = """
            # Identity

            You are D-mon (pronounced "daemon" or "demon"), a coding agent. You run inside a terminal session and help the user write, edit, and reason about code. You have access to tools for reading files, writing files, running bash commands, and more.

            # Tool usage norms

            - Read a file before editing it.
            - Prefer targeted edits over full rewrites.
            - If the scope of a task is genuinely unclear, ask one short question — do not guess and do not ask multiple questions at once.

            # Permission model

            Bash commands and file writes require explicit user confirmation. The runtime handles this — do not try to work around it or warn the user about it on every turn.

            # Tone

            Informal and terse. Not rude. No padding. No apologies. No phrases like "Certainly!", "Of course!", "Great question!", or "I'd be happy to help". Do not describe what you are about to do — just do it.
            """;

        Assert.Equal(expected, BuiltInProfiles.CodingPersona);
    }

    // ── 7.1 — built-in coding profile properties ────────────────────────────

    [Fact]
    public void Coding_Profile_HasAssetsDisabled()
    {
        Assert.False(BuiltInProfiles.Coding.Assets);
    }

    [Fact]
    public void Coding_Profile_HasCodingPermissionMode()
    {
        Assert.Equal(PermissionMode.Coding, BuiltInProfiles.Coding.PermissionMode);
    }

    [Fact]
    public void Coding_Profile_NameIsCoding()
    {
        Assert.Equal("coding", BuiltInProfiles.Coding.Name);
    }

    // ── 7.1 — no asset directory created for coding profile ─────────────────
    // (unit matrix for provisioner is in SessionAssetProvisionerTests;
    //  this verifies the built-in profile drives Assets:false through the provisioner)

    [Fact]
    public void Provision_CodingProfile_CreatesNoAssetDirectory()
    {
        string workspace = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workspace);

        try
        {
            SessionAssetProvisioner provisioner = new(workspace);
            string? result = provisioner.Provision(BuiltInProfiles.Coding, "sess-coding");

            Assert.Null(result);
            Assert.False(Directory.Exists(Path.Combine(workspace, "assets")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
