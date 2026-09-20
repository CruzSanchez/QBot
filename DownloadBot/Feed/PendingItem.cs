namespace DownloadBot.Feed;

public sealed class PendingItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string? InfoHash { get; init; }
    public required ulong ChannelId { get; init; }
    public required ulong UserId { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}
