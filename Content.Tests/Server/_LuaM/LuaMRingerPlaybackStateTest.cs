using System.Linq;
using Content.Server.PDA.Ringer;
using Content.Shared.PDA;
using NUnit.Framework;

namespace Content.Tests.Server._LuaM;

[TestFixture]
public sealed class LuaMRingerPlaybackStateTest
{
    private static readonly Note[] ValidRingtone =
    [
        Note.A,
        Note.B,
        Note.C,
        Note.D,
        Note.E,
        Note.F,
    ];

    [Test]
    public void PreviewAndSaveValidationRejectMalformedSequences()
    {
        var invalidNote = ValidRingtone.ToArray();
        invalidNote[2] = (Note) byte.MaxValue;

        Assert.Multiple(() =>
        {
            Assert.That(RingerSystem.IsValidRingtone(ValidRingtone), Is.True);
            Assert.That(RingerSystem.IsValidRingtone(ValidRingtone[..^1]), Is.False);
            Assert.That(RingerSystem.IsValidRingtone(invalidNote), Is.False);
            Assert.That(RingerSystem.IsValidRingtone(null), Is.False);
        });
    }

    [Test]
    public void IncomingSignalPreemptsPreviewAndKeepsUnsavedEditorInput()
    {
        var ringer = new RingerComponent
        {
            TimeElapsed = 0.17f,
            NoteCount = 4,
        };
        var active = new ActiveRingerComponent
        {
            PreviewRingtone = ValidRingtone.ToArray(),
            PreserveEditorInput = true,
        };

        Assert.That(RingerSystem.PrepareIncomingPlayback(ringer, active), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(active.PreviewRingtone, Is.Null);
            Assert.That(active.PreserveEditorInput, Is.True);
            Assert.That(ringer.TimeElapsed, Is.Zero);
            Assert.That(ringer.NoteCount, Is.Zero);
        });

        Assert.That(RingerSystem.PrepareIncomingPlayback(ringer, active), Is.False);
    }

    [Test]
    public void OrdinaryIncomingSignalDoesNotClaimEditorPreservation()
    {
        var ringer = new RingerComponent();
        var active = new ActiveRingerComponent();

        Assert.That(RingerSystem.PrepareIncomingPlayback(ringer, active), Is.False);
        Assert.That(active.PreserveEditorInput, Is.False);
    }
}
