using Content.Client.Changelog;
using NUnit.Framework;

namespace Content.Tests.Client._LuaM;

[TestFixture]
public sealed class LuaMChangelogReadSessionTest
{
    [Test]
    public void SuccessfulReadCutoffAdvancesWithoutRegressing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChangelogManager.AdvanceReadCutoff(10, 15), Is.EqualTo(15));
            Assert.That(ChangelogManager.AdvanceReadCutoff(15, 10), Is.EqualTo(15));
            Assert.That(ChangelogManager.AdvanceReadCutoff(15, 15), Is.EqualTo(15));
        });
    }

    [Test]
    public void ClosedOrSupersededLoadCannotAcknowledgeEntries()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChangelogWindow.CanApplyPopulateResult(4, 4, isOpen: true), Is.True);
            Assert.That(ChangelogWindow.CanApplyPopulateResult(4, 4, isOpen: false), Is.False);
            Assert.That(ChangelogWindow.CanApplyPopulateResult(3, 4, isOpen: true), Is.False);
        });
    }

    [Test]
    public void CorruptReadCutoffFallsBackWithoutBreakingNewsWindow()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChangelogManager.TryParseReadCutoff("42", out var valid), Is.True);
            Assert.That(valid, Is.EqualTo(42));
            Assert.That(ChangelogManager.TryParseReadCutoff("-4", out _), Is.False);
            Assert.That(ChangelogManager.TryParseReadCutoff("not-an-id", out _), Is.False);
        });
    }
}
