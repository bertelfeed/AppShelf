using System.Reflection;
using System.Text.RegularExpressions;

namespace AppShelf.Core;

/// <summary>Public web addresses the app opens in the browser.</summary>
public static class AppLinks
{
    public static string GitHub => Metadata("RepositoryUrl");
    public static string StoreProductId => Metadata("StoreProductId");
    public static Uri? MicrosoftStore => StorePageFor(StoreProductId);
    public static Uri? MicrosoftStoreWeb => StorePageFor(StoreProductId, web: true);
    public static Uri? MicrosoftStoreReview => StoreReviewFor(StoreProductId);
    public static Uri? NewIssue => NewIssueFor(GitHub);

    /// <summary>A product-specific link, or null until a real Store ID is configured.</summary>
    public static Uri? StorePageFor(string? productId, bool web = false) => ValidProductId(productId) is { } id
        ? new Uri(web ? "https://apps.microsoft.com/detail/" + id : "ms-windows-store://pdp/?ProductId=" + id) : null;

    /// <summary>The Store's rating dialog for the product, or null until a real Store ID is configured.</summary>
    public static Uri? StoreReviewFor(string? productId) =>
        ValidProductId(productId) is { } id ? new Uri("ms-windows-store://review/?ProductId=" + id) : null;

    /// <summary>The repository's issue template picker, or null without an https repository address.</summary>
    public static Uri? NewIssueFor(string? repositoryUrl) =>
        Uri.TryCreate(repositoryUrl?.Trim().TrimEnd('/'), UriKind.Absolute, out var repository) && repository.Scheme == Uri.UriSchemeHttps
            ? new Uri(repository.AbsoluteUri.TrimEnd('/') + "/issues/new/choose") : null;

    private static string? ValidProductId(string? productId)
    {
        var id = productId?.Trim().ToUpperInvariant();
        return id is not null && Regex.IsMatch(id, @"^9[A-Z0-9]{11}$", RegexOptions.CultureInvariant) ? id : null;
    }
    private static string Metadata(string key) => typeof(AppLinks).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == key)?.Value ?? "";

    /// <summary>The project website (site/ in the repository, on GitHub Pages).</summary>
    public const string Website = "https://bertelfeed.github.io/AppShelf/";

    /// <summary>The website page in the interface language: Russian has its own page, every other language gets English.</summary>
    public static Uri WebsiteFor(string? language) =>
        new(Localization.NormalizeLanguage(language) == "ru-RU" ? Website + "ru/" : Website);

    /// <summary>The author's Boosty page; also listed in .github/FUNDING.yml.</summary>
    public const string Support = "https://boosty.to/bertelfeed/donate";
}
