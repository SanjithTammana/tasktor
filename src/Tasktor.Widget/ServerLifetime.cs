namespace Tasktor.Widget;

/// <summary>
/// The COM server's lifetime, ended by COM references rather than by widget activity: an idle board can still hold the
/// provider, and an empty widget list is not a released provider. Pins are the per-process count COM keeps
/// (<c>CoAddRefServerProcess</c> / <c>CoReleaseServerProcess</c>): one per strong external connection to a provider
/// (<see cref="ProviderConnections"/>), one per <c>LockServer(true)</c>, and a creation pin from <c>CreateInstance</c>
/// until that provider's first strong connection. The unpin that takes the count to zero stops the server. COM has
/// suspended the class objects by then, so a new activation starts a fresh server instead of racing into this one, and
/// a <c>CreateInstance</c> already inside the factory is refused (<c>CO_E_SERVER_STOPPING</c>; COM retries it). Every
/// failure path errs toward staying alive: a pin that is never released keeps the server running, the old behaviour.
/// </summary>
public sealed class ServerLifetime(Func<uint> addRef, Func<uint> release)
{
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private bool _stopping;

    /// <summary>Set once the last pin is released; the server then revokes its class object and exits.</summary>
    public WaitHandle Stopped => _stopped.WaitHandle;

    public bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    /// <summary>Take a pin; false once stopping (the caller refuses the work so COM retries elsewhere).</summary>
    public bool TryPin()
    {
        lock (_gate)
        {
            if (_stopping) return false;
            addRef();
            return true;
        }
    }

    /// <summary>Release a pin taken by <see cref="TryPin"/>; the release to zero stops the server.</summary>
    public void Unpin()
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (release() != 0) return;
            _stopping = true;
            _stopped.Set();
        }
    }
}

/// <summary>
/// One provider's strong external connections, reported by COM's stub manager through <c>IExternalConnection</c>.
/// The provider is created holding the factory's creation pin; its first strong connection takes that pin over, every
/// later one takes its own, and each release gives one back. Weak connections never pin.
/// </summary>
public sealed class ProviderConnections(ServerLifetime lifetime)
{
    public const uint Strong = 1; // EXTCONN_STRONG

    private readonly Lock _gate = new();
    private bool _creationPin = true;
    private uint _strong;

    public uint Count
    {
        get { lock (_gate) return _strong; }
    }

    public uint AddConnection(uint kind)
    {
        if (kind != Strong) return 0;
        lock (_gate)
        {
            if (_creationPin)
            {
                _creationPin = false; // the creation pin becomes this connection's pin
            }
            else if (!lifetime.TryPin())
            {
                return _strong; // stopping: this connection is not counted, so its release unpins nothing
            }

            return ++_strong;
        }
    }

    public uint ReleaseConnection(uint kind)
    {
        if (kind != Strong) return 0;
        lock (_gate)
        {
            if (_strong == 0) return 0;
            _strong--;
            lifetime.Unpin();
            return _strong;
        }
    }

    /// <summary>The created provider was never handed out (the factory's QueryInterface failed): give the creation pin back.</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            if (!_creationPin) return;
            _creationPin = false;
            lifetime.Unpin();
        }
    }
}
