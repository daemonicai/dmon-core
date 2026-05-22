using Daemon.Core.Permissions;

namespace Daemon.Core.Tests.Permissions;

public sealed class BashCompositeDetectorTests
{
    private readonly BashCompositeDetector _detector = new();

    // Simple commands — must NOT be composite
    [Theory]
    [InlineData("git commit -m \"msg\"")]
    [InlineData("dotnet build")]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("dotnet test")]
    [InlineData("npm install")]
    [InlineData("cargo build")]
    public void SimpleCommands_AreNotComposite(string command)
    {
        Assert.False(_detector.IsComposite(command));
    }

    // Pipes
    [Theory]
    [InlineData("ls | grep foo")]
    [InlineData("cat file |& tee log")]
    public void Pipes_AreComposite(string command)
    {
        Assert.True(_detector.IsComposite(command));
    }

    // Separators
    [Theory]
    [InlineData("git add . && git commit")]
    [InlineData("make clean || make build")]
    [InlineData("echo a; echo b")]
    [InlineData("sleep 10 &")]
    public void Separators_AreComposite(string command)
    {
        Assert.True(_detector.IsComposite(command));
    }

    // Command substitution
    [Fact]
    public void DollarParenSubstitution_IsComposite()
    {
        Assert.True(_detector.IsComposite("echo $(pwd)"));
    }

    [Fact]
    public void BacktickSubstitution_IsComposite()
    {
        Assert.True(_detector.IsComposite("echo `pwd`"));
    }

    // Process substitution
    [Fact]
    public void ProcessSubstitutionRead_IsComposite()
    {
        Assert.True(_detector.IsComposite("diff <(ls a) <(ls b)"));
    }

    [Fact]
    public void ProcessSubstitutionWrite_IsComposite()
    {
        Assert.True(_detector.IsComposite("tee >(cat)"));
    }

    // Redirects
    [Theory]
    [InlineData("echo hello > file.txt")]
    [InlineData("echo hello >> file.txt")]
    [InlineData("cat file < input")]
    [InlineData("cat << EOF")]
    [InlineData("cat <<< hello")]
    [InlineData("cmd 2> errors.log")]
    [InlineData("cmd &> all.log")]
    [InlineData("cmd >& all.log")]
    public void Redirects_AreComposite(string command)
    {
        Assert.True(_detector.IsComposite(command));
    }

    // Backgrounding
    [Fact]
    public void Backgrounding_IsComposite()
    {
        Assert.True(_detector.IsComposite("sleep 10 &"));
    }

    // Subshell
    [Fact]
    public void Subshell_IsComposite()
    {
        Assert.True(_detector.IsComposite("(cd /tmp && ls)"));
    }

    // Command groups
    [Fact]
    public void CommandGroup_IsComposite()
    {
        Assert.True(_detector.IsComposite("{ echo a; echo b; }"));
    }

    // Inline env assignment
    [Fact]
    public void InlineEnvAssignment_IsComposite()
    {
        Assert.True(_detector.IsComposite("FOO=bar git commit"));
    }

    [Fact]
    public void MultipleInlineEnvAssignments_AreComposite()
    {
        Assert.True(_detector.IsComposite("FOO=bar BAZ=qux git commit"));
    }

    // Single-quoted pipes — NOT composite (single quotes are opaque)
    [Fact]
    public void SingleQuotedPipe_IsNotComposite()
    {
        Assert.False(_detector.IsComposite("echo 'hello | world'"));
    }

    [Fact]
    public void SingleQuotedSemicolon_IsNotComposite()
    {
        Assert.False(_detector.IsComposite("echo 'a; b'"));
    }

    // Unclosed single quote — ambiguous, fail safe → composite
    [Fact]
    public void UnclosedSingleQuote_IsComposite()
    {
        Assert.True(_detector.IsComposite("echo 'hello"));
    }

    // Empty and null-like inputs
    [Fact]
    public void EmptyString_IsNotComposite()
    {
        Assert.False(_detector.IsComposite(string.Empty));
    }

    // Assignment-only (no command word) — not composite
    [Fact]
    public void StandaloneAssignment_IsNotComposite()
    {
        Assert.False(_detector.IsComposite("FOO=bar"));
    }
}
