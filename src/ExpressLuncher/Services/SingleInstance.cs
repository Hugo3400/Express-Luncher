using System.Runtime.InteropServices;

namespace ExpressLuncher.Services;

/// <summary>
/// Instance unique : la première instance détient un mutex nommé et écoute un événement nommé ;
/// une seconde instance le signale puis se ferme.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ExpressLuncher.SingleInstance";
    private const string EventName = @"Local\ExpressLuncher.Activate";
    private const int AsfwAny = -1;

    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _registration;

    /// <summary>Vrai si cette instance est la première.</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return true;
    }

    /// <summary>Appelle <paramref name="onActivate"/> (sur un thread du pool) à chaque nouveau lancement.</summary>
    public void Listen(Action onActivate)
    {
        if (_activate is null) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Demande à l'instance existante de passer au premier plan.</summary>
    public static void SignalExisting()
    {
        try
        {
            // Sans cela, Windows refuserait à l'autre processus de prendre le premier plan.
            AllowSetForegroundWindow(AsfwAny);
            using var handle = EventWaitHandle.OpenExisting(EventName);
            handle.Set();
        }
        catch
        {
            // l'autre instance est en train de démarrer ou de se fermer : rien à faire
        }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* pas le thread propriétaire */ }
            _mutex.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
