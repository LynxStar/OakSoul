using OakSoul.Overlay;
using OakSoul.World;
using Xunit;

namespace OakSoul.Review.Tests;

/// <summary>A portal as the overlay shows it: its activation area, and the name of where it leads.</summary>
public class PortalTests
{
    [Fact]
    public void ActivationAreaIsAroundThePointAndASpawnPointHasNone()
    {
        var door = new Portal("west00", 2, 758, 2023, 10004110, "east00");
        Assert.Equal((733, 1923, 50, 125), door.Activation);
        Assert.True(door.Activates(758 - 25, 2023 + 25));
        Assert.True(door.Activates(758, 2023 - 100));
        Assert.False(door.Activates(758 - 26, 2023));
        Assert.False(door.Activates(758, 2023 + 26));
        Assert.False(door.Activates(758, 2023 - 101));
        var spawn = new Portal("sp", 0, 1, 2, Portal.NoTarget, "");
        Assert.Null(spawn.Activation);
        Assert.False(spawn.Activates(1, 2));
    }

    [Fact]
    public void LabelNamesWhereThePortalLeads()
    {
        Assert.Equal("Deep Valley II", OverlayLabels.Portal(2, "west00", "", 10004110, "Deep Valley II", "east00", false));
        Assert.Equal("Ellinia (h_top00)", OverlayLabels.Portal(1, "h_under00", "", 10002000, "Ellinia", "h_top00", true));
        Assert.Equal("map 10006120", OverlayLabels.Portal(2, "in00", "", 10006120, null, "out00", false));
        Assert.Equal("spawn", OverlayLabels.Portal(0, "sp", "", null, null, "", false));
        Assert.Equal("pt_10002000_in03", OverlayLabels.Portal(8, "in03", "pt_10002000_in03", null, null, "", false));
        Assert.Equal("tp", OverlayLabels.Portal(6, "tp", "", null, null, "", false));
    }

    [Fact]
    public void CatalogNamesTheMapAndAddsTheStreetOnlyWhereItIsNeeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oaksoul-portal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.json"), """
                {"10004110": {"streetName": "Warning Street", "mapName": "Deep Valley II"},
                 "1": {"streetName": "Hidden Street", "mapName": "Mushroom Garden"},
                 "2": {"streetName": "Rainbow Street", "mapName": "Mushroom Garden"},
                 "3": {"streetName": "Bare Street", "mapName": ""},
                 "4": {"streetName": "Lone", "mapName": "Lone"}}
                """);
            var catalog = new MapCatalog(dir);
            Assert.Equal("Deep Valley II", catalog.DisplayName(10004110));
            Assert.Equal("Hidden Street / Mushroom Garden", catalog.DisplayName(1));
            Assert.Equal("Rainbow Street / Mushroom Garden", catalog.DisplayName(2));
            Assert.Equal("Bare Street", catalog.DisplayName(3));
            Assert.Equal("Lone", catalog.DisplayName(4));
            Assert.Null(catalog.DisplayName(99));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PortalItemsAreRegisteredFields()
    {
        var item = new OverlayItem(OverlayLayers.World, "portal").Set("point", new[] { 900, 576 }).Set("rect", new[] { 875, 476, 50, 125 })
            .Set("name", "west00").Set("type", 2).Set("target", 10004110).Set("targetName", "east00").Set("destination", "Deep Valley II")
            .Set("script", "x").Set("label", "Deep Valley II");
        Assert.Empty(OverlayLayers.Check(item));
    }
}
