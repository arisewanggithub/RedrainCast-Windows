namespace RedrainCast.Core;

public enum CastRole { Idle, Sending, Receiving }

public sealed class SessionRoles
{
    private readonly object gate = new();
    private Lease? active;

    public CastRole Role { get { lock (gate) return active?.Role ?? CastRole.Idle; } }

    public IDisposable? TryAcquire(CastRole role)
    {
        if (role is not (CastRole.Sending or CastRole.Receiving))
            throw new ArgumentOutOfRangeException(nameof(role));
        lock (gate)
        {
            if (active is not null) return null;
            active = new Lease(this, role);
            return active;
        }
    }

    private sealed class Lease(SessionRoles owner, CastRole role) : IDisposable
    {
        public CastRole Role { get; } = role;

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (ReferenceEquals(owner.active, this)) owner.active = null;
            }
        }
    }
}
