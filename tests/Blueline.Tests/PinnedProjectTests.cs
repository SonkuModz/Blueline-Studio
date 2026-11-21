using Blueline.Core;

namespace Blueline.Tests;

/// <summary>Pinning works on the settings object only; nothing here saves, so nothing is written under %APPDATA%.</summary>
[TestClass]
public sealed class PinnedProjectTests
{
    [TestMethod]
    public void PinningTogglesAndIgnoresCase()
    {
        var settings = new AppSettings();
        Assert.IsTrue(settings.TogglePin(@"C:\Projects\Overhaul"));
        Assert.IsTrue(settings.IsPinned(@"c:\projects\overhaul"));
        Assert.IsFalse(settings.TogglePin(@"C:\PROJECTS\OVERHAUL"));
        Assert.AreEqual(0, settings.PinnedProjects.Count);
    }

    [TestMethod]
    public void PinnedProjectsAreNotListedAgainAsRecent()
    {
        var settings = new AppSettings { RecentProjects = [@"C:\P\A", @"C:\P\B", @"C:\P\C"] };
        settings.TogglePin(@"C:\P\B");
        CollectionAssert.AreEqual(new[] { @"C:\P\A", @"C:\P\C" }, settings.UnpinnedRecent.ToList());
    }

    [TestMethod]
    public void PinsKeepTheOrderTheyWereMadeIn()
    {
        var settings = new AppSettings();
        settings.TogglePin(@"C:\P\Second");
        settings.TogglePin(@"C:\P\First");
        CollectionAssert.AreEqual(new[] { @"C:\P\Second", @"C:\P\First" }, settings.PinnedProjects);
    }
}
