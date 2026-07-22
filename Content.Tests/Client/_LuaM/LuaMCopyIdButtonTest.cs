using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Content.Client._LuaM.Sector;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.UnitTesting;

namespace Content.Tests.Client._LuaM;

[TestFixture]
public sealed class LuaMCopyIdButtonTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    private sealed class RecordingClipboard : IClipboardManager
    {
        public string Text { get; private set; } = string.Empty;

        public Task<string> GetText()
        {
            return Task.FromResult(Text);
        }

        public void SetText(string text)
        {
            Text = text;
        }
    }

    [OneTimeSetUp]
    public void Setup()
    {
        IoCManager.Resolve<IUserInterfaceManager>().InitializeTesting();
    }

    [Test]
    public void CopyButtonCopiesShortIdToClipboard()
    {
        var clipboard = new RecordingClipboard();

        var fragment = (LuaMSectorStatusUiFragment) FormatterServices.GetUninitializedObject(
            typeof(LuaMSectorStatusUiFragment));

        var clipboardField = typeof(LuaMSectorStatusUiFragment).GetField(
            "_clipboard",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(clipboardField, Is.Not.Null, "Missing private clipboard dependency field");
        clipboardField!.SetValue(fragment, clipboard);

        var method = typeof(LuaMSectorStatusUiFragment).GetMethod(
            "MakeCopyIdButton",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Missing private MakeCopyIdButton method");

        var button = (Button) method!.Invoke(fragment, new object[] { "Route-98765" })!;
        Assert.That(button.Text, Is.EqualTo("RO-98765"));

        button.Mode = BaseButton.ActionMode.Press;

        var click = new GUIBoundKeyEventArgs(
            EngineKeyFunctions.UIClick,
            BoundKeyState.Down,
            new ScreenCoordinates(),
            true,
            Vector2.Zero,
            Vector2.Zero);

        var keyBindDown = typeof(BaseButton).GetMethod(
            "KeyBindDown",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(keyBindDown, Is.Not.Null, "Missing BaseButton.KeyBindDown method");
        keyBindDown!.Invoke(button, new object[] { click });

        Assert.That(clipboard.Text, Is.EqualTo("RO-98765"));
    }

    [Test]
    public void SectorStatusNamesOperationalSeverityAndVisibleItemCount()
    {
        var stable = (string) InvokeFragmentBuilder("GetOperationalStateLocKey", 0, 0, 0);
        var monitoring = (string) InvokeFragmentBuilder("GetOperationalStateLocKey", 0, 2, 2);
        var danger = (string) InvokeFragmentBuilder("GetOperationalStateLocKey", 2, 0, 3);
        var critical = (string) InvokeFragmentBuilder("GetOperationalStateLocKey", 1, 0, 5);
        var limitedCount = (int) InvokeFragmentBuilder("GetVisibleCount", 11, 6);
        var completeCount = (int) InvokeFragmentBuilder("GetVisibleCount", 5, 6);

        Assert.That(stable, Is.EqualTo("luam-sector-status-operational-stable"));
        Assert.That(monitoring, Is.EqualTo("luam-sector-status-operational-monitoring"));
        Assert.That(danger, Is.EqualTo("luam-sector-status-operational-danger"));
        Assert.That(critical, Is.EqualTo("luam-sector-status-operational-critical"));
        Assert.That(limitedCount, Is.EqualTo(6));
        Assert.That(completeCount, Is.EqualTo(5));
    }

    private static object InvokeFragmentBuilder(string name, params object[] arguments)
    {
        var method = typeof(LuaMSectorStatusUiFragment).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing private {name} method");

        return method!.Invoke(null, arguments)!;
    }
}
