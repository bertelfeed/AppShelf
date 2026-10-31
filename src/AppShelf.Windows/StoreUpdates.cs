using System.Runtime.InteropServices;
using static AppShelf.Windows.WinRt;

namespace AppShelf.Windows;

/// <summary>
/// Asks Microsoft Store whether a newer version of this package is ready. The Store downloads and installs it on its
/// own; AppShelf only tells the user. Meaningful only in the Store package: elsewhere the Store finds nothing or fails.
/// </summary>
public static class StoreUpdates
{
    private static readonly Guid StoreContextStatics = new("9c06ee5f-15c0-4e72-9330-d6191cebd19c");

    public static bool Available() => OnWorker(() =>
    {
        var statics = Factory("Windows.Services.Store.StoreContext", StoreContextStatics);
        try
        {
            var context = Out(statics, 6); // IStoreContextStatics.GetDefault
            try
            {
                var operation = Out(context, 23); // IStoreContext.GetAppAndOptionalStorePackageUpdatesAsync
                try
                {
                    Wait(operation, TimeSpan.FromMinutes(1));
                    var updates = Out(operation, 8); // IVectorView<StorePackageUpdate>
                    if (updates == IntPtr.Zero) return false;
                    try { return OutInt(updates, 7) > 0; } // get_Size
                    finally { Marshal.Release(updates); }
                }
                finally { Marshal.Release(operation); }
            }
            finally { Marshal.Release(context); }
        }
        finally { Marshal.Release(statics); }
    });
}
