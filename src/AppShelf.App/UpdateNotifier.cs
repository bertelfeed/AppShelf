using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AppShelf.App;

/// <summary>
/// Tells the user that a newer version is out; it never downloads or installs anything. The Store version asks
/// Microsoft Store, which installs updates itself; the GitHub version asks GitHub for the latest release. A check runs
/// a minute after start and then once a day, and not at all while "Check for updates" is off.
/// </summary>
public sealed class UpdateNotifier : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ShelfSession session;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool enabled, checking;

    /// <summary>True when an update can be installed; <see cref="Version"/> is known only for GitHub releases.</summary>
    public bool Available { get; private set; }
    public Version? Version { get; private set; }
    public event Action? Changed;

    public UpdateNotifier(ShelfSession session)
    {
        this.session = session;
        enabled = session.Configuration.CheckForUpdates;
        timer.Tick += async (_, _) => { timer.Interval = TimeSpan.FromDays(1); await CheckAsync(); };
        if (enabled) timer.Start();
        session.Changed += SettingChanged;
    }

    private void SettingChanged()
    {
        if (session.Configuration.CheckForUpdates == enabled) return;
        enabled = session.Configuration.CheckForUpdates;
        timer.Stop();
        if (enabled) { timer.Interval = TimeSpan.FromSeconds(2); timer.Start(); }
        else if (Available) { Available = false; Version = null; Changed?.Invoke(); }
    }

    public async Task CheckAsync()
    {
        if (!enabled || checking) return;
        checking = true;
        try
        {
            if (AppLocations.IsPackaged)
            {
                Available = await Task.Run(StoreUpdates.Available);
                Version = null;
            }
            else
            {
                var current = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
                var latest = await ReleaseFeed.LatestAsync(Http, AppLinks.GitHub, current);
                Available = latest is not null && ReleaseFeed.IsNewer(latest, current);
                Version = Available ? latest : null;
            }
            Changed?.Invoke();
        }
        // Offline, GitHub's rate limit or a Store that has nothing to say: try again tomorrow, quietly.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or COMException or TimeoutException or OperationCanceledException) { }
        catch (Exception ex) { App.Current.Log(ex); }
        finally { checking = false; }
    }

    /// <summary>The Store page installs the update; the GitHub release page offers the new ZIP.</summary>
    public void Open()
    {
        if (AppLocations.IsPackaged) App.Current.OpenStorePage();
        else if (ReleaseFeed.PageFor(AppLinks.GitHub) is { } page) App.Current.OpenPage(page.AbsoluteUri);
    }

    public string Label => Version is { } version ? L.F("UpdateAvailableVersion", version.ToString(3)) : L.Get("UpdateAvailable");
    public string Help => L.Get(AppLocations.IsPackaged ? "UpdateHelpStore" : "UpdateHelpPortable");

    public void Dispose() { timer.Stop(); session.Changed -= SettingChanged; }
}
