using System.IO;
using System.Text;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMAdminPlayerCameraLayoutTest
{
    [Test]
    public void PlayerTableExposesSafeLiveCameraAction()
    {
        var entryXaml = Read("Content.Client/Administration/UI/Tabs/PlayerTab/PlayerTabEntry.xaml");
        var headerXaml = Read("Content.Client/Administration/UI/Tabs/PlayerTab/PlayerTabHeader.xaml");
        var entryCode = Read("Content.Client/Administration/UI/Tabs/PlayerTab/PlayerTabEntry.xaml.cs");
        var cameraCommand = Read("Content.Server/Administration/Commands/CameraCommand.cs");
        var enLocale = Read("Resources/Locale/en-US/administration/ui/tabs/player-tab.ftl");
        var ruLocale = Read("Resources/Locale/ru-RU/administration/ui/tabs/player-tab.ftl");

        Assert.Multiple(() =>
        {
            Assert.That(entryXaml, Does.Contain("Name=\"CameraButton\""));
            Assert.That(headerXaml, Does.Contain("player-tab-camera"));
            Assert.That(entryCode, Does.Contain("CameraButton.Disabled = !player.Connected || player.NetEntity == null"));
            Assert.That(entryCode, Does.Contain("_console.ExecuteCommand($\"camera {target}\")"));
            Assert.That(cameraCommand, Does.Contain("[AdminCommand(AdminFlags.Admin)]"));
            Assert.That(cameraCommand, Does.Contain("new AdminCameraEui"));
            Assert.That(enLocale, Does.Contain("player-tab-camera = Camera"));
            Assert.That(ruLocale, Does.Contain("player-tab-camera = Камера"));
        });
    }

    private static string Read(string relativePath)
    {
        var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", ".."));
        return File.ReadAllText(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Encoding.UTF8);
    }
}
