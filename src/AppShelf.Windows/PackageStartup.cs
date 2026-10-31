using System.Runtime.InteropServices;
using static AppShelf.Windows.WinRt;

namespace AppShelf.Windows;

/// <summary>
/// The Store package starts at sign-in through the startup task declared in its manifest. A packaged process writes the
/// Run key into its private registry, which Windows never reads. The calls go through <see cref="WinRt"/>.
/// </summary>
public static class PackageStartup
{
    /// <summary>Must match desktop:StartupTask/@TaskId in packaging/AppxManifest.xml.</summary>
    public const string TaskId = "AppShelfStartup";
    public enum State { Disabled, DisabledByUser, Enabled, DisabledByPolicy, EnabledByPolicy }

    private static readonly Guid StartupTaskStatics = new("ee5b60bd-a148-41a7-b26e-e8b88a1e62f8");
    private static readonly Guid AppInstanceStatics = new("9d11e77f-9ea6-47af-a6ec-46784c5ba254");
    private const int StartupTaskActivation = 1020;

    public static bool IsEnabled(State state) => state is State.Enabled or State.EnabledByPolicy;

    public static State Read() => OnWorker(() => WithTask(task => (State)OutInt(task, 8)));

    /// <summary>Returns the resulting state: a task the user turned off in Settings stays DisabledByUser.</summary>
    public static State Set(bool enabled) => OnWorker(() => WithTask(task =>
    {
        if (!enabled) { Call(task, 7); return (State)OutInt(task, 8); }
        var operation = Out(task, 6);
        try { Wait(operation, TimeSpan.FromMinutes(5)); return (State)OutInt(operation, 8); }
        finally { Marshal.Release(operation); }
    }));

    /// <summary>True when Windows started this process for the startup task, i.e. at sign-in.</summary>
    public static bool LaunchedAtSignIn()
    {
        try
        {
            return OnWorker(() =>
            {
                var statics = Factory("Windows.ApplicationModel.AppInstance", AppInstanceStatics);
                try
                {
                    var args = Out(statics, 7);
                    if (args == IntPtr.Zero) return false;
                    try { return OutInt(args, 6) == StartupTaskActivation; }
                    finally { Marshal.Release(args); }
                }
                finally { Marshal.Release(statics); }
            });
        }
        catch (Exception ex) when (ex is COMException or TimeoutException) { return false; }
    }

    private static T WithTask<T>(Func<IntPtr, T> use)
    {
        var statics = Factory("Windows.ApplicationModel.StartupTask", StartupTaskStatics);
        try
        {
            var id = HString(TaskId);
            IntPtr operation;
            try { operation = Out(statics, 7, id); }
            finally { DeleteHString(id); }
            try
            {
                Wait(operation, TimeSpan.FromSeconds(30));
                var task = Out(operation, 8);
                try { return use(task); }
                finally { Marshal.Release(task); }
            }
            finally { Marshal.Release(operation); }
        }
        finally { Marshal.Release(statics); }
    }
}
