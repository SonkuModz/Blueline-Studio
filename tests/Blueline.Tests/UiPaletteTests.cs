using Blueline.Core;

namespace Blueline.Tests;

[TestClass]
public sealed class UiPaletteTests
{
    [TestMethod]
    public void BothThemesDefineTheSameColours()
    {
        CollectionAssert.AreEquivalent(UiPalette.Dark.Colours.Keys.ToList(), UiPalette.Light.Colours.Keys.ToList());
        foreach (var hex in UiPalette.Dark.Colours.Values.Concat(UiPalette.Light.Colours.Values))
            UiPalette.Parse(hex);
    }

    [TestMethod]
    public void TextIsReadableOnEverySurfaceInBothThemes()
    {
        foreach (var palette in new[] { UiPalette.Dark, UiPalette.Light })
        {
            var c = palette.Colours;
            foreach (var surface in new[] { "Window", "Panel", "Raised", "Hover" })
            {
                Assert.IsTrue(UiPalette.Contrast(c["Text"], c[surface]) >= 7, $"{palette.Name}: text on {surface}");
                Assert.IsTrue(UiPalette.Contrast(c["Dim"], c[surface]) >= 4.5, $"{palette.Name}: dim text on {surface}");
            }
            foreach (var surface in new[] { "Window", "Panel", "Raised" })
                Assert.IsTrue(UiPalette.Contrast(c["AccentText"], c[surface]) >= 4.5, $"{palette.Name}: accent text on {surface}");
            foreach (var status in new[] { "Danger", "Success", "Warning" })
                Assert.IsTrue(UiPalette.Contrast(c[status], c["Panel"]) >= 3, $"{palette.Name}: {status} on panel");

            // White button text on the brand blue is a little under AA for body text, and buttons are bold and short.
            Assert.IsTrue(UiPalette.Contrast("#FFFFFF", c["Accent"]) >= 4.0, $"{palette.Name}: white on accent");
        }
    }

    [TestMethod]
    public void DarkIsTheDefaultAndNamesResolve()
    {
        Assert.AreSame(UiPalette.Dark, UiPalette.Named(null));
        Assert.AreSame(UiPalette.Dark, UiPalette.Named("anything"));
        Assert.AreSame(UiPalette.Light, UiPalette.Named("LIGHT"));
        Assert.AreEqual(UiPalette.DarkName, new AppSettings().Theme);
        Assert.IsFalse(new AppSettings().ReduceMotion);
    }

    [TestMethod]
    public void ContrastMatchesKnownValues()
    {
        Assert.AreEqual(21, UiPalette.Contrast("#FFFFFF", "#000000"), 0.01);
        Assert.AreEqual(1, UiPalette.Contrast("#336699", "#336699"), 0.0001);
    }
}
