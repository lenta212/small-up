using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Content.Shared._LuaM.AI;
using Robust.Shared.Map;

namespace Content.Server._LuaM.AI;

/// <summary>
/// Pure deterministic decision kernel. Perception and execution stay in
/// adapters, which makes the same arbitration rules usable by bodies, ships,
/// turrets, animals, and service robots.
/// </summary>
public static class LuaMBehaviorArbiter
{
    private const float TierStride = 100_000f;

    public static LuaMBehaviorDecision Decide(
        LuaMBehaviorProfilePrototype profile,
        IReadOnlyList<LuaMBehaviorObservation> observations,
        LuaMBehaviorDecision? previous,
        TimeSpan now)
    {
        var active = observations
            .Where(observation => IsActive(observation, now))
            .ToArray();
        var candidates = new List<Candidate>();

        foreach (var rule in profile.Rules)
            BuildCandidates(profile, rule, active, candidates);

        candidates.Sort(CompareCandidates);
        var best = candidates.Count > 0 ? candidates[0] : DefaultCandidate(profile);
        var current = previous == null
            ? null
            : FindCurrentCandidate(candidates, previous);

        if (previous != null && current != null && !SameIdentity(best, current.Value))
        {
            var currentValue = current.Value with
            {
                Score = current.Value.Score + Math.Max(0f, current.Value.Rule?.Stickiness ?? 0f),
            };

            if (ShouldKeepCurrent(profile, previous, currentValue, best, now))
                best = currentValue;
        }

        if (previous != null && SameIdentity(best, previous))
        {
            return new LuaMBehaviorDecision
            {
                Intent = best.Intent,
                Tier = best.Tier,
                Score = best.Score,
                Target = best.Target,
                Destination = best.Destination,
                RuleId = best.RuleId,
                Activity = best.Activity,
                Reason = BuildReason(best),
                DecidedAt = previous.DecidedAt,
                CommitUntil = previous.CommitUntil,
                Generation = previous.Generation,
            };
        }

        return new LuaMBehaviorDecision
        {
            Intent = best.Intent,
            Tier = best.Tier,
            Score = best.Score,
            Target = best.Target,
            Destination = best.Destination,
            RuleId = best.RuleId,
            Activity = best.Activity,
            Reason = BuildReason(best),
            DecidedAt = now,
            CommitUntil = AddSeconds(now, best.Rule?.CommitSeconds ?? 0f),
            Generation = NextGeneration(previous?.Generation ?? 0u),
        };
    }

    private static void BuildCandidates(
        LuaMBehaviorProfilePrototype profile,
        LuaMBehaviorRule rule,
        IReadOnlyList<LuaMBehaviorObservation> observations,
        List<Candidate> candidates)
    {
        if (rule.Intent == LuaMBehaviorIntent.None ||
            rule.RequiredCapabilities.Any(capability => !profile.HasCapability(capability)) ||
            rule.RequiredAnyCapabilities.Count > 0 &&
            rule.RequiredAnyCapabilities.All(capability => !profile.HasCapability(capability)) ||
            rule.ForbiddenCapabilities.Any(profile.HasCapability) ||
            rule.Forbidden.Any(stimulus => Signal(observations, stimulus) >= rule.MinimumSignal) ||
            rule.RequiredAll.Any(stimulus => Signal(observations, stimulus) < rule.MinimumSignal) ||
            rule.RequiredAny.Count > 0 && rule.RequiredAny.All(stimulus =>
                Signal(observations, stimulus) < rule.MinimumSignal))
        {
            return;
        }

        if (rule.TargetFrom == LuaMBehaviorStimulus.None)
        {
            var anchor = FindStrongestObservation(observations, rule);
            if (rule.RequireTarget && anchor?.Target == null)
                return;

            candidates.Add(BuildCandidate(rule, observations, anchor));
            return;
        }

        var anchors = observations
            .Where(observation => observation.Stimulus == rule.TargetFrom &&
                                  Strength(observation) >= rule.MinimumSignal)
            .GroupBy(observation => new ObservationIdentity(observation.Target, observation.Destination))
            .Select(group => group.OrderByDescending(Strength).First())
            .ToArray();

        if (anchors.Length == 0 && !rule.RequireTarget)
        {
            var fallback = FindStrongestObservation(observations, rule);
            if (fallback != null)
                candidates.Add(BuildCandidate(rule, observations, fallback));
            return;
        }

        foreach (var anchor in anchors)
        {
            if (rule.RequireTarget && anchor.Target == null)
                continue;

            candidates.Add(BuildCandidate(rule, observations, anchor));
        }
    }

    private static Candidate BuildCandidate(
        LuaMBehaviorRule rule,
        IReadOnlyList<LuaMBehaviorObservation> observations,
        LuaMBehaviorObservation? anchor)
    {
        var score = (int) rule.Tier * TierStride + rule.BaseScore;
        var signals = new List<(LuaMBehaviorStimulus Stimulus, float Value)>();

        foreach (var weight in rule.Weights)
        {
            var value = weight.Stimulus == rule.TargetFrom && anchor != null
                ? Strength(anchor)
                : Signal(observations, weight.Stimulus);
            if (value <= 0f)
                continue;

            score += value * weight.Weight;
            signals.Add((weight.Stimulus, value));
        }

        if (!float.IsFinite(score))
            score = (int) rule.Tier * TierStride;

        return new Candidate(
            rule.Intent,
            rule.Tier,
            score,
            anchor?.Target,
            anchor?.Destination,
            rule.Id,
            rule.Activity,
            rule,
            signals);
    }

