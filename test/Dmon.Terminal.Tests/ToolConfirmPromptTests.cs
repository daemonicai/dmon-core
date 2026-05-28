using Dcli;
using Dmon.Terminal.Tests.Fakes;

namespace Dmon.Terminal.Tests;

public sealed class ToolConfirmPromptTests
{
    private static string PlainText(Line line) =>
        string.Concat(line.Segments.Select(s => s.Text));

    private static ChoiceOpened OpenedChoice(FakeTerminal fake) =>
        fake.Calls.OfType<ChoiceOpened>().Single();

    private static IReadOnlyList<Line> PromptLines(FakeTerminal fake) =>
        OpenedChoice(fake).Request.Prompt ?? [];

    // ------------------------------------------------------------------ //
    //  Low-risk prompt structure
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowAsync_LowRisk_PromptContainsToolLine()
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        await ToolConfirmPrompt.ShowAsync(fake, "bash", "echo hi", "low", CancellationToken.None);

        IReadOnlyList<Line> prompt = PromptLines(fake);
        List<string> texts = prompt.Select(PlainText).ToList();

        Assert.Contains(texts, r => r.Contains("Tool: ") && r.Contains("bash"));
        Assert.Contains(texts, r => r.Contains("Args: ") && r.Contains("echo hi"));
        Assert.Contains(texts, r => r.Contains("Risk: "));
    }

    [Fact]
    public async Task ShowAsync_LowRisk_NoScrollbackCallsBeforeDialog()
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        await ToolConfirmPrompt.ShowAsync(fake, "bash", "echo hi", "low", CancellationToken.None);

        Assert.Empty(fake.Calls.OfType<ScrollbackAppendLine>());
    }

    // ------------------------------------------------------------------ //
    //  High-risk prompt structure
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowAsync_HighRisk_PromptContainsHighRiskMarker()
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 3));

        await ToolConfirmPrompt.ShowAsync(fake, "rm", "-rf /", "high", CancellationToken.None);

        IReadOnlyList<Line> prompt = PromptLines(fake);
        List<string> texts = prompt.Select(PlainText).ToList();

        Assert.Contains(texts, r => r.Contains("HIGH RISK"));
        Assert.DoesNotContain(texts, r => r.StartsWith("Risk: ") || r.Contains("Risk: "));
    }

    // ------------------------------------------------------------------ //
    //  Args truncation at 80 characters
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowAsync_ArgsLongerThan80Chars_TruncatesTo77PlusEllipsis()
    {
        string longArgs = new string('x', 90);
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        await ToolConfirmPrompt.ShowAsync(fake, "tool", longArgs, "low", CancellationToken.None);

        IReadOnlyList<Line> prompt = PromptLines(fake);
        string argsLine = prompt.Select(PlainText).First(r => r.Contains("Args: "));
        string argsValue = argsLine[(argsLine.IndexOf("Args: ", StringComparison.Ordinal) + "Args: ".Length)..];
        Assert.Equal(80, argsValue.Length); // 77 + "..."
        Assert.EndsWith("...", argsValue);
    }

    // ------------------------------------------------------------------ //
    //  Args exactly 80 chars — no truncation
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowAsync_ArgsExactly80Chars_NotTruncated()
    {
        string exactArgs = new string('y', 80);
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        await ToolConfirmPrompt.ShowAsync(fake, "tool", exactArgs, "low", CancellationToken.None);

        IReadOnlyList<Line> prompt = PromptLines(fake);
        string argsLine = prompt.Select(PlainText).First(r => r.Contains("Args: "));
        Assert.DoesNotContain("...", argsLine);
    }

    // ------------------------------------------------------------------ //
    //  High-risk line is styled bold+red
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowAsync_HighRisk_HighRiskLineHasBoldRedStyle()
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        await ToolConfirmPrompt.ShowAsync(fake, "tool", "args", "HIGH", CancellationToken.None);

        IReadOnlyList<Line> prompt = PromptLines(fake);
        Line? highRiskLine = prompt.FirstOrDefault(l => PlainText(l).Contains("HIGH RISK"));

        Assert.NotNull(highRiskLine);
        Segment seg = highRiskLine.Segments[0];
        Assert.Equal(Color.Named(Color.AnsiColor.Red), seg.Style.Foreground);
        Assert.True(seg.Style.Format.HasFlag(Format.Bold));
    }

    // ------------------------------------------------------------------ //
    //  Prompt structure — 4 lines in order, last is "Permission:"
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowAsync_Prompt_HasFourLinesEndingWithPermission()
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 0));

        await ToolConfirmPrompt.ShowAsync(fake, "tool", "args", "low", CancellationToken.None);

        IReadOnlyList<Line> prompt = PromptLines(fake);
        Assert.Equal(4, prompt.Count);
        Assert.Equal("Permission:", PlainText(prompt[3]));
    }

    // ------------------------------------------------------------------ //
    //  Index-to-permission mapping
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData(0, 0)] // 0 = ToolPermission.Once
    [InlineData(1, 1)] // 1 = ToolPermission.Project
    [InlineData(2, 2)] // 2 = ToolPermission.Global
    public async Task ShowAsync_ChoiceIndex_MapsToCorrectPermission(int choiceIndex, int expectedPermission)
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, choiceIndex));

        ToolPermission? result = await ToolConfirmPrompt.ShowAsync(
            fake, "tool", "args", "low", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal((ToolPermission)expectedPermission, result.Value);
    }

    [Fact]
    public async Task ShowAsync_DenyChoice_ReturnsNull()
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Submitted, 3));

        ToolPermission? result = await ToolConfirmPrompt.ShowAsync(
            fake, "tool", "args", "low", CancellationToken.None);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------ //
    //  Cancelled outcome
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowAsync_CancelledOutcome_ReturnsNull()
    {
        FakeTerminal fake = new();
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        ToolPermission? result = await ToolConfirmPrompt.ShowAsync(
            fake, "tool", "args", "low", CancellationToken.None);

        Assert.Null(result);
    }
}
