using System;
using System.Linq;
using Content.Client.UserInterface.Systems.Guidebook;
using NUnit.Framework;

namespace Content.Tests.Client.UserInterface.Systems.Guidebook;

[TestFixture]
[TestOf(typeof(FrontierTutorialFlow))]
public sealed class LuaMFrontierTutorialFlowTest
{
    [Test]
    public void StartsAtFirstIncompleteTopicAndKeepsOrder()
    {
        var flow = new FrontierTutorialFlow(topicCount: 5, completedMask: 0b00101);

        Assert.That(flow.CurrentIndex, Is.EqualTo(1));
        Assert.That(flow.CompleteCurrent(), Is.True);
        Assert.That(flow.CompletedMask, Is.EqualTo(0b00111));
        Assert.That(flow.CurrentIndex, Is.EqualTo(3));
        Assert.That(flow.CompleteCurrent(), Is.True);
        Assert.That(flow.CurrentIndex, Is.EqualTo(4));
    }

    [Test]
    public void CompletesOnlyWhenEveryTopicWasHandled()
    {
        var flow = new FrontierTutorialFlow(topicCount: 3, completedMask: 0);

        Assert.That(flow.IsComplete, Is.False);
        Assert.That(flow.CompleteCurrent(), Is.True);
        Assert.That(flow.CompleteCurrent(), Is.True);
        Assert.That(flow.CompleteCurrent(), Is.True);
        Assert.That(flow.IsComplete, Is.True);
        Assert.That(flow.CompletedMask, Is.EqualTo(0b111));
        Assert.That(flow.CompleteCurrent(), Is.False);
    }

    [Test]
    public void ChecklistCanSelectAnyPendingTopicAndWrapToEarlierWork()
    {
        var flow = new FrontierTutorialFlow(topicCount: 5, completedMask: 0b00101);

        Assert.Multiple(() =>
        {
            Assert.That(flow.SelectTopic(4), Is.True);
            Assert.That(flow.CurrentIndex, Is.EqualTo(4));
            Assert.That(flow.SelectTopic(2), Is.False, "Completed topics must stay read-only in the checklist.");
            Assert.That(flow.SelectTopic(8), Is.False);
        });

        Assert.That(flow.CompleteCurrent(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CompletedMask, Is.EqualTo(0b10101));
            Assert.That(flow.CurrentIndex, Is.EqualTo(1), "Selection should wrap to an earlier unfinished topic.");
            Assert.That(flow.IsTopicComplete(4), Is.True);
            Assert.That(flow.IsTopicComplete(3), Is.False);
        });
    }

    [Test]
    public void ConstructorDropsBitsOutsideCatalogWithoutInventingProgress()
    {
        var flow = new FrontierTutorialFlow(topicCount: 3, completedMask: unchecked((int) 0xFFFF_FFF9));

        Assert.That(flow.CompletedMask, Is.EqualTo(0b001));
        Assert.That(flow.CurrentIndex, Is.EqualTo(1));
    }

    [Test]
    public void UnansweredLegacyChoiceStartsFreshForANewPlayer()
    {
        var migration = FrontierTutorialFlow.Migrate(
            legacyChoice: 0,
            completedMask: 0,
            storedVersion: 0,
            topicCount: 4);

        Assert.That(migration.Compatible, Is.True);
        Assert.That(migration.LegacyChoice, Is.Zero);
        Assert.That(migration.CompletedMask, Is.Zero);
        Assert.That(migration.Version, Is.EqualTo(FrontierTutorialCatalog.ProgressVersion));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void AnsweredLegacyChoiceDoesNotAmbushExistingPlayers(int legacyChoice)
    {
        var migration = FrontierTutorialFlow.Migrate(
            legacyChoice,
            completedMask: 0,
            storedVersion: 0,
            topicCount: 4);

        Assert.That(migration.Compatible, Is.True);
        Assert.That(migration.LegacyChoice, Is.Zero);
        Assert.That(migration.CompletedMask, Is.EqualTo(0b1111));
        Assert.That(migration.Version, Is.EqualTo(FrontierTutorialCatalog.ProgressVersion));
    }

    [Test]
    public void PlaytimeGateOffersOnlyNewOrAlreadyStartedChecklists()
    {
        var window = TimeSpan.FromMinutes(180);

        Assert.Multiple(() =>
        {
            Assert.That(
                FrontierTutorialFlow.ShouldOfferForPlaytime(0, 4, TimeSpan.FromMinutes(30), window),
                Is.True,
                "A genuinely new player should receive the readiness check.");
            Assert.That(
                FrontierTutorialFlow.ShouldOfferForPlaytime(0, 4, TimeSpan.FromMinutes(600), window),
                Is.False,
                "A veteran with no sequential progress should not be interrupted.");
            Assert.That(
                FrontierTutorialFlow.ShouldOfferForPlaytime(0b0010, 4, TimeSpan.FromMinutes(600), window),
                Is.True,
                "A started checklist remains resumable after the new-player window.");
            Assert.That(
                FrontierTutorialFlow.ShouldOfferForPlaytime(0b1111, 4, TimeSpan.FromMinutes(30), window),
                Is.False,
                "A completed checklist must stay complete.");
        });
    }

    [Test]
    public void NewerProgressSchemaIsNeverRewritten()
    {
        var futureVersion = FrontierTutorialCatalog.ProgressVersion + 1;
        var migration = FrontierTutorialFlow.Migrate(
            legacyChoice: 2,
            completedMask: unchecked((int) 0x8000_0001),
            storedVersion: futureVersion,
            topicCount: 4);

        Assert.That(migration.Compatible, Is.False);
        Assert.That(migration.LegacyChoice, Is.EqualTo(2));
        Assert.That(migration.CompletedMask, Is.EqualTo(unchecked((int) 0x8000_0001)));
        Assert.That(migration.Version, Is.EqualTo(futureVersion));
    }

    [Test]
    public void CatalogBitPositionsAreUniqueAndWithinMaskCapacity()
    {
        Assert.That(FrontierTutorialCatalog.Topics, Has.Count.InRange(1, 30));
        Assert.That(
            FrontierTutorialCatalog.Topics.Select(topic => topic.Guide.Id),
            Is.Unique);
        Assert.That(
            FrontierTutorialCatalog.Topics.Select(topic => topic.NameLocId),
            Is.Unique);
    }
}
