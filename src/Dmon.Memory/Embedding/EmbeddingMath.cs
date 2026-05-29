namespace Dmon.Memory.Embedding;

/// <summary>
/// Pure, model-independent vector math helpers.
/// All methods are stateless — safe to call from any thread, any test, without a loaded GGUF.
/// </summary>
public static class EmbeddingMath
{
    /// <summary>
    /// L2-normalizes <paramref name="vector"/> in place and returns the same array.
    /// If the vector's magnitude is zero the array is returned unchanged (avoids NaN).
    /// LlamaSharp does not normalize output vectors; this must be applied to every
    /// embedding before storage or comparison.
    /// </summary>
    public static float[] L2NormalizeInPlace(float[] vector)
    {
        float sumOfSquares = 0f;
        for (int i = 0; i < vector.Length; i++)
            sumOfSquares += vector[i] * vector[i];

        float magnitude = MathF.Sqrt(sumOfSquares);
        if (magnitude == 0f)
            return vector;

        float invMagnitude = 1f / magnitude;
        for (int i = 0; i < vector.Length; i++)
            vector[i] *= invMagnitude;

        return vector;
    }

    /// <summary>
    /// Returns a new L2-normalized copy of <paramref name="vector"/>.
    /// The input is not modified.
    /// </summary>
    public static float[] L2Normalize(ReadOnlySpan<float> vector)
    {
        float[] result = vector.ToArray();
        return L2NormalizeInPlace(result);
    }
}
