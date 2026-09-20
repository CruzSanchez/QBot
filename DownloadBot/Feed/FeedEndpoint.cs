using System.ServiceModel.Syndication;
using System.Xml;

namespace DownloadBot.Feed;

public static class FeedEndpoint
{
    public static void MapFeedEndpoint(this WebApplication app)
    {
        app.MapGet("/feed", (PendingItemQueue queue, ILogger<PendingItemQueue> logger) =>
        {
            var items = queue.GetAll();
            logger.LogInformation("/feed requested — returning {Count} item(s): {Titles}",
                items.Count, string.Join(", ", items.Select(i => i.Title)));

            var feed = new SyndicationFeed(
                "DownloadBot Feed",
                "Pending items picked in Discord, waiting for qBittorrent to pick up.",
                new Uri("http://localhost/feed"))
            {
                LastUpdatedTime = DateTimeOffset.UtcNow
            };

            feed.Items = items.Select(item => new SyndicationItem(
                item.Title,
                item.Title,
                new Uri(item.Link),
                item.Id,
                item.AddedAt)
            {
                PublishDate = item.AddedAt
            });

            using var stream = new MemoryStream();
            using (var xmlWriter = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = System.Text.Encoding.UTF8, Indent = true }))
            {
                new Rss20FeedFormatter(feed).WriteTo(xmlWriter);
            }

            return Results.Text(System.Text.Encoding.UTF8.GetString(stream.ToArray()), "application/rss+xml");
        });
    }
}
