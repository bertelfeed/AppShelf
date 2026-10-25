using System.Runtime.InteropServices;

namespace AppShelf.Windows;

public static class TaskbarTabs
{
    public static void Add(IntPtr window)
    {
        var taskbar = (ITaskbarList)new TaskbarList();
        try { taskbar.HrInit(); taskbar.AddTab(window); }
        finally { Marshal.FinalReleaseComObject(taskbar); }
    }
    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")] private class TaskbarList { }
    [ComImport, Guid("56FDF342-FD6D-11D0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr window);
        void DeleteTab(IntPtr window);
        void ActivateTab(IntPtr window);
        void SetActiveAlt(IntPtr window);
    }
}
