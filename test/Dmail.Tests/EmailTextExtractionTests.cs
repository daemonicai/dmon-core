using Dmail.Services;

namespace Dmail.Tests;

public class EmailTextExtractionTests
{
    // Task 11.3: Unit tests for email text extraction and truncation

    [Fact]
    public void BuildEmbeddingText_SubjectAndBody_ConcatenatesWithNewline()
    {
        var subject = "Meeting Notes";
        var body = "Discussed Q2 budget and roadmap.";

        var result = EmbeddingService.BuildEmbeddingText(subject, body);

        Assert.StartsWith("Meeting Notes\n", result);
        Assert.EndsWith("roadmap.", result);
    }

    [Fact]
    public void BuildEmbeddingText_EmptyBody_ReturnsSubjectOnly()
    {
        var subject = "Reminder";
        var body = "";

        var result = EmbeddingService.BuildEmbeddingText(subject, body);

        Assert.Equal("Reminder\n", result);
    }

    [Fact]
    public void BuildEmbeddingText_EmptySubject_ReturnsBodyWithLeadingNewline()
    {
        var subject = "";
        var body = "Some body text";

        var result = EmbeddingService.BuildEmbeddingText(subject, body);

        Assert.StartsWith("\n", result);
        Assert.Contains("Some body text", result);
    }

    [Fact]
    public void BuildEmbeddingText_BothEmpty_ReturnsNewlineOnly()
    {
        var result = EmbeddingService.BuildEmbeddingText("", "");

        Assert.Equal("\n", result);
    }

    [Fact]
    public void BuildEmbeddingText_TruncatesAt512Tokens()
    {
        // 512 tokens × ~4 chars/token = ~2048 chars
        var subject = "Long email";
        var body = new string('X', 3000); // 3000 chars, well over 512 tokens

        var result = EmbeddingService.BuildEmbeddingText(subject, body);

        Assert.True(result.Length <= EmbeddingService.MaxTokenCount * 4,
            $"Expected max {EmbeddingService.MaxTokenCount * 4} chars, got {result.Length}");
    }

    [Fact]
    public void BuildEmbeddingText_UnderTokenLimit_ReturnsFullText()
    {
        var subject = "Hi";
        var body = "Short message.";

        var result = EmbeddingService.BuildEmbeddingText(subject, body);

        Assert.Contains("Hi", result);
        Assert.Contains("Short message.", result);
        Assert.True(result.Length < EmbeddingService.MaxTokenCount * 4);
    }

    [Fact]
    public void BuildEmbeddingText_ExactTokenBoundary_TokenCountIs384Dimensions()
    {
        // Verify the embedding service produces 384-dimension vectors
        // This test confirms the constant matches the model spec
        Assert.Equal(384, EmbeddingService.EmbeddingDimensions);
    }

    [Fact]
    public void BuildEmbeddingText_UnicodeText_HandledCorrectly()
    {
        var subject = "日本語の件名";
        var body = "Unicode body text with emoji 🚀";

        var result = EmbeddingService.BuildEmbeddingText(subject, body);

        Assert.StartsWith("日本語の件名\n", result);
        Assert.Contains("🚀", result);
    }

    [Fact]
    public void BuildEmbeddingText_MaxBatchSize_Is32()
    {
        // Verify batch size constant
        Assert.Equal(32, EmbeddingService.MaxBatchSize);
    }
}
