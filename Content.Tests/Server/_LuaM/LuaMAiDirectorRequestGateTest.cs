#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._LuaM.Sector;
using NUnit.Framework;

namespace Content.Tests.Server._LuaM;

[TestFixture]
public sealed class LuaMAiDirectorRequestGateTest
{
    [Test]
    public void LeaseBlocksOtherCallersAndCanBeReacquiredAfterDispose()
    {
        var gate = new LuaMAiDirectorRequestGate();

        Assert.That(gate.TryAcquire(out var first), Is.True);
        Assert.That(first, Is.Not.Null);
        Assert.That(gate.IsActive, Is.True);
        Assert.That(gate.TryAcquire(out var blocked), Is.False);
        Assert.That(blocked, Is.Null);

        first!.Dispose();

        Assert.That(gate.IsActive, Is.False);
        Assert.That(gate.TryAcquire(out var second), Is.True);
        second!.Dispose();
        Assert.That(gate.IsActive, Is.False);
    }

    [Test]
    public void LeaseDisposeIsIdempotentAndCannotReleaseANewerLease()
    {
        var gate = new LuaMAiDirectorRequestGate();
        Assert.That(gate.TryAcquire(out var first), Is.True);

        first!.Dispose();
        Assert.That(gate.TryAcquire(out var second), Is.True);

        first.Dispose();
        Assert.That(gate.IsActive, Is.True);
        Assert.That(gate.TryAcquire(out _), Is.False);

        second!.Dispose();
        Assert.That(gate.IsActive, Is.False);
    }

    [Test]
    public void ConcurrentCallersProduceExactlyOneLease()
    {
        const int callerCount = 32;
        var gate = new LuaMAiDirectorRequestGate();
        using var start = new ManualResetEventSlim();
        using var releaseWinner = new ManualResetEventSlim();
        using var attempted = new CountdownEvent(callerCount);
        var acquired = 0;

        var tasks = new Task[callerCount];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                start.Wait();
                var won = gate.TryAcquire(out var lease);
                if (won)
                    Interlocked.Increment(ref acquired);

                attempted.Signal();
                if (!won)
                    return;

                releaseWinner.Wait();
                lease!.Dispose();
            });
        }

        start.Set();
        Assert.That(attempted.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(Volatile.Read(ref acquired), Is.EqualTo(1));
        Assert.That(gate.IsActive, Is.True);

        releaseWinner.Set();
        Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(gate.IsActive, Is.False);
    }
}
