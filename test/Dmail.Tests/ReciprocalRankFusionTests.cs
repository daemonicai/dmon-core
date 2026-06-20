namespace Dmail.Tests;

public class ReciprocalRankFusionTests
{
    // Task 11.1: Unit tests for hybrid search RRF fusion logic

    [Fact]
    public void Fuse_FtsOnly_ReturnsFtsKeysInOrder()
    {
        var ftsKeys = new[] { "a", "b", "c" };
        var vecKeys = Array.Empty<string>();

        var result = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys);

        Assert.Equal(3, result.Count);
        Assert.Equal("a", result[0]);
        Assert.Equal("b", result[1]);
        Assert.Equal("c", result[2]);
    }

    [Fact]
    public void Fuse_VectorOnly_ReturnsVecKeysInOrder()
    {
        var ftsKeys = Array.Empty<string>();
        var vecKeys = new[] { "x", "y", "z" };

        var result = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys);

        Assert.Equal(3, result.Count);
        Assert.Equal("x", result[0]);
        Assert.Equal("y", result[1]);
        Assert.Equal("z", result[2]);
    }

    [Fact]
    public void Fuse_BothMatch_BoostsSharedKeys()
    {
        // "a" appears in both FTS (rank 1) and vector (rank 3)
        // FTS: a(1), b(2), c(3), d(4)
        // Vec: x(1), y(2), a(3), z(4)
        var ftsKeys = new[] { "a", "b", "c", "d" };
        var vecKeys = new[] { "x", "y", "a", "z" };

        var result = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys);

        // "a" gets 1/(60+1) + 1/(60+3) ≈ 0.0164 + 0.0159 = 0.0323
        // x gets    1/(60+1) = 0.0164
        // b gets    1/(60+2) = 0.0161
        // y gets    1/(60+2) = 0.0161
        // c gets    1/(60+3) = 0.0159
        // a should be first since it appears in both

        Assert.Equal("a", result[0]);
        Assert.Contains("a", result);
        Assert.Contains("x", result);
        Assert.Contains("b", result);
    }

    [Fact]
    public void Fuse_WithCustomK_UsesProvidedK()
    {
        var ftsKeys = new[] { "a" };
        var vecKeys = new[] { "a" };

        var result = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys, k: 10);

        Assert.Single(result);
        Assert.Equal("a", result[0]);
    }

    [Fact]
    public void Fuse_EmptyInputs_ReturnsEmpty()
    {
        var result = Services.ReciprocalRankFusion.Fuse([], []);

        Assert.Empty(result);
    }

    [Fact]
    public void Fuse_OverlappingKeys_RanksAboveNonOverlapping()
    {
        // "shared" appears first in both FTS and vector → highest combined score
        var ftsKeys = new[] { "shared", "fts_only" };
        var vecKeys = new[] { "shared", "vec_only" };

        var result = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys);

        Assert.Equal("shared", result[0]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Fuse_LargerK_ReducesRankPenalty()
    {
        // With large K (1000), ranks matter less → order is similar to input order
        // With small K (1), first rank dominates
        var ftsKeys = new[] { "top" };
        var vecKeys = new[] { "other", "top" }; // "top" is rank 2 in vector

        var resultLargeK = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys, k: 1000);
        var resultSmallK = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys, k: 1);

        // Both should have "top" first since it's in both lists
        Assert.Equal("top", resultLargeK[0]);
        Assert.Equal("top", resultSmallK[0]);
    }
}
