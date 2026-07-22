using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._LuaM.Administration;
using Content.Client._LuaM.Sector;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMOperationalUiLayoutTest
{
    private static readonly Vector2 DirectorMinimumViewport = new(860f, 560f);
    private static readonly Vector2 SectorViewport = new(640f, 480f);

    [Test]
    public async Task DirectorAndSectorViewsRemainScrollableAndUseSharedOperationalSurfaces()
    {
        var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
        });

        try
        {
            var activator = pair.Client.ResolveDependency<IDynamicTypeFactory>();

            await pair.Client.WaitAssertion(() =>
            {
                using var director = activator.CreateInstance<LuaMAiDirectorWindow>(oneOff: true, inject: false);
                using var sector = activator.CreateInstance<LuaMSectorStatusUiFragment>(oneOff: true, inject: false);

                Arrange(director, DirectorMinimumViewport);
                Arrange(sector, SectorViewport);

                var directorTabs = director.FindControl<TabContainer>("DirectorTabs");
                var directorPages = directorTabs.Children.OfType<ScrollContainer>().ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(director.Resizable, Is.True);
                    Assert.That(director.MinSize, Is.EqualTo(DirectorMinimumViewport));
                    Assert.That(director.Size, Is.EqualTo(DirectorMinimumViewport));
                    Assert.That(directorPages, Has.Length.EqualTo(5));
                    Assert.That(directorPages.All(page => page.VScrollEnabled), Is.True);
                    Assert.That(directorPages.All(page => !page.HScrollEnabled), Is.True);
                    Assert.That(directorPages.All(page => page.ReserveScrollbarSpace), Is.True);

                    Assert.That(sector.Tabs.ChildCount, Is.EqualTo(4));
                    Assert.That(sector.Tabs.Children.OfType<ScrollContainer>().All(page => page.VScrollEnabled), Is.True);
                    Assert.That(sector.Tabs.Children.OfType<ScrollContainer>().All(page => !page.HScrollEnabled), Is.True);
                    Assert.That(sector.Tabs.Children.OfType<ScrollContainer>().All(page => page.ReserveScrollbarSpace), Is.True);
                    Assert.That(sector.Tabs.GetActualTabTitle(0), Is.Not.Null.And.Not.Empty);
                    Assert.That(sector.Tabs.GetActualTabTitle(3), Is.Not.Null.And.Not.Empty);
                    Assert.That(Descendants(sector).SelectMany(control => control.StyleClasses),
                        Does.Contain("UiSurfaceSection"));
                    Assert.That(Descendants(sector).SelectMany(control => control.StyleClasses),
                        Does.Not.Contain("PdaSectionPanel"));
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children)
        {
            yield return child;

            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void Arrange(Control control, Vector2 viewport)
    {
        control.SetSize = viewport;
        control.Measure(viewport);
        control.Arrange(UIBox2.FromDimensions(Vector2.Zero, viewport));
    }
}
