namespace Dmon.Core.Rpc;

/// <summary>
/// Shared encode/decode for wizard answer values carried on <c>wizard.answer</c>.
///
/// For ChooseMany steps the wire format is a comma-separated list of zero-based
/// option indices, e.g. "0,2". All other step types carry a raw string value.
/// </summary>
internal static class WizardAnswerHelper
{
    /// <summary>
    /// Decodes a comma-separated index string into a list of zero-based integer indices.
    /// </summary>
    /// <param name="value">The raw value from <c>WizardAnswerCommand.Value</c>.</param>
    /// <param name="optionCount">The number of options in the step (used for range validation).</param>
    /// <returns>The validated indices.</returns>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="value"/> is null, empty, contains non-integer tokens,
    /// or any index is out of range for <paramref name="optionCount"/> options.
    /// </exception>
    public static IReadOnlyList<int> DecodeChooseManyIndices(string? value, int optionCount)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("ChooseMany answer value must not be null or empty.");

        string[] tokens = value.Split(',');
        int[] indices = new int[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i].Trim();
            if (!int.TryParse(token, out int index))
                throw new FormatException($"ChooseMany index token '{token}' is not a valid integer.");

            if (index < 0 || index >= optionCount)
                throw new FormatException(
                    $"ChooseMany index {index} is out of range for {optionCount} option(s).");

            indices[i] = index;
        }

        return indices;
    }

    /// <summary>
    /// Encodes a list of zero-based option indices into the wire format expected by
    /// <see cref="DecodeChooseManyIndices"/>.
    /// </summary>
    public static string EncodeChooseManyIndices(IReadOnlyList<int> indices)
        => string.Join(',', indices);
}
