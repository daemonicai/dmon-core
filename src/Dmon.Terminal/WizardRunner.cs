namespace Dmon.Terminal;

/// <summary>
/// Iterates a list of wizard steps, passing state forward. Supports Back navigation
/// via a stack of prior states: when a step returns <see cref="WizardState.Back"/>,
/// the runner pops the stack and re-runs from that position.
/// </summary>
internal static class WizardRunner
{
    public static async Task<WizardState?> RunAsync(
        IReadOnlyList<Func<WizardState, Task<WizardState?>>> steps,
        CancellationToken cancellationToken)
    {
        WizardState current = new(null, null, null, null);
        Stack<WizardState> history = new();
        int index = 0;

        while (index < steps.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WizardState? result = await steps[index](current).ConfigureAwait(false);

            if (result is null)
                return null; // cancelled

            if (ReferenceEquals(result, WizardState.Back))
            {
                if (history.Count == 0)
                    return null; // Back on first step = cancel

                current = history.Pop();
                index--;
                continue;
            }

            history.Push(current);
            current = result;
            index++;
        }

        return current;
    }
}
