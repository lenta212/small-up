using System;
using Content.Client.PDA;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.XAML.Proxy;
using Robust.Shared.IoC;
using Robust.UnitTesting;

namespace Content.Tests.Client._LuaM;

[TestFixture]
public sealed class LuaMPdaNavigationButtonTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;

    private sealed class CompiledXamlOnlyProxyHelper : IXamlProxyHelper
    {
        public bool Populate(Type type, object control)
        {
            return false;
        }
    }

    protected override void OverrideIoC()
    {
        base.OverrideIoC();
        IoCManager.Register<IXamlProxyHelper, CompiledXamlOnlyProxyHelper>(overwrite: true);
    }

    [OneTimeSetUp]
    public void Setup()
    {
        IoCManager.Resolve<IUserInterfaceManager>().InitializeTesting();
    }

    [Test]
    public void NavigationButtonCanBeConstructedBeforeItsXamlChildrenExist()
    {
        Assert.DoesNotThrow(() =>
        {
            using var button = new PdaNavigationButton();
        });
    }
}
