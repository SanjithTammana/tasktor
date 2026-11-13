using Tasktor.Widget;

namespace Tasktor.Widget.Tests;

/// <summary>The COM server's reference lifetime, against a counter that behaves like CoAddRefServerProcess / CoReleaseServerProcess.</summary>
public sealed class ServerLifetimeTests
{
    private sealed class ProcessCount
    {
        public uint Value;
        public uint AddRef() => Interlocked.Increment(ref Value);
        public uint Release() => Interlocked.Decrement(ref Value);
    }

    private static (ServerLifetime Lifetime, ProcessCount Count) Create()
    {
        var count = new ProcessCount();
        return (new ServerLifetime(count.AddRef, count.Release), count);
    }

    /// <summary>The factory's CreateInstance: a creation pin, then the provider's connections.</summary>
    private static ProviderConnections Created(ServerLifetime lifetime)
    {
        Assert.True(lifetime.TryPin());
        return new ProviderConnections(lifetime);
    }

    [Fact]
    public void A_started_server_with_no_client_keeps_running()
    {
        var (lifetime, _) = Create();
        Assert.False(lifetime.IsStopping);
        Assert.False(lifetime.Stopped.WaitOne(0));
    }

    [Fact]
    public void The_last_strong_release_stops_the_server_and_not_before()
    {
        var (lifetime, count) = Create();
        var provider = Created(lifetime);
        provider.AddConnection(ProviderConnections.Strong); // the host's proxy takes over the creation pin
        Assert.Equal(1u, count.Value);
        provider.AddConnection(ProviderConnections.Strong); // a second marshal of the same provider
        Assert.Equal(2u, count.Value);

        provider.ReleaseConnection(ProviderConnections.Strong);
        Assert.False(lifetime.IsStopping);
        provider.ReleaseConnection(ProviderConnections.Strong);
        Assert.True(lifetime.IsStopping);
        Assert.True(lifetime.Stopped.WaitOne(0));
        Assert.Equal(0u, count.Value);
    }

    [Fact]
    public void An_idle_board_that_still_holds_the_provider_keeps_the_server()
    {
        // Widget activity is not a reference: no Activate/Deactivate is involved, only connections.
        var (lifetime, _) = Create();
        var provider = Created(lifetime);
        provider.AddConnection(ProviderConnections.Strong);
        Assert.False(lifetime.IsStopping);
    }

    [Fact]
    public void Weak_connections_neither_pin_nor_release()
    {
        var (lifetime, count) = Create();
        var provider = Created(lifetime);
        provider.AddConnection(ProviderConnections.Strong);
        provider.AddConnection(2); // EXTCONN_WEAK
        provider.ReleaseConnection(2);
        Assert.Equal(1u, count.Value);
        Assert.False(lifetime.IsStopping);
    }

    [Fact]
    public void A_created_provider_that_is_never_connected_holds_the_server_until_abandoned()
    {
        var (lifetime, count) = Create();
        var provider = Created(lifetime);
        Assert.Equal(1u, count.Value);
        Assert.False(lifetime.IsStopping); // between CreateInstance and the marshal there is no window to exit
        provider.Abandon();
        Assert.True(lifetime.IsStopping);
        provider.Abandon(); // idempotent
        Assert.Equal(0u, count.Value);
    }

    [Fact]
    public void A_new_creation_during_another_providers_last_release_keeps_the_server()
    {
        var (lifetime, _) = Create();
        var first = Created(lifetime);
        first.AddConnection(ProviderConnections.Strong);
        var second = Created(lifetime); // CreateInstance has returned; its marshal has not arrived yet
        first.ReleaseConnection(ProviderConnections.Strong);
        Assert.False(lifetime.IsStopping);
        second.AddConnection(ProviderConnections.Strong);
        second.ReleaseConnection(ProviderConnections.Strong);
        Assert.True(lifetime.IsStopping);
    }

    [Fact]
    public void Once_stopping_new_work_is_refused_and_releases_do_not_underflow()
    {
        var (lifetime, count) = Create();
        var provider = Created(lifetime);
        provider.AddConnection(ProviderConnections.Strong);
        provider.ReleaseConnection(ProviderConnections.Strong);
        Assert.True(lifetime.IsStopping);

        Assert.False(lifetime.TryPin()); // CreateInstance / LockServer answer CO_E_SERVER_STOPPING
        provider.AddConnection(ProviderConnections.Strong); // not counted while stopping
        provider.ReleaseConnection(ProviderConnections.Strong);
        provider.ReleaseConnection(ProviderConnections.Strong);
        lifetime.Unpin();
        Assert.Equal(0u, count.Value);
    }

    [Fact]
    public void LockServer_pins_hold_the_server_like_connections()
    {
        var (lifetime, _) = Create();
        var provider = Created(lifetime);
        provider.AddConnection(ProviderConnections.Strong);
        Assert.True(lifetime.TryPin()); // LockServer(true)
        provider.ReleaseConnection(ProviderConnections.Strong);
        Assert.False(lifetime.IsStopping);
        lifetime.Unpin(); // LockServer(false)
        Assert.True(lifetime.IsStopping);
    }

    [Fact]
    public async Task Concurrent_connections_and_releases_stop_exactly_once_at_zero()
    {
        var (lifetime, count) = Create();
        var providers = Enumerable.Range(0, 32).Select(_ => Created(lifetime)).ToArray();
        foreach (var p in providers) p.AddConnection(ProviderConnections.Strong);
        await Task.WhenAll(providers.Select(p => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                p.AddConnection(ProviderConnections.Strong);
                p.ReleaseConnection(ProviderConnections.Strong);
            }
        })));
        Assert.False(lifetime.IsStopping);
        await Task.WhenAll(providers.Select(p => Task.Run(() => p.ReleaseConnection(ProviderConnections.Strong))));
        Assert.True(lifetime.IsStopping);
        Assert.Equal(0u, count.Value);
    }
}
