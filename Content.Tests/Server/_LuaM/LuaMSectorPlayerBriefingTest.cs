#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._LuaM.Sector;
using Content.Shared._LuaM.Sector;
using NUnit.Framework;
using Robust.Shared.Prototypes;

namespace Content.Tests.Server._LuaM;

[TestFixture]
public sealed class LuaMSectorPlayerBriefingTest
{
    [Test]
    public void NextActionsPrioritizeOpenLeadRiskAndSectorCondition()
    {
        var status = BuildStatus();
        var openStory = new LuaMSectorStoryRecord
        {
            Story = new ProtoId<LuaMSectorStoryPrototype>("LuaMOpenLead"),
            Title = "Rescue lane",
            ContractDescription = "Coordinates marker: GPS 120, -45.",
            News = "Fallback GPS 12, 34.",
            Hazard = "Damaged beacon and weak comms.",
        };

        var actions = LuaMSectorPlayerBriefing.BuildNextActions(status, openStory, 3);

        Assert.That(actions, Has.Length.EqualTo(3));
        Assert.That(actions[0], Does.Contain("Rescue lane"));
        Assert.That(actions[0], Does.Contain("GPS 120, -45"));
        Assert.That(actions[1], Does.Contain("Damaged beacon"));
        Assert.That(actions[2], Does.Contain("Dust lane"));
    }

    [Test]
    public void DailyDigestIncludesActionableRecapAndRespectsLineLimit()
    {
        var status = BuildStatus();
        var openStory = new LuaMSectorStoryRecord
        {
            Story = new ProtoId<LuaMSectorStoryPrototype>("LuaMOpenLead"),
            Title = "Black box trail",
            ContractDescription = "No marker yet.",
            Hazard = "Recorder signal is unstable.",
        };

        var lines = LuaMSectorPlayerBriefing.BuildDailyDigestLines(status, openStory, activePlayers: 2, maxLines: 6);

        Assert.That(lines, Has.Length.EqualTo(6));
        Assert.That(lines[0], Does.Contain("Сводка дня"));
        Assert.That(lines.Any(line => line.Contains("Black box trail")), Is.True);
        Assert.That(lines.Any(line => line.Contains("Radiation echo")), Is.True);
        Assert.That(lines.Any(line => line.Contains("Encrypted cache")), Is.True);
        Assert.That(lines.Any(line => line.Contains("Цель на сессию")), Is.True);
        Assert.That(lines.All(line => line.Length <= 180), Is.True);
    }

    [Test]
    public void MarkerExtractionKeepsGpsCoordinatesCompact()
    {
        var route = LuaMSectorPlayerBriefing.ExtractMarkerLocation(
            "Briefing. Координаты маркера: GPS 42, -17; close the report after arrival.");

        Assert.That(route, Is.EqualTo("GPS 42, -17"));
    }

    [Test]
    public void QuestTasksExposeSeveralClearPlayerTasks()
    {
        var status = BuildStatus();
        var openStory = new LuaMSectorStoryRecord
        {
            Story = new ProtoId<LuaMSectorStoryPrototype>("LuaMOpenLead"),
            Title = "Rescue lane",
            ContractDescription = "Coordinates marker: GPS 120, -45.",
            Hazard = "Damaged beacon and weak comms.",
        };
        var automation = new LuaMSectorAutomationUiEntry
        {
            CanPingRoute = true,
            ActiveRouteMarker = "GPS 120, -45",
            RoutePingCount = 1,
        };
        var preferred = new LuaMSectorPreferredProcessUiEntry
        {
            TemplateId = "quiet-distress",
            Title = "Quiet distress",
            Vessel = "Prospector",
            ReputationTarget = "Salvage",
            CurrentReputation = 450,
            RequiredReputation = 500,
            Tier = "known",
            BaseReward = 1200,
            ReputationBonus = 500,
            Unlocked = true,
            CanRequestNow = true,
        };
        var insurance = new LuaMSectorInsuranceUiEntry
        {
            StoryId = "LuaMInsurance",
            Title = "Cracked hull",
            Vessel = "Courier",
            RequestedAmount = 650,
            Claimed = false,
        };
        var registry = new LuaMSectorRegistryUiEntry
        {
            StoryId = "LuaMRegistry",
            Title = "Frontier filing",
            Vessel = "Skiff",
            CanRegisterCompany = true,
            ServiceLine = "Company charter pending",
        };

        var tasks = LuaMSectorPlayerBriefing.BuildQuestTasks(
            status,
            openStory,
            automation,
            [],
            [preferred],
            [insurance],
            [registry],
            8);

        Assert.That(tasks, Has.Length.GreaterThanOrEqualTo(6));
        Assert.That(tasks[0].TaskId, Is.EqualTo("active-route"));
        Assert.That(tasks[0].Objective, Does.Contain("Долети до GPS 120, -45"));
        Assert.That(tasks.Select(task => task.TaskId), Does.Contain("route-ping"));
        Assert.That(tasks.Select(task => task.TaskId), Does.Contain("hazard-LuaMHazard"));
        Assert.That(tasks.Select(task => task.TaskId), Does.Contain("condition-dust-lane"));
        Assert.That(tasks.Select(task => task.TaskId), Does.Contain("preferred-quiet-distress"));
        Assert.That(tasks.Select(task => task.TaskId), Does.Contain("insurance-LuaMInsurance"));
        Assert.That(tasks.Select(task => task.TaskId), Does.Contain("registry-LuaMRegistry"));
        Assert.That(tasks.All(task => !string.IsNullOrWhiteSpace(task.Title)), Is.True);
        Assert.That(tasks.All(task => !string.IsNullOrWhiteSpace(task.Objective)), Is.True);
    }

