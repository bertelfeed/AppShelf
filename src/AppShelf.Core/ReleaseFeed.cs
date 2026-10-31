using System.Net;
using System.Text.Json;

namespace AppShelf.Core;

/// <summary>
/// The newest published release on GitHub, for the portable version's update notice. GitHub's "latest release" skips
/// drafts and prereleases, so previews never announce themselves. Nothing is downloaded: the user opens the page.
/// </summary>
public static class ReleaseFeed
{
    /// <summary>https://github.com/owner/repo → its latest-release API address, or null for anything else.</summary>
    public static Uri? ApiFor(string repositoryUrl) =>
        Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository) && repository.Scheme == "https" && repository.Host == "github.com" &&
        repository.AbsolutePath.Trim('/').Split('/') is [var owner, var name] && owner.Length > 0 && name.Length > 0
            ? new Uri($"https://api.github.com/repos/{owner}/{name}/releases/latest") : null;

    public static Uri? PageFor(string repositoryUrl) =>
        ApiFor(repositoryUrl) is null ? null : new Uri(repositoryUrl.TrimEnd('/') + "/releases/latest");

    /// <summary>The version in a release's JSON (tag "v1.2.3" or "1.2.3"), or null for drafts, prereleases and anything unreadable.</summary>
    public static Version? VersionIn(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } text) return null;
            return Version.TryParse(text.TrimStart('v', 'V'), out var version) && version.Build >= 0 ? Normalize(version) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Major.minor.patch only: the assembly's fourth part is always zero.</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    public static bool IsNewer(Version latest, Version current) => Normalize(latest) > Normalize(current);

    /// <summary>The latest release's version; null when there is none yet (404) or GitHub did not answer usefully.</summary>
    public static async Task<Version?> LatestAsync(HttpClient http, string repositoryUrl, Version current, CancellationToken cancel = default)
    {
        if (ApiFor(repositoryUrl) is not { } api) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.UserAgent.ParseAdd($"AppShelf/{Normalize(current)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
        return VersionIn(await response.Content.ReadAsStringAsync(cancel));
    }
}
