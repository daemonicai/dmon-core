using System.Linq;
using Dmon.Tools.Dmail;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Dmail.Tests;

public sealed class DmailExtensionTests
{
    [Fact]
    public void ExposesTheThreeAgentTools()
    {
        var extension = new DmailExtension("http://localhost:9", apiKey: null);

        string[] names = extension.Tools.Select(t => t.Name).ToArray();

        Assert.Equal(new[] { "search_email", "check_new_messages", "get_email" }, names);
    }

    [Fact]
    public void EveryToolHasADescriptionForTheModel()
    {
        var extension = new DmailExtension("http://localhost:9", apiKey: null);

        Assert.All(extension.Tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public void GetEmailPromptsWhileMetadataToolsAreAllowed()
    {
        var extension = new DmailExtension("http://localhost:9", apiKey: null);

        // Evaluate keys solely off the tool name; the settings args are unused.
        Assert.Equal(
            Dmon.Protocol.Enums.PermissionResult.Allow,
            extension.Evaluate(Call("search_email"), null!, null));
        Assert.Equal(
            Dmon.Protocol.Enums.PermissionResult.Allow,
            extension.Evaluate(Call("check_new_messages"), null!, null));
        Assert.Equal(
            Dmon.Protocol.Enums.PermissionResult.Prompt,
            extension.Evaluate(Call("get_email"), null!, null));
    }

    [Fact]
    public async Task SearchReturnsAFriendlyMessageWhenDmailIsUnreachable()
    {
        // Port 9 (discard) refuses connections — the tool must degrade to a message, not throw.
        var extension = new DmailExtension("http://localhost:9", apiKey: null);
        AIFunction search = extension.Tools.Single(t => t.Name == "search_email");

        object? result = await search.InvokeAsync(
            new AIFunctionArguments { ["query"] = "invoice" });

        Assert.Contains("Could not search email", result?.ToString());
    }

    private static FunctionCallContent Call(string name) =>
        new(callId: "test", name: name, arguments: null);
}
