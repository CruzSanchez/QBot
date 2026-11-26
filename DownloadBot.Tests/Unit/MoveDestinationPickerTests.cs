using Discord;
using DownloadBot.Discord;
using DownloadBot.LocalLibrary;

namespace DownloadBot.Tests.Unit;

public class MoveDestinationPickerTests
{
    [Fact]
    public void BuildMoveDestinationComponents_OffersEveryDestinationPast25()
    {
        var destinations = new[] { "D", "E", "F", "G", "H", "Z" }
            .SelectMany(d => Enumerable.Range(1, 6).Select(n => new PlexCategory($@"{d}:\plex\C{n}", $"{d}:", $"C{n}")))
            .ToList();

        var rows = DashboardFormatter.BuildMoveDestinationComponents(destinations)
            .Components.OfType<ActionRowComponent>().ToList();

        var values = rows.SelectMany(r => r.Components).OfType<SelectMenuComponent>()
            .SelectMany(m => m.Options).Select(o => o.Value);

        Assert.Equal(3, rows.Count); // two menus + Cancel
        Assert.Equal(Enumerable.Range(0, 36).Select(i => i.ToString()), values);
    }
}