    private static LuaMBehaviorObservation? FindStrongestObservation(
        IReadOnlyList<LuaMBehaviorObservation> observations,
        LuaMBehaviorRule rule)
    {
        var relevant = rule.RequiredAny
            .Concat(rule.RequiredAll)
            .Concat(rule.Weights.Select(weight => weight.Stimulus))
            .ToHashSet();

        LuaMBehaviorObservation? strongest = null;
        var strength = 0f;
        foreach (var observation in observations)
        {
            if (!relevant.Contains(observation.Stimulus))
                continue;

            var candidateStrength = Strength(observation);
            if (candidateStrength <= strength)
                continue;

            strongest = observation;
            strength = candidateStrength;
        }

        return strongest;
    }

    private static Candidate? FindCurrentCandidate(
        IReadOnlyList<Candidate> candidates,
        LuaMBehaviorDecision previous)
    {
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.RuleId, previous.RuleId, StringComparison.Ordinal))
                continue;

            if (candidate.Target == previous.Target &&
                Nullable.Equals(candidate.Destination, previous.Destination))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool ShouldKeepCurrent(
        LuaMBehaviorProfilePrototype profile,
        LuaMBehaviorDecision previous,
        Candidate current,
        Candidate challenger,
        TimeSpan now)
    {
        if (challenger.Tier > current.Tier)
            return false;

        if (challenger.Tier < current.Tier)
            return true;

        if (now < previous.CommitUntil && current.Rule is { Interruptible: false })
            return true;

        return challenger.Score < current.Score + Math.Max(0f, profile.SameTierPreemptMargin);
    }

    private static int CompareCandidates(Candidate left, Candidate right)
    {
        var tier = right.Tier.CompareTo(left.Tier);
        if (tier != 0)
            return tier;

        var score = right.Score.CompareTo(left.Score);
        if (score != 0)
            return score;

        var rule = string.Compare(left.RuleId, right.RuleId, StringComparison.Ordinal);
        if (rule != 0)
            return rule;

        return CompareTargets(left.Target, right.Target);
    }

    private static int CompareTargets(EntityUid? left, EntityUid? right)
    {
        if (left == right)
            return 0;
        if (left == null)
            return 1;
        if (right == null)
            return -1;
        return left.Value.Id.CompareTo(right.Value.Id);
    }

    private static Candidate DefaultCandidate(LuaMBehaviorProfilePrototype profile)
    {
        return new Candidate(
            profile.DefaultIntent,
            profile.DefaultTier,
            (int) profile.DefaultTier * TierStride,
            null,
            null,
            "default",
            profile.DefaultActivity,
            null,
            Array.Empty<(LuaMBehaviorStimulus, float)>());
    }

    private static bool SameIdentity(Candidate left, Candidate right)
    {
        return left.Intent == right.Intent &&
               string.Equals(left.RuleId, right.RuleId, StringComparison.Ordinal) &&
               left.Target == right.Target &&
               Nullable.Equals(left.Destination, right.Destination);
    }

    private static bool SameIdentity(Candidate candidate, LuaMBehaviorDecision decision)
    {
        return candidate.Intent == decision.Intent &&
               string.Equals(candidate.RuleId, decision.RuleId, StringComparison.Ordinal) &&
               candidate.Target == decision.Target &&
               Nullable.Equals(candidate.Destination, decision.Destination);
    }

    private static string BuildReason(Candidate candidate)
    {
        if (candidate.Signals.Count == 0)
            return candidate.RuleId;

        var signals = string.Join(
            ",",
            candidate.Signals
                .OrderByDescending(signal => signal.Value)
                .ThenBy(signal => signal.Stimulus)
                .Take(4)
                .Select(signal => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{signal.Stimulus}:{signal.Value:0.00}")));
        return $"{candidate.RuleId} [{signals}]";
    }

    private static bool IsActive(LuaMBehaviorObservation observation, TimeSpan now)
    {
        return observation.Stimulus != LuaMBehaviorStimulus.None &&
               float.IsFinite(observation.Severity) &&
               float.IsFinite(observation.Confidence) &&
               Strength(observation) > 0f &&
               (observation.ExpiresAt == TimeSpan.Zero || observation.ExpiresAt > now);
    }

    private static float Signal(
        IReadOnlyList<LuaMBehaviorObservation> observations,
        LuaMBehaviorStimulus stimulus)
    {
        var best = 0f;
        foreach (var observation in observations)
        {
            if (observation.Stimulus != stimulus)
                continue;
            best = Math.Max(best, Strength(observation));
        }

        return best;
    }

    private static float Strength(LuaMBehaviorObservation observation)
    {
        return Math.Clamp(observation.Severity, 0f, 1f) *
               Math.Clamp(observation.Confidence, 0f, 1f);
    }

    private static TimeSpan AddSeconds(TimeSpan now, float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f)
            return now;

        var ticks = Math.Min(
            TimeSpan.MaxValue.Ticks - Math.Max(0L, now.Ticks),
            TimeSpan.FromSeconds(seconds).Ticks);
        return TimeSpan.FromTicks(Math.Max(0L, now.Ticks) + ticks);
    }

    private static uint NextGeneration(uint generation)
    {
        var next = unchecked(generation + 1);
        return next == 0 ? 1u : next;
    }

    private readonly record struct ObservationIdentity(
        EntityUid? Target,
        EntityCoordinates? Destination);

    private readonly record struct Candidate(
        LuaMBehaviorIntent Intent,
        LuaMBehaviorTier Tier,
        float Score,
        EntityUid? Target,
        EntityCoordinates? Destination,
        string RuleId,
        string Activity,
        LuaMBehaviorRule? Rule,
        IReadOnlyList<(LuaMBehaviorStimulus Stimulus, float Value)> Signals);
}