    [Test]
    public void QuestTasksStartWithProcessRequestWhenNoRouteIsOpen()
    {
        var tasks = LuaMSectorPlayerBriefing.BuildQuestTasks(
            BuildStatus(),
            null,
            new LuaMSectorAutomationUiEntry
            {
                CanRequestDynamicEvent = true,
                RequestBlockReason = "ready",
            },
            [],
            [],
            [],
            [],
            3);

        Assert.That(tasks[0].TaskId, Is.EqualTo("start-process"));
        Assert.That(tasks[0].Objective, Does.Contain("Сгенерировать зацепку"));
    }

    [Test]
    public void QuestTasksAddRescueFollowUpWhenAfterActionHadBlockers()
    {
        var status = BuildStatus(
            recentHistory:
            [
                new LuaMSectorHistoryStatus(
                    "Rescue",
                    new ProtoId<LuaMSectorStoryPrototype>("LuaMSectorRescueAfterAction"),
                    "Triage shuttle",
                    "LuaM Rescue",
                    "treatment=stable; evacuation=secured onboard; blockers=threat/crowd/route=0/1/1; blockers=2; scene=route pressure; playerContribution=unverified; teamStatus=available"),
            ]);

        var tasks = LuaMSectorPlayerBriefing.BuildQuestTasks(
            status,
            null,
            new LuaMSectorAutomationUiEntry
            {
                CanRequestDynamicEvent = true,
                RequestBlockReason = "ready",
            },
            [],
            [],
            [],
            [],
            4);

        var followUp = tasks.Single(task => task.TaskId.StartsWith("rescue-followup", StringComparison.Ordinal));
        Assert.That(followUp.TaskId, Does.Contain("LuaMSectorRescueAfterAction"));
        Assert.That(followUp.Title, Does.Contain("rescue"));
        Assert.That(followUp.Objective, Does.Contain("threat/crowd/route=0/1/1"));
        Assert.That(followUp.Objective, Does.Contain("blockers=2"));
        Assert.That(followUp.Location, Is.EqualTo("Triage shuttle"));
        Assert.That(followUp.TurnIn, Does.Contain("LuaM"));
        Assert.That(followUp.Active, Is.True);
    }

    [Test]
    public void QuestTasksHideRescueFollowUpAfterBlockersWereCleared()
    {
        var status = BuildStatus(
            recentHistory:
            [
                new LuaMSectorHistoryStatus(
                    "Rescue",
                    new ProtoId<LuaMSectorStoryPrototype>("LuaMSectorRescueAfterAction"),
                    "Triage shuttle",
                    "LuaM Rescue",
                    "treatment=stable; evacuation=secured onboard; blockers=threat/crowd/route=0/1/1; blockers=2; blockersCleared=true; clearedBy=Engineer; clearedNote=route opened; playerContribution=verified; teamStatus=available"),
            ]);

        var tasks = LuaMSectorPlayerBriefing.BuildQuestTasks(
            status,
            null,
            new LuaMSectorAutomationUiEntry
            {
                CanRequestDynamicEvent = true,
                RequestBlockReason = "ready",
            },
            [],
            [],
            [],
            [],
            4);

        Assert.That(tasks.Any(task => task.TaskId.StartsWith("rescue-followup", StringComparison.Ordinal)), Is.False);
    }

    private static LuaMSectorStatusSnapshot BuildStatus(LuaMSectorHistoryStatus[]? recentHistory = null)
    {
        return new LuaMSectorStatusSnapshot(
            totalStories: 4,
            activeHazards: 2,
            acknowledgedHazards: 1,
            insurancePayouts: 0,
            blackBoxRecoveries: 1,
            companyRecords: 0,
            shipRecords: 0,
            activeConditions: 1,
            lockedStories: 1,
            reputationLedger: new Dictionary<string, int>
            {
                ["Salvage"] = 450,
            },
            reputation:
            [
                new LuaMSectorReputationStatus("Salvage", 450, "known", 500),
            ],
            hazards:
            [
                new LuaMSectorHazardStatus(
                    new ProtoId<LuaMSectorStoryPrototype>("LuaMHazard"),
                    "Radiation echo",
                    "Localized radiation echo.",
                    "Marker description.",
                    severity: 4,
                    rewardBonus: 750,
                    acknowledged: false,
                    resolved: false),
            ],
            conditions:
            [
                new LuaMSectorConditionStatus(
                    "dust-lane",
                    "Dust lane",
                    severity: 3,
                    "Sensor drift is likely near the western trade route.",
                    "integration-test",
                    active: true),
            ],
            lockedLeads:
            [
                new LuaMSectorLockedStoryStatus(
                    new ProtoId<LuaMSectorStoryPrototype>("LuaMLockedLead"),
                    "Encrypted cache",
                    "Salvage",
                    requiredValue: 500,
                    currentValue: 450),
            ],
            recentHistory: recentHistory ??
            [
                new LuaMSectorHistoryStatus(
                    "BlackBox",
                    new ProtoId<LuaMSectorStoryPrototype>("LuaMHistory"),
                    "Recovered recorder",
                    "pilot",
                    "Flight recorder returned to the sector board."),
            ]);
    }
}
