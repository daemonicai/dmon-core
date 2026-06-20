namespace Dmail.Models;

public sealed record Email
{
    public uint Uid { get; init; }
    public string Account { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string FromAddr { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string? Labels { get; init; }
    public bool PendingEmbedding { get; set; }
}
