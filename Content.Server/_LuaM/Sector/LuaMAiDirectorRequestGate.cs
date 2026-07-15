using System;
using System.Threading;

namespace Content.Server._LuaM.Sector;

/// <summary>
/// Serializes access to the AI gateway without a check-then-set race.
/// A lease can be handed to a fire-and-forget request and releases the gate exactly once.
/// </summary>
internal sealed class LuaMAiDirectorRequestGate
{
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public bool TryAcquire(out Lease? lease)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            lease = null;
            return false;
        }

        lease = new Lease(this);
        return true;
    }

    private void Release()
    {
        Volatile.Write(ref _active, 0);
    }

    internal sealed class Lease : IDisposable
    {
        private LuaMAiDirectorRequestGate? _owner;

        internal Lease(LuaMAiDirectorRequestGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
