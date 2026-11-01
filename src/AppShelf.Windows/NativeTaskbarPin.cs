using System.Runtime.InteropServices;

namespace AppShelf.Windows;

// Windows Runtime ABI, no additional runtime or third-party UI package required.
// Used in a dedicated process whose explicit AppID is the folder's AppID.
public sealed class NativeTaskbarPin : IDisposable
{
    private IntPtr manager;
    private bool initialized;
    public bool Supported => manager != IntPtr.Zero;
    public bool Allowed
    {
        get
        {
            if (!Supported) return false;
            Marshal.ThrowExceptionForHR(Method<GetBoolean>(manager, 7)(manager, out var allowed));
            return allowed != 0;
        }
    }

    public NativeTaskbarPin()
    {
        var result = RoInitialize(0);
        if (result < 0) return;
        initialized = true;
        IntPtr name = IntPtr.Zero, factory = IntPtr.Zero, marker = IntPtr.Zero;
        try
        {
            const string runtimeClass = "Windows.UI.Shell.TaskbarManager";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out name));
            var iid = new Guid("DB32AB74-DE52-4FE6-B7B6-95FF9F8395DF");
            if (RoGetActivationFactory(name, ref iid, out factory) < 0) return;
            var support = new Guid("CDFEFD63-E879-4134-B9A7-8283F05F9480");
            if (Marshal.QueryInterface(factory, in support, out marker) < 0) return;
            // Denied where there is no interactive shell (a service or a hosted CI runner): pinning is then unsupported.
            if (Method<GetObject>(factory, 6)(factory, out manager) < 0) manager = IntPtr.Zero;
        }
        finally
        {
            if (marker != IntPtr.Zero) Marshal.Release(marker);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (name != IntPtr.Zero) WindowsDeleteString(name);
        }
    }

    public async Task<bool> RequestAsync()
    {
        if (!Supported) return false;
        Marshal.ThrowExceptionForHR(Method<GetObject>(manager, 8)(manager, out var check));
        if (await ReadBooleanAsync(check)) return true;
        if (!Allowed) return false;
        Marshal.ThrowExceptionForHR(Method<GetObject>(manager, 10)(manager, out var request));
        return await ReadBooleanAsync(request);
    }

    private static async Task<bool> ReadBooleanAsync(IntPtr operation)
    {
        IntPtr info = IntPtr.Zero;
        try
        {
            var iid = new Guid("00000036-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(operation, in iid, out info));
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (true)
            {
                Marshal.ThrowExceptionForHR(Method<GetInteger>(info, 7)(info, out var status));
                if (status == 1)
                {
                    Marshal.ThrowExceptionForHR(Method<GetBoolean>(operation, 8)(operation, out var value));
                    return value != 0;
                }
                if (status == 2) return false;
                if (status == 3)
                {
                    Marshal.ThrowExceptionForHR(Method<GetInteger>(info, 8)(info, out var error));
                    Marshal.ThrowExceptionForHR(error);
                    return false;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    Method<Invoke>(info, 9)(info);
                    return false;
                }
                await Task.Delay(60);
            }
        }
        finally
        {
            if (info != IntPtr.Zero) { Method<Invoke>(info, 10)(info); Marshal.Release(info); }
            if (operation != IntPtr.Zero) Marshal.Release(operation);
        }
    }
    private static T Method<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    public void Dispose()
    {
        if (manager != IntPtr.Zero) { Marshal.Release(manager); manager = IntPtr.Zero; }
        if (initialized) { RoUninitialize(); initialized = false; }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBoolean(IntPtr self, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInteger(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Invoke(IntPtr self);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
}
