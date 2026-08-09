using System.Threading;

// One pad, one window. A gamepad is OS-global — every window on the machine reads every pad — and
// the network protocol cannot arbitrate that: device ids are machine-local (two machines both have
// a "pad0") and a host has no idea which windows sit on the same machine. The exclusion therefore
// lives in an OS named mutex keyed by device index. The window that holds a seat with a pad owns
// the mutex; any other window trying to claim the same pad gets a "手柄已被占用" popup instead of
// being allowed to fight over the device.
//
// No shared-memory file is involved — the kernel keeps the handle. Named mutexes are cross-process
// on Windows (this game's target), and they survive a crashed owner: an abandoned mutex is handed
// to the next acquirer rather than staying locked forever.
public static class PadLock
{
    private const string NamePrefix = "Local\\MouseKombat.Pad.";

    // Returns a mutex THIS process now holds, or null when another window already owns the pad.
    public static Mutex TryAcquire(int device)
    {
        try
        {
            var m = new Mutex(false, NamePrefix + device);
            return m.WaitOne(0) ? m : null;   // 0 ms: probe, never block
        }
        catch (AbandonedMutexException e)
        {
            // The previous owner crashed mid-hold; the kernel transfers ownership to us.
            if (e.Mutex != null) return e.Mutex;
            var m = new Mutex(false, NamePrefix + device);
            try { return m.WaitOne(0) ? m : null; }
            catch (AbandonedMutexException) { return m; }   // handed to us by the kernel
        }
    }

    public static void Release(ref Mutex m)
    {
        if (m == null) return;
        try { m.ReleaseMutex(); } catch { }   // not ours anymore: the lock is already gone
        m.Dispose();
        m = null;
    }
}
