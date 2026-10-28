using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RemoteInspector.Core;

/// <summary>Independent TCP listener. Every connection has separate requests and caches.</summary>
public sealed class InspectorHost : IAsyncDisposable
{
    private TcpListener listener;
    private CancellationTokenSource lifetime;
    private Task acceptTask;
    private Task stopTask;
    private readonly object lifecycleLock = new();
    private readonly ConcurrentDictionary<Guid, InspectorSession> sessions = new();
    public InspectorSession[] Sessions => sessions.Values.ToArray();
    public bool IsRunning { get { lock (lifecycleLock) return lifetime != null && !lifetime.IsCancellationRequested; } }
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public event Action<InspectorSession> SessionAdded;
    public event Action<string> Diagnostic;

    public void Start(int port = 9555, IPAddress address = null)
    {
        lock (lifecycleLock)
        {
            if (lifetime != null) throw new InvalidOperationException("Listener is already running or stopping.");
            var next = new TcpListener(address ?? IPAddress.Any, port);
            next.Start();
            listener = next;
            lifetime = new CancellationTokenSource();
            stopTask = null;
            acceptTask = AcceptAsync(next, lifetime.Token);
        }
    }

    private async Task AcceptAsync(TcpListener current, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await current.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;
                var session = new InspectorSession(client);
                sessions[session.Id] = session;
                session.Closed += s => sessions.TryRemove(s.Id, out _);
                session.Diagnostic += message => Diagnostic?.Invoke(message);
                SessionAdded?.Invoke(session);
                session.Start();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Diagnostic?.Invoke("Listener: " + ex.Message); }
    }

    public Task StopAsync()
    {
        lock (lifecycleLock)
        {
            if (lifetime == null) return stopTask ?? Task.CompletedTask;
            if (stopTask != null) return stopTask;
            // Stop button and window shutdown can overlap. Both await this same cleanup.
            var currentListener = listener;
            var currentLifetime = lifetime;
            var currentAccept = acceptTask;
            return stopTask = Task.Run(() => StopCoreAsync(currentListener, currentLifetime, currentAccept));
        }
    }

    private async Task StopCoreAsync(TcpListener currentListener, CancellationTokenSource currentLifetime, Task currentAccept)
    {
        try
        {
            currentLifetime.Cancel();
            currentListener.Stop();
            await currentAccept.ConfigureAwait(false);
            await Task.WhenAll(Sessions.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        finally
        {
            currentLifetime.Dispose();
            lock (lifecycleLock) { lifetime = null; listener = null; }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
