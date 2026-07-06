#nullable enable

using System.Linq;
using Content.Shared._LuaM.Sector;

namespace Content.Server._LuaM.Sector;

public static class LuaMSectorPlayerBriefing
{
    private const int MaxLineLength = 180;

    public static string BuildDigest(LuaMSectorStatusSnapshot status, LuaMSectorStoryRecord? openStory, int activePlayers)
    {
        var lead = openStory != null
            ? $"открыта цель \"{Trim(openStory.Title, 80)}\""
            : "открытой цели нет";
        var recent = status.RecentHistory.FirstOrDefault();
        var recentText = recent != null
            ? $"последняя запись: {Trim(recent.Title, 80)}"
            : "история сектора пока пустая";

        return $"операторов {activePlayers}; {lead}; угроз {status.ActiveHazards}; условий {status.ActiveConditions}; {recentText}";
    }

    public static string[] BuildDailyDigestLines(
        LuaMSectorStatusSnapshot status,
        LuaMSectorStoryRecord? openStory,
        int activePlayers,
        int maxLines = 5)
    {
        var lines = new List<string>
        {
            $"Сводка дня: операторов {activePlayers}, активных рисков {status.ActiveHazards}, условий {status.ActiveConditions}, закрытых зацепок {status.LockedStories}",
        };

        if (openStory != null)
        {
            var route = ExtractEventRouteLocation(openStory);
            lines.Add(string.IsNullOrWhiteSpace(route)
                ? $"Активная линия: \"{openStory.Title}\"; маршрут не уточнен, запросите \"ИИ, маршрут\""
                : $"Активная линия: \"{openStory.Title}\"; маршрут {route}");
        }
        else
        {
            lines.Add("Активной линии нет: можно взять новый процесс через \"ИИ, задание\" или терминал LuaM");
        }

        var recent = status.RecentHistory
            .Take(2)
            .Select(entry => $"{entry.Category}: {entry.Title} - {entry.Summary}")
            .ToArray();

        if (recent.Length == 0)
        {
            lines.Add("Недавних записей нет: начните цикл с отчета, black box, регистрации судна или первого контракта");
        }
        else
        {
            foreach (var entry in recent)
                lines.Add($"Недавно: {entry}");
        }

        var strongestHazard = status.Hazards
            .Where(hazard => !hazard.Resolved)
            .OrderByDescending(hazard => hazard.Severity)
            .ThenBy(hazard => hazard.Title)
            .FirstOrDefault();

        if (strongestHazard != null)
        {
            var state = strongestHazard.Acknowledged
                ? "уже подан"
                : "еще не подан";
            lines.Add($"Главный риск: HZ-{strongestHazard.Severity} {strongestHazard.Title}, отчет {state}, бонус +{strongestHazard.RewardBonus}");
        }

        var closestLockedLead = status.LockedLeads
            .OrderBy(lead => Math.Max(0, lead.RequiredValue - lead.CurrentValue))
            .ThenBy(lead => lead.Title)
            .FirstOrDefault();

        if (closestLockedLead != null)
        {
            var remaining = Math.Max(0, closestLockedLead.RequiredValue - closestLockedLead.CurrentValue);
            lines.Add($"Ближайшее открытие: \"{closestLockedLead.Title}\" после +{remaining} репутации у {closestLockedLead.RequiredTarget}");
        }

        var dailyGoal = BuildNextActions(status, openStory, 1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(dailyGoal))
            lines.Add($"Цель на сессию: {dailyGoal}");

        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => Trim(line, MaxLineLength))
            .Distinct()
            .Take(Math.Max(1, maxLines))
            .ToArray();
    }

    public static string[] BuildNextActions(
        LuaMSectorStatusSnapshot status,
        LuaMSectorStoryRecord? openStory,
        int maxActions = 5)
    {
        var actions = new List<string>();

        if (openStory != null)
        {
            var route = ExtractEventRouteLocation(openStory);
            if (!string.IsNullOrWhiteSpace(route))
            {
                actions.Add($"Текущая цель: \"{openStory.Title}\"; летите к {route} и сдайте результат через LuaM-метку или полевой акт");
            }
            else
            {
                actions.Add($"Текущая цель: \"{openStory.Title}\"; сначала запросите \"ИИ, маршрут\" или откройте терминал LuaM");
            }

            if (!string.IsNullOrWhiteSpace(openStory.Hazard))
                actions.Add($"Перед вылетом учтите риск: {Trim(openStory.Hazard, 120)}");
        }
        else if (status.RecentHistory.Count == 0)
        {
            actions.Add("Старт: откройте КПК/терминал LuaM, посмотрите карту сектора и возьмите первый контракт или запросите \"ИИ, задание\"");
        }
        else
        {
            actions.Add("Открытой цели нет: запросите \"ИИ, задание\" или выберите доступный процесс в терминале LuaM");
        }

        var strongestCondition = status.Conditions
            .Where(condition => condition.Active)
            .OrderByDescending(condition => condition.Severity)
            .ThenBy(condition => condition.Title)
            .FirstOrDefault();

        if (strongestCondition != null)
        {
            var conditionAction = strongestCondition.Severity >= 4
                ? "не летите в одиночку без связи, кислорода и пути отхода"
                : "используйте как фон маршрута, но не откладывайте основную цель";
            actions.Add($"Условие SC-{strongestCondition.Severity} {strongestCondition.Title}: {conditionAction}");
        }

        var strongestHazard = status.Hazards
            .Where(hazard => !hazard.Resolved)
            .OrderByDescending(hazard => hazard.Severity)
            .ThenBy(hazard => hazard.Title)
            .FirstOrDefault();

        if (strongestHazard != null)
        {
            var state = strongestHazard.Acknowledged
                ? "уже подана"
                : "еще не подана";
            actions.Add($"Риск HZ-{strongestHazard.Severity} {strongestHazard.Title}: запись {state}, бонус +{strongestHazard.RewardBonus}");
        }

        var closestLockedLead = status.LockedLeads
            .OrderBy(lead => Math.Max(0, lead.RequiredValue - lead.CurrentValue))
            .ThenBy(lead => lead.Title)
            .FirstOrDefault();

        if (closestLockedLead != null)
        {
            var remaining = Math.Max(0, closestLockedLead.RequiredValue - closestLockedLead.CurrentValue);
            actions.Add($"Для открытия \"{closestLockedLead.Title}\" нужно еще {remaining} репутации у {closestLockedLead.RequiredTarget}");
        }

        var bestReputation = status.Reputation
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Target)
            .FirstOrDefault();

        if (bestReputation != null && bestReputation.Value > 0)
        {
            actions.Add($"Репутация {bestReputation.Target} {bestReputation.Value} ({bestReputation.Tier}) дает +{bestReputation.RewardBonus} к будущим контрактам");
        }

        if (actions.Count == 0)
            actions.Add("Начните с короткого отчета, black box, регистрации судна или первого контракта LuaM");

        return actions
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => Trim(action, MaxLineLength))
            .Distinct()
            .Take(Math.Max(1, maxActions))
            .ToArray();
    }

    public static LuaMSectorQuestTaskUiEntry[] BuildQuestTasks(
        LuaMSectorStatusSnapshot status,
        LuaMSectorStoryRecord? openStory,
        LuaMSectorAutomationUiEntry automation,
        LuaMSectorMapNodeUiEntry[] sectorMapNodes,
        LuaMSectorPreferredProcessUiEntry[] preferredProcesses,
        LuaMSectorInsuranceUiEntry[] insuranceCases,
        LuaMSectorRegistryUiEntry[] registryRecords,
        int maxTasks = 6)
    {
        var tasks = new List<LuaMSectorQuestTaskUiEntry>();

        if (openStory != null)
        {
            var route = ExtractEventRouteLocation(openStory);
            if (string.IsNullOrWhiteSpace(route))
                route = automation.ActiveRouteMarker;
            if (string.IsNullOrWhiteSpace(route))
            {
                var routeNode = sectorMapNodes
                    .Where(node => node.Active || string.Equals(node.StoryId, openStory.Story.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(node => node.SortOrder)
                    .FirstOrDefault();
                route = routeNode.Location;
            }

            AddTask(
                tasks,
                "active-route",
                "Сейчас",
                $"Лети к цели: {openStory.Title}",
                string.IsNullOrWhiteSpace(route)
                    ? "Сначала получи маршрут: в терминале нажми «Пакет маршрута» или спроси у ИИ «маршрут»."
                    : $"Долети до {route}, осмотри маркер LuaM или объект на точке.",
                string.IsNullOrWhiteSpace(route)
                    ? "Маршрут пока не найден"
                    : route,
                "На точке нажми маркер LuaM и выбери «Сдать / закрыть задание». Если есть акт или бумага, нажми «Подать доказательство LuaM».",
                BuildOpenStoryReward(status, openStory),
                priority: 0,
                active: true);

            if (automation.CanPingRoute || automation.RoutePingCount > 0)
            {
                AddTask(
                    tasks,
                    "route-ping",
                    automation.CanPingRoute ? "Можно" : "Ждет",
                    "Уточни маршрут перед вылетом",
                    automation.CanPingRoute
                        ? "Нажми «Пинг маршрута» в терминале. Это подсветит активную точку и улучшит закрытие маршрута."
                        : automation.RoutePingBlockReason,
                    string.IsNullOrWhiteSpace(automation.ActiveRouteMarker)
                        ? "Активный маркер маршрута"
                        : automation.ActiveRouteMarker,
                    "После пинга лети по основной карточке выше; отдельно сдавать этот пункт не нужно.",
                    $"Пинги маршрута: {automation.RoutePingCount}/{LuaMSectorDynamicEventSystem.RouteStabilizationPingThreshold}",
                    priority: 1,
                    active: automation.CanPingRoute);
            }
        }
        else
        {
            AddTask(
                tasks,
                "start-process",
                automation.CanRequestDynamicEvent ? "Старт" : "Ждет",
                "Возьми первое задание LuaM",
                automation.CanRequestDynamicEvent
                    ? "Открой терминал LuaM и нажми «Сгенерировать зацепку». Можно выбрать доступный preferred process."
                    : automation.RequestBlockReason,
                "Терминал LuaM или КПК",
                "После генерации появится новая карточка с маршрутом, маркером и способом сдачи.",
                "Откроет текущую цепочку заданий",
                priority: 0,
                active: automation.CanRequestDynamicEvent);
        }

        var unfiledHazard = status.Hazards
            .Where(hazard => !hazard.Resolved && !hazard.Acknowledged)
            .OrderByDescending(hazard => hazard.Severity)
            .ThenBy(hazard => hazard.Title)
            .FirstOrDefault();
        if (unfiledHazard != null)
        {
            AddTask(
                tasks,
                $"hazard-{unfiledHazard.Story}",
                "Отчет",
                $"Подай доказательство риска: {unfiledHazard.Title}",
                string.IsNullOrWhiteSpace(unfiledHazard.Description)
                    ? "Найди связанный акт, маркер или объект. Если бумаги нет, распечатай отчет в терминале."
                    : unfiledHazard.Description,
                "Акт, маркер, объект на точке или терминал",
                "На бумаге или объекте выбери «Подать доказательство LuaM». Это закрывает риск как evidence.",
                $"Бонус за риск: +{unfiledHazard.RewardBonus}",
                priority: 2,
                active: true);
        }

        var strongestCondition = status.Conditions
            .Where(condition => condition.Active)
            .OrderByDescending(condition => condition.Severity)
            .ThenBy(condition => condition.Title)
            .FirstOrDefault();
        if (strongestCondition != null)
        {
            AddTask(
                tasks,
                $"condition-{strongestCondition.ConditionId}",
                $"SC-{strongestCondition.Severity}",
                $"Учти условие сектора: {strongestCondition.Title}",
                strongestCondition.Severity >= 4
                    ? "Перед вылетом возьми связь, кислород и путь отхода. Не лети один, если маршрут опасный."
                    : "Это фон маршрута: учитывай его при выборе следующего задания и подготовке корабля.",
                "Весь сектор / маршрут",
                "Это предупреждение, а не отдельная сдача. Выполняй основную карточку с учетом этого риска.",
                strongestCondition.Summary,
                priority: 3,
                active: true);
        }

        var rescueFollowUp = FindLatestRescueBlockerFollowUp(status.RecentHistory);
        if (rescueFollowUp != null)
        {
            var blockers = ExtractRescueBlockersSummary(rescueFollowUp.Summary);
            AddTask(
                tasks,
                $"rescue-followup-{rescueFollowUp.Story}",
                "Follow-up",
                "Проверь rescue-коридор после операции Айболита",
                string.IsNullOrWhiteSpace(blockers)
                    ? "После rescue-операции остались помехи. Проверь место, освободи проход и отметь результат через LuaM-отчет."
                    : $"После rescue-операции остались помехи: {blockers}. Освободи проход, доступ или опасную сторону.",
                string.IsNullOrWhiteSpace(rescueFollowUp.Title)
                    ? "Последняя rescue-сцена"
                    : rescueFollowUp.Title,
                "Подай полевой отчет или закрой связанный маршрут через LuaM-терминал после расчистки.",
                "Снижает повторные блоки rescue-группы",
                priority: 4,
                active: true);
        }

        var preferred = preferredProcesses
            .OrderByDescending(process => process.CanRequestNow)
            .ThenByDescending(process => process.Unlocked)
            .ThenBy(process => process.RequiredReputation - process.CurrentReputation)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(preferred.TemplateId))
        {
            AddTask(
                tasks,
                $"preferred-{preferred.TemplateId}",
                preferred.CanRequestNow ? "Доступно" : preferred.Unlocked ? "Ждет" : "Закрыто",
                $"Следующий контракт: {preferred.Title}",
                preferred.CanRequestNow
                    ? "Когда нет активного маршрута, нажми «Запросить этот процесс» в терминале."
                    : preferred.BlockReason,
                string.IsNullOrWhiteSpace(preferred.Vessel) ? "Терминал LuaM" : preferred.Vessel,
                "После запроса появится основная карточка с маршрутом и маркером.",
                $"{preferred.ReputationTarget}: {preferred.CurrentReputation}/{preferred.RequiredReputation}; награда {preferred.BaseReward}+{preferred.ReputationBonus}",
                priority: 5,
                active: preferred.CanRequestNow);
        }

        var pendingInsurance = insuranceCases.FirstOrDefault(claim => !claim.Claimed);
        if (!string.IsNullOrWhiteSpace(pendingInsurance.StoryId))
        {
            AddTask(
                tasks,
                $"insurance-{pendingInsurance.StoryId}",
                "Бумаги",
                $"Страховка: {pendingInsurance.Title}",
                "Распечатай страховой талон по этому делу и подай его как доказательство LuaM.",
                string.IsNullOrWhiteSpace(pendingInsurance.Vessel) ? "Страховой терминал LuaM" : pendingInsurance.Vessel,
                "Нажми «Печать талона» у этого дела, затем на бумаге выбери «Подать доказательство LuaM».",
                $"Запрошено: {pendingInsurance.RequestedAmount}",
                priority: 6,
                active: true);
        }

        var pendingRegistry = registryRecords.FirstOrDefault(record => record.CanRegisterCompany || record.CanRegisterShip);
        if (!string.IsNullOrWhiteSpace(pendingRegistry.StoryId))
        {
            AddTask(
                tasks,
                $"registry-{pendingRegistry.StoryId}",
                "Бумаги",
                $"Регистрация: {pendingRegistry.Title}",
                pendingRegistry.CanRegisterCompany
                    ? "Распечатай чартер компании и подай его как доказательство LuaM."
                    : "Распечатай чартер судна и подай его как доказательство LuaM.",
                string.IsNullOrWhiteSpace(pendingRegistry.Vessel) ? "Реестр LuaM" : pendingRegistry.Vessel,
                "Нажми «Печать чартера» у записи, затем на бумаге выбери «Подать доказательство LuaM».",
                pendingRegistry.ServiceLine,
                priority: 7,
                active: true);
        }

        var lockedLead = status.LockedLeads
            .OrderBy(lead => Math.Max(0, lead.RequiredValue - lead.CurrentValue))
            .ThenBy(lead => lead.Title)
            .FirstOrDefault();
        if (lockedLead != null)
        {
            var remaining = Math.Max(0, lockedLead.RequiredValue - lockedLead.CurrentValue);
            AddTask(
                tasks,
                $"unlock-{lockedLead.Story}",
                remaining == 0 ? "Открыто" : "Прогресс",
                $"Открыть контракт: {lockedLead.Title}",
                remaining == 0
                    ? "Разблокировка готова. Проверь список preferred process в терминале."
                    : $"Нужно еще {remaining} репутации у {lockedLead.RequiredTarget}.",
                lockedLead.RequiredTarget,
                "Закрывай задания сектора для этой службы или фракции.",
                $"{lockedLead.CurrentValue}/{lockedLead.RequiredValue}",
                priority: 8,
                active: remaining == 0);
        }

        return tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Title))
            .GroupBy(task => task.TaskId)
            .Select(group => group.First())
            .OrderBy(task => task.Priority)
            .ThenBy(task => task.Title)
            .Take(Math.Max(1, maxTasks))
            .ToArray();
    }

    private static LuaMSectorHistoryStatus? FindLatestRescueBlockerFollowUp(IReadOnlyList<LuaMSectorHistoryStatus> history)
    {
        return history.FirstOrDefault(entry =>
            string.Equals(entry.Category, "Rescue", StringComparison.OrdinalIgnoreCase) &&
            HasActionableRescueBlockers(entry.Summary));
    }

    private static bool HasActionableRescueBlockers(string summary)
    {
        var blockers = ExtractRescueBlockersSummary(summary);
        if (string.IsNullOrWhiteSpace(blockers) ||
            blockers.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            blockers.Equals("team scene memory unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = blockers.Replace(" ", string.Empty);
        if (!normalized.StartsWith("threat/crowd/route=0/0/0", StringComparison.OrdinalIgnoreCase))
            return true;

        var blockerCount = ExtractSummaryField(blockers, "blockers");
        return !string.IsNullOrWhiteSpace(blockerCount) &&
               !blockerCount.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractRescueBlockersSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;

        var marker = "blockers=";
        var start = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += marker.Length;
        var end = summary.IndexOf("; playerContribution=", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            end = summary.IndexOf("; teamStatus=", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            end = summary.Length;

        return summary[start..end].Trim();
    }

    private static string ExtractSummaryField(string summary, string field)
    {
        if (string.IsNullOrWhiteSpace(summary) ||
            string.IsNullOrWhiteSpace(field))
        {
            return string.Empty;
        }

        var marker = $"{field}=";
        var start = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += marker.Length;
        var end = summary.IndexOf(';', start);
        if (end < 0)
            end = summary.Length;

        return summary[start..end].Trim();
    }

    public static string ExtractEventRouteLocation(LuaMSectorStoryRecord record)
    {
        var route = ExtractMarkerLocation(record.ContractDescription);
        return string.IsNullOrWhiteSpace(route)
            ? ExtractMarkerLocation(record.News)
            : route;
    }

    private static string BuildOpenStoryReward(LuaMSectorStatusSnapshot status, LuaMSectorStoryRecord openStory)
    {
        var matchingHazard = status.Hazards.FirstOrDefault(hazard => hazard.Story.Equals(openStory.Story));
        return matchingHazard != null
            ? $"Бонус за доказательства: +{matchingHazard.RewardBonus}"
            : "Награда зависит от отчета, evidence и репутации";
    }

    private static void AddTask(
        List<LuaMSectorQuestTaskUiEntry> tasks,
        string taskId,
        string status,
        string title,
        string objective,
        string location,
        string turnIn,
        string reward,
        int priority,
        bool active)
    {
        tasks.Add(new LuaMSectorQuestTaskUiEntry
        {
            TaskId = Trim(taskId, 80),
            Status = Trim(status, 32),
            Title = Trim(title, 90),
            Objective = Trim(objective, MaxLineLength),
            Location = Trim(location, 120),
            TurnIn = Trim(turnIn, MaxLineLength),
            Reward = Trim(reward, 120),
            Priority = priority,
            Active = active,
        });
    }

    public static string ExtractMarkerLocation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var marker = "Координаты маркера:";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += marker.Length;
        }
        else
        {
            start = text.IndexOf("GPS", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
        }

        var end = text.Length;
        foreach (var delimiter in new[] { '.', '\n', '\r', ';' })
        {
            var index = text.IndexOf(delimiter, start);
            if (index >= 0)
                end = Math.Min(end, index);
        }

        return text[start..end].Trim().Trim('.');
    }

    private static string Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
