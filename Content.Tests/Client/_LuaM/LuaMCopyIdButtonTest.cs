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
}
