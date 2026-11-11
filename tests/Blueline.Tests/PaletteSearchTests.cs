using Blueline.Core;

namespace Blueline.Tests;

[TestClass]
public sealed class PaletteSearchTests
{
    [TestMethod]
    public void EveryWordMustAppearInAnyOrderIgnoringCase()
    {
        Assert.IsTrue(PaletteSearch.Score("tex", "Go to Textures") > 0);
        Assert.IsTrue(PaletteSearch.Score("TEXTURES GO", "Go to Textures") > 0);
        Assert.IsTrue(PaletteSearch.Score("burn 01", "nhl/ui/kettle_assets/effects/burn_01.dds") > 0);
        Assert.AreEqual(0, PaletteSearch.Score("burn 99", "nhl/ui/kettle_assets/effects/burn_01.dds"));
        Assert.AreEqual(0, PaletteSearch.Score("zzz", "Go to Textures"));
    }

    [TestMethod]
    public void AnEmptyQueryMatchesEverything()
    {
        Assert.IsTrue(PaletteSearch.Score("", "anything") > 0);
        Assert.IsTrue(PaletteSearch.Score("   ", "anything") > 0);
    }

    [TestMethod]
    public void StartsOfTextAndWordsRankAboveTheMiddleOfAWord()
    {
        var start = PaletteSearch.Score("over", "Overlays");
        var word = PaletteSearch.Score("over", "Go to Overlays");
        var middle = PaletteSearch.Score("over", "Recovery tools");
        Assert.IsTrue(start > word, "start of the text beats start of a later word");
        Assert.IsTrue(word > middle, "start of a word beats the middle of one");
    }

    [TestMethod]
    public void ShorterTextRanksAboveALongPathWithTheSameMatch()
    {
        var short1 = PaletteSearch.Score("logo", "logo.gnf");
        var long1 = PaletteSearch.Score("logo", "nhl/ui/kettle_assets/teamlogos/some/very/deep/folder/logo.gnf");
        Assert.IsTrue(short1 > long1);
    }

    [TestMethod]
    public void WordsAreSplitOnSpacesAndTabs()
    {
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, PaletteSearch.Words("  a  b\tc "));
    }
}
