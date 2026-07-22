using System.Collections.Generic;
using System.Numerics;
using Content.Client.PDA;
using Content.Client.PDA.Ringer;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._LuaM;

[TestFixture]
public sealed class LuaMPdaTextLayoutTest
{
    private static readonly Vector2 PdaViewport = new(640f, 520f);
    private static readonly Vector2 RingtoneViewport = new(520f, 270f);

    [Test]
    public async Task ServicesProgramsSettingsAndRingtoneKeepReadableLabels()
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
                using var pda = activator.CreateInstance<PdaMenu>(oneOff: true, inject: false);
                pda.UpdateAvailablePrograms(new List<(EntityUid, CartridgeComponent)>
                {
                    (new EntityUid(1), new CartridgeComponent
                    {
                        ProgramName = "comp-pda-ui-programs-title",
                        InstallationStatus = InstallationStatus.Installed,
                    }),
                });

                pda.ChangeView(PdaMenu.ProgramListView);
                Arrange(pda, PdaViewport);

                Assert.That(pda.Resizable, Is.True);

                AssertReadable(pda.ProgramListButton.FindControl<Label>("Label"));
                AssertReadable(pda.FindControl<PdaNavigationButton>("ServicesButton").FindControl<Label>("Label"));
                AssertReadable(pda.FindControl<PdaNavigationButton>("SettingsButton").FindControl<Label>("Label"));
                AssertReadable(pda.FindControl<Label>("ProgramsSectionTitle"));
                AssertReadable(pda.FindControl<Label>("ProgramsSectionDescription"));

                var programList = pda.FindControl<BoxContainer>("ProgramList");
                var programItem = programList.GetChild(0) as PdaProgramItem;
                Assert.That(programItem, Is.Not.Null);
                AssertReadable(programItem!.ProgramName);
                AssertReadable(programItem.InstallButton.Label);

                using var servicesPda = activator.CreateInstance<PdaMenu>(oneOff: true, inject: false);
                servicesPda.ChangeView(PdaMenu.ServicesView);
                Arrange(servicesPda, PdaViewport);

                AssertReadable(servicesPda.FindControl<Label>("ServicesSectionTitle"));
                AssertReadable(servicesPda.FindControl<Label>("ServicesSectionDescription"));
                AssertReadable(servicesPda.FindControl<Label>("BankTransferRecipientLabel"));
                AssertReadable(servicesPda.FindControl<Label>("BankTransferAmountLabel"));

                Assert.Multiple(() =>
                {
                    Assert.That(PdaMenu.ServicesView, Is.Not.EqualTo(PdaMenu.HomeView));
                    Assert.That(servicesPda.FindControl<LineEdit>("BankTransferRecipientEdit").Width, Is.GreaterThan(0f));
                    Assert.That(servicesPda.FindControl<LineEdit>("BankTransferAmountEdit").Width, Is.GreaterThan(0f));
                });

                using var settingsPda = activator.CreateInstance<PdaMenu>(oneOff: true, inject: false);
                settingsPda.ChangeView(PdaMenu.SettingsView);
                Arrange(settingsPda, PdaViewport);

                AssertReadable(settingsPda.FindControl<Label>("SettingsSectionTitle"));
                AssertReadable(settingsPda.FindControl<Label>("SettingsSectionDescription"));

                var ringtoneEntry = settingsPda.FindControl<PdaSettingsButton>("AccessRingtoneButton");
                AssertReadable(ringtoneEntry.FindControl<Label>("OptionName"));
                AssertReadable(ringtoneEntry.FindControl<Label>("OptionDescription"));

                using var ringtone = activator.CreateInstance<RingtoneMenu>(oneOff: true, inject: false);
                Arrange(ringtone, RingtoneViewport);

                Assert.Multiple(() =>
                {
                    Assert.That(ringtone.MinSize, Is.EqualTo(new Vector2(500f, 260f)));
                    Assert.That(ringtone.Size, Is.EqualTo(RingtoneViewport));
                });

                AssertReadable(ringtone.FindControl<Label>("RingtoneTitle"));
                AssertReadable(ringtone.FindControl<Label>("RingtoneHint"));
                AssertReadable(ringtone.FindControl<Label>("RingtoneSequenceTitle"));
                AssertReadable(ringtone.FindControl<Label>("RingtoneStatusLabel"));
                AssertReadable(ringtone.TestRingerButton.Label);
                AssertReadable(ringtone.SetRingerButton.Label);

                foreach (var noteInput in ringtone.RingerNoteInputs)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(noteInput.Width, Is.GreaterThanOrEqualTo(50f));
                        Assert.That(noteInput.Height, Is.GreaterThan(0f));
                    });
                }
            });
        }
        finally
        {
            await pair.CleanReturnAsync();
        }
    }

    private static void Arrange(Control control, Vector2 viewport)
    {
        control.SetSize = viewport;
        control.Measure(viewport);
        control.Arrange(UIBox2.FromDimensions(Vector2.Zero, viewport));
    }

    private static void AssertReadable(Label label)
    {
        Assert.Multiple(() =>
        {
            Assert.That(label.Text, Is.Not.Null.And.Not.Empty);
            Assert.That(label.Visible, Is.True);
            Assert.That(label.Width, Is.GreaterThan(0f));
            Assert.That(label.Height, Is.GreaterThan(0f));
            Assert.That(label.FontColorOverride, Is.Not.Null);
            Assert.That(label.FontColorOverride!.Value.A, Is.GreaterThan(0f));
        });
    }
}
