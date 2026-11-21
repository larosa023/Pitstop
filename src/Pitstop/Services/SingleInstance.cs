namespace Pitstop.Services;

/// <summary>
/// One Pitstop at a time. A second launch brings the first one forward; "Pitstop.exe --quit" (used by
/// Hangar) asks the running copy to close.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Local\\Pitstop.Instance";
    private const string ShowName = "Local\\Pitstop.Show";
    private const string QuitName = "Local\\Pitstop.Quit";
    private static Mutex? _mutex;

    public static bool TryAcquire(TimeSpan wait)
    {
        _mutex = new Mutex(false, MutexName);
        try
        {
            if (_mutex.WaitOne(wait)) return true;
        }
        catch (AbandonedMutexException)
        {
            return true;   // previous copy crashed; it's ours now
        }
        Signal(ShowName);
        return false;
    }

    /// <summary>For the copy started by an update: take the lock once the old copy exits.</summary>
    public static void AcquireLater() => Task.Run(() => TryAcquireQuietly(TimeSpan.FromMinutes(2)));

    private static void TryAcquireQuietly(TimeSpan wait)
    {
        _mutex ??= new Mutex(false, MutexName);
        try { _mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { }
        catch { }
    }

    public static bool SignalQuit()
    {
        bool running = Mutex.TryOpenExisting(MutexName, out var m);
        m?.Dispose();
        if (running) Signal(QuitName);
        return running;
    }

    private static void Signal(string name)
    {
        try
        {
            using var e = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            e.Set();
        }
        catch { }
    }

    public static void Listen(Action onShow, Action onQuit)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        var t = new Thread(() =>
        {
            var handles = new WaitHandle[] { show, quit };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                if (i == 0) onShow();
                else if (i == 1) onQuit();
            }
        })
        { IsBackground = true, Name = "Pitstop single instance" };
        t.Start();
    }
}
