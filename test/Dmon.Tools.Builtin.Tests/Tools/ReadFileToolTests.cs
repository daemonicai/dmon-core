using Dmon.Tools.Builtin.Tools;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tests.Tools;

[Collection("CwdMutating")]
public sealed class ReadFileToolTests
{
    private static IPermissionSettings MakeSettings(PermissionSettings? settings = null)
        => new StubPermissionSettings(settings ?? new PermissionSettings());

    private sealed class StubPermissionSettings(PermissionSettings settings) : IPermissionSettings
    {
        public PermissionSettings Settings => settings;
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static FunctionCallContent MakeCall(string path)
        => new("call-1", "read_file", new Dictionary<string, object?> { ["path"] = path });

    [Fact]
    public void Evaluate_PathUnderCwd_ReturnsAllow()
    {
        ReadFileTool tool = new();
        string cwdFile = Path.Combine(Environment.CurrentDirectory, "somefile.txt");
        FunctionCallContent call = MakeCall(cwdFile);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void Evaluate_PathOutsideCwd_ReturnsPrompt()
    {
        ReadFileTool tool = new();
        string outsidePath = Path.Combine(Path.GetTempPath(), "outsidefile.txt");
        FunctionCallContent call = MakeCall(outsidePath);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    [Fact]
    public void Evaluate_NullArguments_ReturnsPrompt()
    {
        ReadFileTool tool = new();
        FunctionCallContent call = new("call-1", "read_file", null);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    [Fact]
    public void Evaluate_CwdItself_ReturnsAllow()
    {
        ReadFileTool tool = new();
        string cwd = Environment.CurrentDirectory;
        FunctionCallContent call = MakeCall(cwd);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void Evaluate_SymlinkInCwdPointingOutside_ReturnsPrompt()
    {
        ReadFileTool tool = new();
        string cwdDir = NewTempDir();
        string outsideDir = NewTempDir();
        string original = Environment.CurrentDirectory;
        try
        {
            string outsideTarget = Path.Combine(outsideDir, "secret.txt");
            File.WriteAllText(outsideTarget, "secret");
            string link = Path.Combine(cwdDir, "escape.txt");
            File.CreateSymbolicLink(link, outsideTarget);

            Environment.CurrentDirectory = cwdDir;

            PermissionResult result = tool.Evaluate(MakeCall(link), MakeSettings(), null);

            Assert.Equal(PermissionResult.Prompt, result);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            SafeDelete(cwdDir);
            SafeDelete(outsideDir);
        }
    }

    [Fact]
    public void Evaluate_SymlinkedAncestorDirEscape_ReturnsPrompt()
    {
        // A symlink *directory* inside CWD whose real target is outside CWD, with a real
        // file beneath it. The leaf is a regular file, so the escape is only caught by
        // resolving the symlinked ancestor directory (ResolveExistingAncestor).
        ReadFileTool tool = new();
        string cwdDir = NewTempDir();
        string outsideDir = NewTempDir();
        string original = Environment.CurrentDirectory;
        try
        {
            string outsideTarget = Path.Combine(outsideDir, "secret.txt");
            File.WriteAllText(outsideTarget, "secret");
            string linkDir = Path.Combine(cwdDir, "linkdir");
            Directory.CreateSymbolicLink(linkDir, outsideDir);

            Environment.CurrentDirectory = cwdDir;

            string throughLink = Path.Combine(Environment.CurrentDirectory, "linkdir", "secret.txt");
            PermissionResult result = tool.Evaluate(MakeCall(throughLink), MakeSettings(), null);

            Assert.Equal(PermissionResult.Prompt, result);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            SafeDelete(cwdDir);
            SafeDelete(outsideDir);
        }
    }

    [Fact]
    public void Evaluate_RegularFileWithinCwd_ReturnsAllow()
    {
        ReadFileTool tool = new();
        string cwdDir = NewTempDir();
        string original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwdDir;

            // Build the target from the OS-canonical CWD so the assertion mirrors the
            // production path (the model's path is resolved against the real CWD), not the
            // pre-symlink-resolution temp string (e.g. macOS /var vs /private/var).
            string target = Path.Combine(Environment.CurrentDirectory, "inside.txt");
            File.WriteAllText(target, "hi");

            PermissionResult result = tool.Evaluate(MakeCall(target), MakeSettings(), null);

            Assert.Equal(PermissionResult.Allow, result);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            SafeDelete(cwdDir);
        }
    }

    [Fact]
    public void Evaluate_BrokenSymlinkInCwd_ReturnsPrompt()
    {
        ReadFileTool tool = new();
        string cwdDir = NewTempDir();
        string original = Environment.CurrentDirectory;
        try
        {
            string missingTarget = Path.Combine(cwdDir, "does-not-exist.txt");
            string link = Path.Combine(cwdDir, "dangling.txt");
            File.CreateSymbolicLink(link, missingTarget);

            Environment.CurrentDirectory = cwdDir;

            PermissionResult result = tool.Evaluate(MakeCall(link), MakeSettings(), null);

            Assert.Equal(PermissionResult.Prompt, result);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            SafeDelete(cwdDir);
        }
    }

    [Fact]
    public void Evaluate_PlainRelativePathInCwd_ReturnsAllow()
    {
        ReadFileTool tool = new();
        string cwdDir = NewTempDir();
        string original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwdDir;

            PermissionResult result = tool.Evaluate(MakeCall("notes.txt"), MakeSettings(), null);

            Assert.Equal(PermissionResult.Allow, result);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            SafeDelete(cwdDir);
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
