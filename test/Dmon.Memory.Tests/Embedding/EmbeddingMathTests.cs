using Dmon.Memory.Embedding;

namespace Dmon.Memory.Tests.Embedding;

public sealed class EmbeddingMathTests
{
    [Fact]
    public void L2NormalizeInPlace_NonZeroVector_OutputHasUnitNorm()
    {
        float[] vector = [3f, 4f];

        float[] result = EmbeddingMath.L2NormalizeInPlace(vector);

        float norm = MathF.Sqrt(result.Sum(x => x * x));
        Assert.Equal(1f, norm, precision: 5);
    }

    [Fact]
    public void L2NormalizeInPlace_ZeroVector_ReturnsUnchangedWithNoNaN()
    {
        float[] vector = [0f, 0f, 0f];

        float[] result = EmbeddingMath.L2NormalizeInPlace(vector);

        Assert.All(result, v => Assert.False(float.IsNaN(v), "zero vector must not produce NaN"));
        Assert.All(result, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void L2NormalizeInPlace_AlreadyNormalizedVector_IsIdempotent()
    {
        // Start with a known unit vector: [1/√2, 1/√2, 0].
        float inv = 1f / MathF.Sqrt(2f);
        float[] vector = [inv, inv, 0f];

        float[] result = EmbeddingMath.L2NormalizeInPlace(vector);

        float norm = MathF.Sqrt(result.Sum(x => x * x));
        Assert.Equal(1f, norm, precision: 5);
        Assert.Equal(inv, result[0], precision: 5);
        Assert.Equal(inv, result[1], precision: 5);
        Assert.Equal(0f,  result[2], precision: 5);
    }

    [Fact]
    public void L2NormalizeInPlace_Mutates_OriginalArray()
    {
        float[] vector = [6f, 0f, 0f];
        float[] returned = EmbeddingMath.L2NormalizeInPlace(vector);

        // Must return the same array reference and mutate it.
        Assert.Same(vector, returned);
        Assert.Equal(1f, vector[0], precision: 5);
    }

    [Fact]
    public void L2Normalize_DoesNotMutate_OriginalSpan()
    {
        float[] original = [3f, 4f];
        float[] copy = [.. original];

        float[] normalized = EmbeddingMath.L2Normalize(original);

        // Input unchanged.
        Assert.Equal(3f, original[0]);
        Assert.Equal(4f, original[1]);

        // Output is unit norm.
        float norm = MathF.Sqrt(normalized.Sum(x => x * x));
        Assert.Equal(1f, norm, precision: 5);
    }

    [Theory]
    [InlineData(1, 768)]
    [InlineData(768, 768)]
    public void L2NormalizeInPlace_ArbitraryDimensions_OutputHasUnitNorm(int nonZeroDims, int totalDims)
    {
        float[] vector = new float[totalDims];
        for (int i = 0; i < nonZeroDims; i++)
            vector[i] = (i % 2 == 0) ? 1f : -0.5f;

        EmbeddingMath.L2NormalizeInPlace(vector);

        float norm = MathF.Sqrt(vector.Sum(x => x * x));
        Assert.Equal(1f, norm, precision: 4);
    }
}
