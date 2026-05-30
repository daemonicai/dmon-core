using Dmon.Core.Rpc;

namespace Dmon.Core.Tests.Rpc;

public sealed class WizardAnswerHelperTests
{
    // ─── DecodeChooseManyIndices ──────────────────────────────────────

    [Fact]
    public void Decode_SingleIndex_ReturnsCorrectly()
    {
        IReadOnlyList<int> result = WizardAnswerHelper.DecodeChooseManyIndices("2", optionCount: 5);
        Assert.Equal([2], result);
    }

    [Fact]
    public void Decode_MultipleIndices_ReturnsCorrectly()
    {
        IReadOnlyList<int> result = WizardAnswerHelper.DecodeChooseManyIndices("0,2,4", optionCount: 5);
        Assert.Equal([0, 2, 4], result);
    }

    [Fact]
    public void Decode_WhitespacePadding_IsStripped()
    {
        IReadOnlyList<int> result = WizardAnswerHelper.DecodeChooseManyIndices(" 1 , 3 ", optionCount: 5);
        Assert.Equal([1, 3], result);
    }

    [Fact]
    public void Decode_NullValue_Throws()
    {
        Assert.Throws<FormatException>(() =>
            WizardAnswerHelper.DecodeChooseManyIndices(null, optionCount: 3));
    }

    [Fact]
    public void Decode_EmptyString_Throws()
    {
        Assert.Throws<FormatException>(() =>
            WizardAnswerHelper.DecodeChooseManyIndices("", optionCount: 3));
    }

    [Fact]
    public void Decode_NonIntegerToken_Throws()
    {
        Assert.Throws<FormatException>(() =>
            WizardAnswerHelper.DecodeChooseManyIndices("0,abc,2", optionCount: 5));
    }

    [Fact]
    public void Decode_IndexOutOfRange_Throws()
    {
        Assert.Throws<FormatException>(() =>
            WizardAnswerHelper.DecodeChooseManyIndices("0,5", optionCount: 5)); // max valid = 4
    }

    [Fact]
    public void Decode_NegativeIndex_Throws()
    {
        Assert.Throws<FormatException>(() =>
            WizardAnswerHelper.DecodeChooseManyIndices("-1", optionCount: 3));
    }

    // ─── EncodeChooseManyIndices ──────────────────────────────────────

    [Fact]
    public void Encode_SingleIndex_ProducesCorrectString()
    {
        string result = WizardAnswerHelper.EncodeChooseManyIndices([1]);
        Assert.Equal("1", result);
    }

    [Fact]
    public void Encode_MultipleIndices_ProducesCommaSeparated()
    {
        string result = WizardAnswerHelper.EncodeChooseManyIndices([0, 2, 4]);
        Assert.Equal("0,2,4", result);
    }

    [Fact]
    public void RoundTrip_EncodeDecodePreservesIndices()
    {
        IReadOnlyList<int> original = [0, 3, 7];
        string encoded = WizardAnswerHelper.EncodeChooseManyIndices(original);
        IReadOnlyList<int> decoded = WizardAnswerHelper.DecodeChooseManyIndices(encoded, optionCount: 10);
        Assert.Equal(original, decoded);
    }
}
