using System.IO;
using System.Numerics;
using Content.Client.Shuttles.UI;
using Content.Client._Mono.FireControl.UI;
using Content.Client.Fax.UI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMShuttleConsoleLayoutTest
{
    private static readonly Vector2 MinimumViewport = new(640f, 480f);
    private static readonly Vector2 FireControlMinimumViewport = new(760f, 520f);
    private static readonly Vector2 ExpandedViewport = new(1060f, 720f);

    [Test]
    public async Task MinimumWindowSizeKeepsEveryRadarAndSidebarUsable()
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
                {
                    using var window = CreateWindow(activator, out var nav, out var map, out var dock);
                    AssertWindowContract(window);
                    AssertModeLayout(
                        window,
                        nav,
                        [nav, map, dock],
                        nav.FindControl<ShuttleNavControl>("NavRadar"),
                        nav.FindControl<BoxContainer>("RightDisplayNav"),
                        "NAV");
                }

                {
                    using var window = CreateWindow(activator, out var nav, out var map, out var dock);
                    AssertWindowContract(window);
                    AssertModeLayout(
                        window,
                        map,
                        [nav, map, dock],
                        map.FindControl<ShuttleMapControl>("MapRadar"),
                        map.FindControl<BoxContainer>("RightDisplayMap"),
                        "MAP");
                }

                {
                    using var window = CreateWindow(activator, out var nav, out var map, out var dock);
                    AssertWindowContract(window);
                    AssertModeLayout(
                        window,
                        dock,
                        [nav, map, dock],
                        dock.FindControl<ShuttleDockControl>("DockingControl"),
                        dock.FindControl<BoxContainer>("RightDisplayDock"),
                        "DOCK");
                }
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public async Task FireControlUsesSharedConsoleFrameAndTelemetryAtMinimumSize()
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
                using var fireWindow = activator.CreateInstance<FireControlWindow>(oneOff: true, inject: false);
                using var shuttleWindow = CreateWindow(activator, out var nav, out _, out _);
                using var faxWindow = activator.CreateInstance<FaxWindow>(oneOff: true, inject: false);

                var radar = fireWindow.FindControl<FireControlNavControl>("NavRadar");
                var controls = fireWindow.FindControl<BoxContainer>("ControlsBox");
                var sidebarScroll = controls.Parent as ScrollContainer;
                var consoleRoot = fireWindow.FindControl<LayoutContainer>("ConsoleRoot");
                var consoleSurface = fireWindow.FindControl<PanelContainer>("ConsoleSurface");
                var crtOverlay = fireWindow.FindControl<ShuttleConsoleCrtOverlay>("CrtOverlay");
                var fireTelemetry = fireWindow.FindControl<ShuttleCombatTelemetryPanel>("CombatTelemetry");
                var navTelemetry = nav.FindControl<ShuttleCombatTelemetryPanel>("CombatTelemetry");

                fireWindow.SetSize = FireControlMinimumViewport;
                fireWindow.Measure(FireControlMinimumViewport);
                fireWindow.Arrange(UIBox2.FromDimensions(Vector2.Zero, FireControlMinimumViewport));

                Assert.Multiple(() =>
                {
                    Assert.That(fireWindow.Resizable, Is.True);
                    Assert.That(faxWindow.Resizable, Is.True);
                    Assert.That(fireWindow.MinSize, Is.EqualTo(FireControlMinimumViewport));
                    Assert.That(fireWindow.Size, Is.EqualTo(FireControlMinimumViewport));
                    Assert.That(radar.Width, Is.GreaterThan(0f));
                    Assert.That(radar.Height, Is.GreaterThan(0f));
                    Assert.That(radar.RectClipContent, Is.True);
                    Assert.That(sidebarScroll, Is.Not.Null);
                    Assert.That(sidebarScroll!.VScrollEnabled, Is.True);
                    Assert.That(sidebarScroll.HScrollEnabled, Is.False);
                    Assert.That(sidebarScroll.ReserveScrollbarSpace, Is.True);
                    Assert.That(consoleSurface.Width, Is.GreaterThan(FireControlMinimumViewport.X * 0.95f));
                    Assert.That(crtOverlay.Width, Is.EqualTo(consoleRoot.Width).Within(1f));
                    Assert.That(crtOverlay.Height, Is.EqualTo(consoleRoot.Height).Within(1f));

                    Assert.That(fireTelemetry, Is.TypeOf<ShuttleCombatTelemetryPanel>());
                    Assert.That(navTelemetry, Is.TypeOf<ShuttleCombatTelemetryPanel>());
                    Assert.That(fireWindow.FindControl<ShuttleConsoleButton>("RefreshButton"), Is.Not.Null);
                    Assert.That(fireWindow.FindControl<ShuttleConsoleButton>("SelectAllButton"), Is.Not.Null);
                    Assert.That(fireWindow.FindControl<ShuttleConsoleButton>("IFFToggle").ToggleMode, Is.True);
                    Assert.That(fireWindow.FindControl<ShuttleConsoleButton>("DockToggle").ToggleMode, Is.True);
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }


    [Test]
    public async Task ShuttleConsoleShowsExplicitFlightAndDockingSyncStatuses()
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
                using var window = CreateWindow(activator, out var nav, out var map, out var dock);

                var activeModeStatus = window.FindControl<Label>("ActiveModeStatus");
                var navigationModeText = activeModeStatus.Text;
                window.SwitchMode(ShuttleConsoleWindow.ShuttleConsoleMode.Map);
                var mapModeText = activeModeStatus.Text;
                window.SwitchMode(ShuttleConsoleWindow.ShuttleConsoleMode.Dock);
                var dockingModeText = activeModeStatus.Text;

                Assert.Multiple(() =>
                {
                    Assert.That(navigationModeText, Is.Not.Empty);
                    Assert.That(mapModeText, Is.Not.EqualTo(navigationModeText));
                    Assert.That(dockingModeText, Is.Not.EqualTo(mapModeText));
                    Assert.That(nav.FindControl<Label>("DampenerModeStatus").Text, Is.Not.Empty);
                    Assert.That(nav.FindControl<Label>("GridPosition").Text, Does.Contain("m"));
                    Assert.That(nav.FindControl<Label>("GridOrientation").Text, Does.Contain("°"));
                    Assert.That(nav.FindControl<Label>("GridLinearVelocity").Text, Does.Contain("m/s"));
                    Assert.That(nav.FindControl<Label>("GridAngularVelocity").Text, Does.Contain("°/s"));
                    Assert.That(nav.FindControl<Label>("MaximumShuttleSpeedFeedback").Text, Is.Not.Empty);
                    Assert.That(nav.FindControl<LineEdit>("TargetX"), Is.Not.Null);
                    Assert.That(nav.FindControl<LineEdit>("TargetY"), Is.Not.Null);
                    Assert.That(nav.FindControl<ShuttleConsoleButton>("TargetSet"), Is.Not.Null);
                    Assert.That(nav.FindControl<ShuttleConsoleButton>("TargetHide").ToggleMode, Is.True);
                    Assert.That(nav.FindControl<Label>("TargetFeedback").Text, Is.Not.Empty);

                    Assert.That(map.FindControl<Label>("TargetingStatus").Text, Is.Not.Empty);
                    Assert.That(map.FindControl<Label>("CoordinateFeedback").Text, Is.Not.Empty);
                    Assert.That(map.FindControl<ShuttleConsoleButton>("CoordinateGoButton").MinWidth,
                        Is.GreaterThanOrEqualTo(84f));
                    Assert.That(map.FindControl<ShuttleConsoleButton>("CancelTargetingButton").Visible, Is.False);

                    Assert.That(dock.FindControl<Label>("FTLLockStatus").Text, Is.Not.Empty);
                    Assert.That(dock.FindControl<Label>("FTLLockHint").Text, Is.Not.Empty);
                    Assert.That(dock.FindControl<Label>("DockPortsStatus").Text, Is.Not.Empty);
                    Assert.That(dock.FindControl<Label>("DockPortsEmpty").Visible, Is.True);
                    Assert.That(dock.FindControl<ShuttleConsoleButton>("UndockAllButton").Disabled, Is.True);
                    Assert.That(dock.FindControl<PanelContainer>("UndockAllConfirmation").Visible, Is.False);

                    var enabled = dock.FindControl<ShuttleConsoleButton>("FTLLockEnabledButton");
                    var disabled = dock.FindControl<ShuttleConsoleButton>("FTLLockDisabledButton");
                    Assert.That(enabled.ToggleMode, Is.True);
                    Assert.That(disabled.ToggleMode, Is.True);
                });
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    [Test]
    public void ShuttleConsoleLocalizationHasUnitsFeedbackAndNoReplacementText()
    {
        var enConsole = ReadRepositoryFile("Resources/Locale/en-US/shuttles/console.ftl");
        var ruConsole = ReadRepositoryFile("Resources/Locale/ru-RU/shuttles/console.ftl");
        var enFrontier = ReadRepositoryFile("Resources/Locale/en-US/_NF/shuttles/console.ftl");
        var ruFrontier = ReadRepositoryFile("Resources/Locale/ru-RU/_NF/shuttles/console.ftl");

        foreach (var text in new[] { enConsole, ruConsole, enFrontier, ruFrontier })
        {
            Assert.That(text, Does.Not.Contain("???"),
                "Shuttle-console localization contains replacement question marks.");
            Assert.That(text, Does.Not.Contain("\uFFFD"),
                "Shuttle-console localization contains a Unicode replacement character.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(enConsole, Does.Contain("{$X}, {$Y} m"));
            Assert.That(enConsole, Does.Contain("m/s"));
            Assert.That(enConsole, Does.Contain("shuttle-console-coordinate-feedback-invalid"));
            Assert.That(enConsole, Does.Contain("shuttle-console-undock-all-confirmation"));
            Assert.That(enFrontier, Does.Contain("Cruise —"));
            Assert.That(enFrontier, Does.Not.Contain("Cruise ?"));
            Assert.That(enFrontier, Does.Contain("shuttle-console-target-feedback-active"));
            Assert.That(enFrontier, Does.Contain("shuttle-console-map-track-tooltip"));

            Assert.That(ruConsole, Does.Contain("Синхронизация БСС"));
            Assert.That(ruConsole, Does.Contain("shuttle-console-dock-port-state-connected"));
            Assert.That(ruFrontier, Does.Contain("Ограничение скорости"));
            Assert.That(ruFrontier, Does.Contain("«Дрейф» —"));
            Assert.That(ruFrontier, Does.Contain("shuttle-console-target-feedback-active"));
            Assert.That(ruFrontier, Does.Contain("shuttle-console-map-track-tooltip"));
        });
    }

    private static ShuttleConsoleWindow CreateWindow(
        IDynamicTypeFactory activator,
        out NavScreen nav,
        out MapScreen map,
        out DockingScreen dock)
    {
        var window = activator.CreateInstance<ShuttleConsoleWindow>(oneOff: true, inject: false);
        nav = window.FindControl<NavScreen>("NavContainer");
        map = window.FindControl<MapScreen>("MapContainer");
        dock = window.FindControl<DockingScreen>("DockContainer");
        return window;
    }

    private static void AssertWindowContract(ShuttleConsoleWindow window)
    {
        Assert.Multiple(() =>
        {
            Assert.That(window.Resizable, Is.True);
            Assert.That(window.MinSize, Is.EqualTo(MinimumViewport));
        });
    }

    private static void AssertModeLayout(
        ShuttleConsoleWindow window,
        Control activeMode,
        Control[] modes,
        Control radar,
        BoxContainer sidebarContents,
        string modeName)
    {
        foreach (var mode in modes)
        {
            mode.Visible = ReferenceEquals(mode, activeMode);
        }

        Assert.That(sidebarContents.Parent, Is.TypeOf<ScrollContainer>(),
            $"{modeName} sidebar must be hosted in its scrolling viewport");
        var sidebarScroll = (ScrollContainer)sidebarContents.Parent!;
        var consoleRoot = window.FindControl<LayoutContainer>("ConsoleRoot");
        var consoleSurface = window.FindControl<PanelContainer>("ConsoleSurface");
        var crtOverlay = window.FindControl<ShuttleConsoleCrtOverlay>("CrtOverlay");

        ArrangeWindow(window, MinimumViewport);
        var minimumRadarWidth = radar.Width;

        Assert.Multiple(() =>
        {
            Assert.That(window.Size, Is.EqualTo(MinimumViewport),
                $"{modeName} mode expanded the minimum-size window");
            Assert.That(activeMode.Width, Is.GreaterThan(0f), $"{modeName} mode has no width");
            Assert.That(activeMode.Height, Is.GreaterThan(0f), $"{modeName} mode has no height");

            Assert.That(radar.Width, Is.GreaterThan(0f), $"{modeName} radar has no width");
            Assert.That(radar.Height, Is.GreaterThan(0f), $"{modeName} radar has no height");
            Assert.That(radar.RectClipContent, Is.True,
                $"{modeName} radar can draw over adjacent console panels");
            Assert.That(radar.Width, Is.LessThan(648f),
                $"{modeName} radar retained its old fixed 648px width");

            Assert.That(sidebarScroll.Width, Is.GreaterThan(0f), $"{modeName} sidebar viewport has no width");
            Assert.That(sidebarScroll.Height, Is.GreaterThan(0f), $"{modeName} sidebar viewport has no height");
            Assert.That(sidebarScroll.Height, Is.LessThan(window.Height),
                $"{modeName} sidebar viewport escaped the window content area");
            Assert.That(sidebarScroll.VScrollEnabled, Is.True, $"{modeName} sidebar cannot scroll vertically");
            Assert.That(sidebarScroll.HScrollEnabled, Is.False, $"{modeName} sidebar unexpectedly scrolls horizontally");
            Assert.That(sidebarScroll.ReserveScrollbarSpace, Is.True,
                $"{modeName} sidebar does not reserve space for its scrollbar");
        });

        ArrangeWindow(window, ExpandedViewport);

        Assert.Multiple(() =>
        {
            Assert.That(window.Size, Is.EqualTo(ExpandedViewport),
                $"{modeName} mode did not retain the requested expanded size");
            Assert.That(consoleRoot.Width, Is.GreaterThan(ExpandedViewport.X * 0.95f),
                $"{modeName} console root did not fill the expanded window");
            Assert.That(consoleSurface.Width, Is.GreaterThan(ExpandedViewport.X * 0.95f),
                $"{modeName} surface remained pinned to its minimum width");
            Assert.That(consoleSurface.Height, Is.GreaterThan(ExpandedViewport.Y * 0.9f),
                $"{modeName} surface remained pinned to its minimum height");
            Assert.That(crtOverlay.Width, Is.EqualTo(consoleRoot.Width).Within(1f),
                $"{modeName} CRT overlay did not cover the console surface");
            Assert.That(crtOverlay.Height, Is.EqualTo(consoleRoot.Height).Within(1f),
                $"{modeName} CRT overlay did not cover the console surface");
            Assert.That(activeMode.Width, Is.GreaterThan(ExpandedViewport.X * 0.9f),
                $"{modeName} content remained pinned to the upper-left corner");
            Assert.That(activeMode.Height, Is.GreaterThan(ExpandedViewport.Y * 0.75f),
                $"{modeName} content left an empty area below the console");
            Assert.That(radar.Width, Is.GreaterThan(minimumRadarWidth + 300f),
                $"{modeName} radar did not consume the additional window width");
        });
    }

    private static void ArrangeWindow(ShuttleConsoleWindow window, Vector2 viewport)
    {
        window.SetSize = viewport;
        window.Measure(viewport);
        window.Arrange(UIBox2.FromDimensions(Vector2.Zero, viewport));
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, $"Missing repository file {relativePath}.");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate repository root from test output directory.");
        return string.Empty;
    }
}
