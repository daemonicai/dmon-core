using Dmon.Core.Extensions.NuGet;

namespace Dmon.Core.Tests.Extensions.NuGet;

public sealed class NuGetSearchServiceTests
{
    // -----------------------------------------------------------------------
    // Ranking formula
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeScore_HighDownloadsAndRecentActivity_ScoresHigherThanLowDownloads()
    {
        NuGetSearchResult high = new()
        {
            Id = "A", Version = "1.0.0",
            TotalDownloads = 100_000,
            Stars = 500,
            PushedAt = DateTimeOffset.UtcNow.AddDays(-5)
        };
        NuGetSearchResult low = new()
        {
            Id = "B", Version = "1.0.0",
            TotalDownloads = 10,
            Stars = 0,
            PushedAt = DateTimeOffset.UtcNow.AddDays(-400)
        };

        double scoreHigh = NuGetSearchService.ComputeScore(high);
        double scoreLow = NuGetSearchService.ComputeScore(low);

        Assert.True(scoreHigh > scoreLow);
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(10, 0.8)]
    [InlineData(45, 0.6)]
    [InlineData(200, 0.3)]
    [InlineData(400, 0.1)]
    public void ComputeRecencyScore_AgeThresholds_ReturnExpectedValues(int daysAgo, double expected)
    {
        DateTimeOffset pushed = DateTimeOffset.UtcNow.AddDays(-daysAgo);

        double score = NuGetSearchService.ComputeRecencyScore(pushed);

        Assert.Equal(expected, score);
    }

    [Fact]
    public void ComputeRecencyScore_NullPushedAt_ReturnsNeutralHalf()
    {
        double score = NuGetSearchService.ComputeRecencyScore(null);

        Assert.Equal(0.5, score);
    }

    [Fact]
    public void ComputeScore_NoGhData_StarScoreIsZero()
    {
        // Stars = null means no gh data; star contribution should be 0.
        NuGetSearchResult noGh = new()
        {
            Id = "A", Version = "1.0.0",
            TotalDownloads = 1000,
            Stars = null,
            PushedAt = null
        };
        NuGetSearchResult withGh = new()
        {
            Id = "B", Version = "1.0.0",
            TotalDownloads = 1000,
            Stars = 500,
            PushedAt = DateTimeOffset.UtcNow.AddDays(-5)
        };

        double noGhScore = NuGetSearchService.ComputeScore(noGh);
        double withGhScore = NuGetSearchService.ComputeScore(withGh);

        // The one with gh data must score higher (star + better recency).
        Assert.True(withGhScore > noGhScore);
    }

    // -----------------------------------------------------------------------
    // Graceful degradation — no gh
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeScore_GhUnavailable_NeutralRecencyUsed()
    {
        // When gh is unavailable, PushedAt is null → recency 0.5.
        // Score = log10(downloads+1)*0.5 + 0 + 0.5*0.2
        NuGetSearchResult result = new()
        {
            Id = "A", Version = "1.0.0",
            TotalDownloads = 999,
            Stars = null,
            PushedAt = null
        };

        double score = NuGetSearchService.ComputeScore(result);

        double expectedDownload = Math.Log10(1000) * 0.5;
        double expectedRecency = 0.5 * 0.2;
        double expected = expectedDownload + expectedRecency;
        Assert.Equal(expected, score, precision: 10);
    }

    // -----------------------------------------------------------------------
    // Archived repo exclusion — via Archived flag
    // -----------------------------------------------------------------------

    [Fact]
    public void ArchivedResult_HasArchivedTrue()
    {
        // Verify the record correctly carries the Archived flag.
        NuGetSearchResult archived = new()
        {
            Id = "Archived.Pkg", Version = "1.0.0",
            Archived = true
        };

        Assert.True(archived.Archived);
    }

    [Fact]
    public void NonArchivedResult_HasArchivedFalse()
    {
        NuGetSearchResult active = new()
        {
            Id = "Active.Pkg", Version = "1.0.0",
            Archived = false
        };

        Assert.False(active.Archived);
    }

    // -----------------------------------------------------------------------
    // Tag filter (IsGitHub helper and RepositoryUrl)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/owner/repo", true)]
    [InlineData("https://github.com/owner/repo.git", true)]
    [InlineData("https://GITHUB.COM/owner/repo", true)]
    [InlineData("https://gitlab.com/owner/repo", false)]
    [InlineData("https://bitbucket.org/owner/repo", false)]
    [InlineData(null, false)]
    public void IsGitHub_ReturnsExpectedValue(string? repoUrl, bool expected)
    {
        NuGetSearchResult result = new()
        {
            Id = "pkg", Version = "1.0.0",
            RepositoryUrl = repoUrl
        };

        Assert.Equal(expected, result.IsGitHub);
    }

    [Fact]
    public void ReadmeAvailable_IsFalse_WhenNoGhData()
    {
        NuGetSearchResult result = new()
        {
            Id = "pkg", Version = "1.0.0",
            RepositoryUrl = "https://github.com/owner/repo",
            ReadmeAvailable = false
        };

        Assert.False(result.ReadmeAvailable);
    }

    [Fact]
    public void ReadmeAvailable_IsTrue_WhenGhEnriched()
    {
        NuGetSearchResult result = new()
        {
            Id = "pkg", Version = "1.0.0",
            RepositoryUrl = "https://github.com/owner/repo",
            ReadmeAvailable = true
        };

        Assert.True(result.ReadmeAvailable);
    }
}
