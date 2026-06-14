namespace Daemonic.Dmail.Services;

/// <summary>
/// Extracted RRF fusion logic for unit testability.
/// Task 11.1: Unit tests for hybrid search RRF fusion logic.
/// </summary>
public static class ReciprocalRankFusion
{
    private const int DefaultK = 60;

    /// <summary>
    /// Fuse two ranked result sets using Reciprocal Rank Fusion.
    /// Returns a deduplicated list of keys ordered by combined RRF score.
    /// </summary>
    /// <param name="ftsKeys">Ranked FTS result keys (key → rank index, 0-based).</param>
    /// <param name="vecKeys">Ranked vector result keys (key → rank index, 0-based).</param>
    /// <param name="k">RRF smoothing constant (default 60).</param>
    /// <returns>Keys sorted by descending combined RRF score.</returns>
    public static List<string> Fuse(
        IReadOnlyList<string> ftsKeys,
        IReadOnlyList<string> vecKeys,
        int k = DefaultK)
    {
        var scores = new Dictionary<string, double>();

        // Score FTS results by rank
        for (int i = 0; i < ftsKeys.Count; i++)
        {
            var key = ftsKeys[i];
            scores[key] = 1.0 / (k + i + 1);
        }

        // Score vector results by rank, adding if already present
        for (int i = 0; i < vecKeys.Count; i++)
        {
            var key = vecKeys[i];
            var rrfScore = 1.0 / (k + i + 1);

            if (scores.TryGetValue(key, out var existing))
            {
                scores[key] = existing + rrfScore;
            }
            else
            {
                scores[key] = rrfScore;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
    }
}
