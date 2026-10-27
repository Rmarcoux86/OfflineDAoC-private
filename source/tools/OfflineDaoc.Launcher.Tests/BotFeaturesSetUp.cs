using System.Reflection;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The player-bot pages are hidden in the launcher (MainForm.ShowBotFeatures, owner 2026-10-27) but still built; the
/// launcher tests show them so the existing page tests keep covering them. HiddenBotFeaturesTests checks the hidden view.
/// </summary>
[SetUpFixture]
public sealed class BotFeaturesSetUp
{
    internal static readonly FieldInfo Switch = Assembly.Load("OfflineDAoC").GetType("OfflineDaoc.Launcher.MainForm")!
        .GetField("ShowBotFeatures", BindingFlags.Static | BindingFlags.NonPublic)!;

    [OneTimeSetUp]
    public void ShowBotFeatures()
    {
        Switch.SetValue(null, true);
    }
}

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class HiddenBotFeaturesTests
{
    [Test]
    public void ANormalLaunchShowsOnlyBuildHistoryCharacterPlannerAndOptions()
    {
        Type mainFormType = Assembly.Load("OfflineDAoC").GetType("OfflineDaoc.Launcher.MainForm")!;
        BotFeaturesSetUp.Switch.SetValue(null, false);

        try
        {
            using var form = (Form)Activator.CreateInstance(mainFormType)!;
            TabControl main = Descendants(form).OfType<TabControl>()
                .First(tab => tab.TabPages.Cast<TabPage>().Any(page => page.Text == "Options"));
            IEnumerable<string> tabs = main.TabPages.Cast<TabPage>().Select(page => page.Text);

            Assert.That(tabs, Is.EqualTo(new[] { "Build History", "Character Planner", "Options" }));
        }
        finally
        {
            BotFeaturesSetUp.Switch.SetValue(null, true);
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;

            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
