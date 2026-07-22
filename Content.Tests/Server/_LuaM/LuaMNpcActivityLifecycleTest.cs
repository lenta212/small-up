using System;
using Content.Server._LuaM.NPC;
using Content.Shared._LuaM.NPC;
using NUnit.Framework;

namespace Content.Tests.Server._LuaM;

[TestFixture]
public sealed class LuaMNpcActivityLifecycleTest
{
    private const string Standby = "standby";
    private const string Work = "work";
    private const string Handoff = "handoff";

    private readonly LuaMNpcActivityLifecycleSystem _lifecycle = new();
    private readonly TestPolicy _policy = new();

    [Test]
    public void RepeatingIntentIsIdempotentAndTransitionsArePolicyOwned()
    {
        var state = new LuaMNpcActivityState();
        var startedAt = TimeSpan.FromSeconds(10);

        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Standby,
                null,
                null,
                startedAt,
                out var rejection),
            Is.True);
        Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.None));
        Assert.That(state.Generation, Is.EqualTo(1));
        Assert.That(state.Deadline, Is.EqualTo(TimeSpan.FromSeconds(15)));

        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Standby,
                null,
                null,
                startedAt + TimeSpan.FromSeconds(1),
                out rejection),
            Is.True);
        Assert.That(state.Generation, Is.EqualTo(1), "the executor must not churn an identical intent");
        Assert.That(state.StartedAt, Is.EqualTo(startedAt));

        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Handoff,
                null,
                null,
                startedAt + TimeSpan.FromSeconds(2),
                out rejection),
            Is.False);
        Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.InvalidTransition));
        Assert.That(state.Activity, Is.EqualTo(Standby));

        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Handoff,
                new Robust.Shared.GameObjects.EntityUid(42),
                null,
                startedAt + TimeSpan.FromSeconds(2),
                out rejection),
            Is.False,
            "changing the target must not bypass the role transition policy");
        Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.InvalidTransition));
        Assert.That(state.Activity, Is.EqualTo(Standby));

        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Work,
                null,
                null,
                startedAt + TimeSpan.FromSeconds(3),
                out rejection),
            Is.True);
        Assert.That(state.Generation, Is.EqualTo(2));
    }

    [Test]
    public void StaleExecutorCannotCompleteReplacementIntent()
    {
        var state = new LuaMNpcActivityState();
        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Standby,
                null,
                null,
                TimeSpan.Zero,
                out _),
            Is.True);
        var staleGeneration = state.Generation;

        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Work,
                null,
                null,
                TimeSpan.FromSeconds(1),
                out _),
            Is.True);

        Assert.That(
            _lifecycle.Complete(
                state,
                staleGeneration,
                TimeSpan.FromSeconds(2),
                out var rejection),
            Is.False);
        Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.StaleGeneration));
        Assert.That(state.Activity, Is.EqualTo(Work));
        Assert.That(state.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Active));
    }

    [Test]
    public void BlockedRetryUsesExponentialBackoffAndStopsAtAttemptLimit()
    {
        var state = new LuaMNpcActivityState();
        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Work,
                null,
                null,
                TimeSpan.Zero,
                out _),
            Is.True);

        Assert.That(
            _lifecycle.Block(
                state,
                _policy,
                state.Generation,
                "route-blocked",
                Standby,
                LuaMNpcActivityRouteStatus.Blocked,
                TimeSpan.Zero,
                out _),
            Is.True);
        Assert.That(state.RetryNotBefore, Is.EqualTo(TimeSpan.FromSeconds(1)));

        Assert.That(
            _lifecycle.CanAttempt(
                state,
                _policy,
                state.Generation,
                TimeSpan.FromMilliseconds(500),
                out var canAttemptRetryAfter,
                out var canAttemptRejection),
            Is.False);
        Assert.That(canAttemptRejection, Is.EqualTo(LuaMNpcActivityRejection.RetryBackoffActive));
        Assert.That(canAttemptRetryAfter, Is.EqualTo(TimeSpan.FromMilliseconds(500)));

        Assert.That(
            _lifecycle.CanAttempt(
                state,
                _policy,
                state.Generation,
                TimeSpan.FromSeconds(1),
                out canAttemptRetryAfter,
                out canAttemptRejection),
            Is.True);
        Assert.That(canAttemptRejection, Is.EqualTo(LuaMNpcActivityRejection.None));
        Assert.That(canAttemptRetryAfter, Is.EqualTo(TimeSpan.Zero));

        Assert.That(
            _lifecycle.TryRecoverBlockedIntent(
                state,
                _policy,
                state.Generation,
                Work,
                null,
                null,
                TimeSpan.FromMilliseconds(500),
                out var retryAfter,
                out var rejection),
            Is.False);
        Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.RetryBackoffActive));
        Assert.That(retryAfter, Is.EqualTo(TimeSpan.FromMilliseconds(500)));

        Assert.That(
            _lifecycle.TryRecoverBlockedIntent(
                state,
                _policy,
                state.Generation,
                Work,
                null,
                null,
                TimeSpan.FromSeconds(1),
                out _,
                out _),
            Is.True);
        Assert.That(state.Generation, Is.EqualTo(2));
        Assert.That(state.Attempts, Is.EqualTo(1));

        Assert.That(
            _lifecycle.Block(
                state,
                _policy,
                state.Generation,
                "route-blocked",
                Standby,
                LuaMNpcActivityRouteStatus.Blocked,
                TimeSpan.FromSeconds(1),
                out _),
            Is.True);
        Assert.That(state.RetryNotBefore, Is.EqualTo(TimeSpan.FromSeconds(3)), "second failure uses 2x backoff");

        var secondBlockedGeneration = state.Generation;
        Assert.That(
            _lifecycle.TryRecoverBlockedIntent(
                state,
                _policy,
                secondBlockedGeneration,
                Work,
                null,
                null,
                TimeSpan.FromSeconds(3),
                out _,
                out _),
            Is.True);
        Assert.That(state.Attempts, Is.EqualTo(2));

        Assert.That(
            _lifecycle.Block(
                state,
                _policy,
                state.Generation,
                "route-blocked",
                Standby,
                LuaMNpcActivityRouteStatus.Blocked,
                TimeSpan.FromSeconds(3),
                out _),
            Is.True);
        Assert.That(state.RetryNotBefore, Is.EqualTo(TimeSpan.FromSeconds(7)), "third failure uses capped 4x backoff");

        var terminalGeneration = state.Generation;
        Assert.That(
            _lifecycle.CanAttempt(
                state,
                _policy,
                terminalGeneration,
                TimeSpan.FromSeconds(7),
                out canAttemptRetryAfter,
                out canAttemptRejection),
            Is.False);
        Assert.That(canAttemptRejection, Is.EqualTo(LuaMNpcActivityRejection.AttemptLimitReached));
        Assert.That(canAttemptRetryAfter, Is.EqualTo(TimeSpan.Zero));

        Assert.That(
            _lifecycle.TryRecoverBlockedIntent(
                state,
                _policy,
                terminalGeneration,
                Work,
                null,
                null,
                TimeSpan.FromSeconds(7),
                out retryAfter,
                out rejection),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.AttemptLimitReached));
            Assert.That(retryAfter, Is.EqualTo(TimeSpan.Zero));
            Assert.That(state.Attempts, Is.EqualTo(_policy.MaxAttempts));
            Assert.That(state.Generation, Is.EqualTo(terminalGeneration));
            Assert.That(state.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Blocked));
        });
    }

    [Test]
    public void StaleGenerationCannotRecoverBlockedReplacement()
    {
        var state = new LuaMNpcActivityState();
        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                state,
                _policy,
                Work,
                null,
                null,
                TimeSpan.Zero,
                out _),
            Is.True);
        Assert.That(
            _lifecycle.Block(
                state,
                _policy,
                state.Generation,
                "route-blocked",
                Standby,
                LuaMNpcActivityRouteStatus.Blocked,
                TimeSpan.Zero,
                out _),
            Is.True);

        var blockedGeneration = state.Generation;
        var retryNotBefore = state.RetryNotBefore;
        Assert.That(
            _lifecycle.TryRecoverBlockedIntent(
                state,
                _policy,
                blockedGeneration + 1,
                Work,
                null,
                null,
                retryNotBefore,
                out var retryAfter,
                out var rejection),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.StaleGeneration));
            Assert.That(retryAfter, Is.EqualTo(TimeSpan.Zero));
            Assert.That(state.Generation, Is.EqualTo(blockedGeneration));
            Assert.That(state.Attempts, Is.Zero);
            Assert.That(state.RetryNotBefore, Is.EqualTo(retryNotBefore));
            Assert.That(state.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Blocked));
            Assert.That(state.Failure, Is.EqualTo("route-blocked"));
        });
    }

    [Test]
    public void DeadlineAndRecordedAttemptBudgetHaveBoundedTerminalOutcomes()
    {
        var attemptState = new LuaMNpcActivityState();
        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                attemptState,
                _policy,
                Work,
                null,
                null,
                TimeSpan.Zero,
                out _),
            Is.True);

        LuaMNpcActivityRejection rejection = default;
        for (var attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
        {
            Assert.That(
                _lifecycle.RecordAttempt(
                    attemptState,
                    _policy,
                    attemptState.Generation,
                    TimeSpan.FromSeconds(attempt),
                    "attempt-limit",
                    out rejection),
                Is.EqualTo(attempt < _policy.MaxAttempts));
            Assert.That(attemptState.Attempts, Is.EqualTo(attempt));
        }

        Assert.That(rejection, Is.EqualTo(LuaMNpcActivityRejection.AttemptLimitReached));
        Assert.That(attemptState.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Failed));
        Assert.That(attemptState.Failure, Is.EqualTo("attempt-limit"));

        var deadlineState = new LuaMNpcActivityState();
        Assert.That(
            _lifecycle.BeginOrReplaceIntent(
                deadlineState,
                _policy,
                Work,
                null,
                null,
                TimeSpan.Zero,
                out _),
            Is.True);
        Assert.That(
            _lifecycle.ExpireIfDue(
                deadlineState,
                _policy,
                deadlineState.Deadline,
                "deadline"),
            Is.True);
        Assert.That(deadlineState.TerminalStatus, Is.EqualTo(LuaMNpcActivityTerminalStatus.Failed));
        Assert.That(deadlineState.Fallback, Is.EqualTo(Standby));
    }

    private sealed class TestPolicy : ILuaMNpcRoleActivityPolicy
    {
        public string Role => "test-role";
        public string NoneActivity => LuaMNpcActivityIds.None;
        public string DefaultFallbackActivity => Standby;
        public int MaxAttempts => 3;
        public TimeSpan BaseRetryBackoff => TimeSpan.FromSeconds(1);
        public TimeSpan MaxRetryBackoff => TimeSpan.FromSeconds(4);
        public float ProgressTolerance => 0.25f;

        public bool Allows(string activity)
        {
            return activity is Standby or Work or Handoff;
        }

        public bool TryGetActivityPolicy(string activity, out LuaMNpcActivityPolicyEntry policy)
        {
            if (!Allows(activity))
            {
                policy = default;
                return false;
            }

            policy = new LuaMNpcActivityPolicyEntry(
                activity,
                TimeSpan.FromSeconds(5),
                Standby);
            return true;
        }

        public bool IsTransitionAllowed(string from, string to)
        {
            return from == to ||
                   from == LuaMNpcActivityIds.None ||
                   from == Standby && to == Work ||
                   to == Standby;
        }
    }
}
