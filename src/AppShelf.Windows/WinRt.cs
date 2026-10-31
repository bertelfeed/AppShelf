using System.Runtime.InteropServices;

namespace AppShelf.Windows;

/// <summary>
/// Plain WinRT calls through the ABI, so the app needs no Windows SDK projection. Slots and IIDs come from the SDK's
/// winrt headers (e.g. Include\10.0.26100.0\winrt\windows.services.store.h). Each call runs on its own MTA thread and
/// never waits on the UI thread.
/// </summary>
internal static unsafe class WinRt
{
    private static readonly Guid AsyncInfo = new("00000036-0000-0000-c000-000000000046");

    /// <summary>Waits for an IAsyncAction or IAsyncOperation; its result is then read with <see cref="Out(IntPtr, int)"/> at slot 8.</summary>
    public static void Wait(IntPtr operation, TimeSpan timeout)
    {
        var iid = AsyncInfo;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(operation, in iid, out var info));
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                switch (OutInt(info, 7))
                {
                    case 1: return;
                    case 2: throw new OperationCanceledException();
                    case 3: Marshal.ThrowExceptionForHR(OutInt(info, 8)); return;
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                Thread.Sleep(20);
            }
        }
        finally { Marshal.Release(info); }
    }

    public static T OnWorker<T>(Func<T> action)
    {
        T result = default!; Exception? error = null;
        var thread = new Thread(() =>
        {
            var initialized = RoInitialize(1) >= 0; // RO_INIT_MULTITHREADED
            try { result = action(); }
            catch (Exception ex) { error = ex; }
            finally { if (initialized) RoUninitialize(); }
        }) { IsBackground = true };
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result;
    }

    public static IntPtr Factory(string className, Guid iid)
    {
        var name = HString(className);
        try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out var factory)); return factory; }
        finally { WindowsDeleteString(name); }
    }
    public static IntPtr HString(string value)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(value, value.Length, out var result));
        return result;
    }
    public static void DeleteHString(IntPtr value) => WindowsDeleteString(value);

    // Vtable slots count IUnknown (0-2) and IInspectable (3-5).
    private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];
    public static void Call(IntPtr instance, int slot) =>
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(instance, slot))(instance));
    public static IntPtr Out(IntPtr instance, int slot)
    {
        IntPtr result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }
    public static IntPtr Out(IntPtr instance, int slot, IntPtr argument)
    {
        IntPtr result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(instance, slot))(instance, argument, &result));
        return result;
    }
    public static int OutInt(IntPtr instance, int slot)
    {
        int result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    [DllImport("combase.dll")] private static extern int RoInitialize(int type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
}
