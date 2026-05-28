using System.Linq;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tier-A unit tests for <see cref="InputStateLayer"/>.
/// No terminal dependency — the class is pure state.
/// </summary>
public sealed class InputStateLayerTests
{
    // ── IsLocked default + roundtrip ──────────────────────────────────────

    [Fact]
    public void IsLocked_DefaultsToFalse()
    {
        InputStateLayer layer = new();

        Assert.False(layer.IsLocked);
        Assert.Equal(string.Empty, layer.CurrentBuffer);
        Assert.Empty(layer.History);
    }

    [Fact]
    public void IsLocked_Toggle_RoundTrips()
    {
        InputStateLayer layer = new();

        layer.IsLocked = true;
        Assert.True(layer.IsLocked);

        layer.IsLocked = false;
        Assert.False(layer.IsLocked);
    }

    // ── CurrentBuffer mirror ──────────────────────────────────────────────

    [Fact]
    public void OnInputChanged_UpdatesCurrentBuffer()
    {
        InputStateLayer layer = new();

        layer.OnInputChanged("hello");

        Assert.Equal("hello", layer.CurrentBuffer);
    }

    [Fact]
    public void OnInputChanged_ReplacesPreviousBuffer()
    {
        InputStateLayer layer = new();

        layer.OnInputChanged("a");
        layer.OnInputChanged("ab");

        Assert.Equal("ab", layer.CurrentBuffer);
    }

    [Fact]
    public void OnInputChanged_EmptyString_ClearsBuffer()
    {
        InputStateLayer layer = new();
        layer.OnInputChanged("hi");

        layer.OnInputChanged(string.Empty);

        Assert.Equal(string.Empty, layer.CurrentBuffer);
    }

    [Fact]
    public void OnInputChanged_WhileLocked_StillMirrors()
    {
        // Lock suppresses History appends and dispatch forwarding; the buffer mirrors
        // regardless so the user can see what they are typing during a turn.
        InputStateLayer layer = new();
        layer.IsLocked = true;

        layer.OnInputChanged("typing");

        Assert.Equal("typing", layer.CurrentBuffer);
    }

    // ── History append + bounded ──────────────────────────────────────────

    [Fact]
    public void OnInputSubmitted_Unlocked_AppendsToHistory()
    {
        InputStateLayer layer = new();

        layer.OnInputSubmitted("first");

        string entry = Assert.Single(layer.History);
        Assert.Equal("first", entry);
    }

    [Fact]
    public void OnInputSubmitted_Locked_DropsFromHistory()
    {
        InputStateLayer layer = new();
        layer.IsLocked = true;

        layer.OnInputSubmitted("dropped");

        Assert.Empty(layer.History);
    }

    [Fact]
    public void OnInputSubmitted_WhitespaceOnly_DropsFromHistory()
    {
        InputStateLayer layer = new();

        layer.OnInputSubmitted("   ");

        Assert.Empty(layer.History);
    }

    [Fact]
    public void OnInputSubmitted_HistoryBoundedAt100()
    {
        InputStateLayer layer = new();

        for (int i = 0; i < 105; i++)
            layer.OnInputSubmitted($"entry-{i}");

        Assert.Equal(100, layer.History.Count);
        // Oldest five entries evicted; oldest surviving entry is entry-5.
        Assert.Equal("entry-5", layer.History.First());
        Assert.Equal("entry-104", layer.History.Last());
    }

    [Fact]
    public void OnInputSubmitted_BoundedDoesNotEvictWhileLocked()
    {
        InputStateLayer layer = new();

        // Fill to capacity while unlocked.
        for (int i = 0; i < 100; i++)
            layer.OnInputSubmitted($"entry-{i}");

        string expectedFirst = layer.History.First();
        layer.IsLocked = true;

        // Locked submit must short-circuit before any eviction logic.
        layer.OnInputSubmitted("dropped");

        Assert.Equal(100, layer.History.Count);
        Assert.Equal(expectedFirst, layer.History.First());
    }

    // ── CurrentBuffer cleared on successful submit ────────────────────────

    [Fact]
    public void OnInputSubmitted_Unlocked_ClearsCurrentBuffer()
    {
        // InputChanged sets the buffer; a successful (unlocked, non-whitespace) submit clears it.
        InputStateLayer layer = new();
        layer.OnInputChanged("ready");

        layer.OnInputSubmitted("ready");

        Assert.Equal(string.Empty, layer.CurrentBuffer);
    }

    [Fact]
    public void OnInputSubmitted_Locked_DoesNotClearCurrentBuffer()
    {
        // Locked submit returns early before the buffer-clear line.
        InputStateLayer layer = new();
        layer.OnInputChanged("typed while locked");
        layer.IsLocked = true;

        layer.OnInputSubmitted("typed while locked");

        // Buffer should remain unchanged — the early-return fires before CurrentBuffer = string.Empty.
        Assert.Equal("typed while locked", layer.CurrentBuffer);
    }
}
