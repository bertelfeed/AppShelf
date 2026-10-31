function Get-AppShelfStoreSettings {
    param(
        [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
        [hashtable]$Overrides = @{},
        [switch]$RequireProduction
    )
    $identityFile = Join-Path $ProjectRoot 'packaging\StoreIdentity.props'
    [xml]$identity = Get-Content -LiteralPath $identityFile -Raw -Encoding UTF8
    $mapping = @{
        ProductId = @('MSSTORE_PRODUCT_ID', 'StoreProductId')
        IdentityName = @('MSSTORE_IDENTITY_NAME', 'StoreIdentityName')
        Publisher = @('MSSTORE_PUBLISHER', 'StorePublisher')
        PublisherDisplayName = @('MSSTORE_PUBLISHER_DISPLAY_NAME', 'StorePublisherDisplayName')
    }
    $settings = @{}
    foreach ($key in $mapping.Keys) {
        $environmentName, $property = $mapping[$key]
        $node = $identity.SelectSingleNode('/Project/PropertyGroup/' + $property)
        $value = if ($node) { $node.InnerText } else { '' }
        $environmentValue = [Environment]::GetEnvironmentVariable($environmentName)
        if (-not [string]::IsNullOrWhiteSpace($environmentValue)) { $value = $environmentValue }
        if ($Overrides.ContainsKey($key)) { $value = [string]$Overrides[$key] }
        $value = ([string]$value).Trim()
        if ($value -match '[\x00-\x1F\x7F]') { throw "Control characters are not allowed in $key." }
        $settings[$key] = $value
    }
    $settings.ProductId = $settings.ProductId.ToUpperInvariant()
    if ($settings.ProductId -and $settings.ProductId -cnotmatch '^9[A-Z0-9]{11}$') { throw 'Invalid MSSTORE_PRODUCT_ID: use the 12-character MSIX Store product ID.' }
    if ($settings.IdentityName -and $settings.IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw 'Invalid MSSTORE_IDENTITY_NAME.' }
    if ($settings.Publisher -and $settings.Publisher -notmatch '(^|,\s*)CN=') { throw 'Invalid MSSTORE_PUBLISHER: use the exact Partner Center Publisher value.' }
    $developmentPublisher = 'CN=AppShelf Dev, OID.2.25.311729368913984317654407730594956997722=1'
    if ($RequireProduction) {
        $missing = @($mapping.Keys | Where-Object { [string]::IsNullOrWhiteSpace($settings[$_]) })
        if ($missing.Count) { throw ('Partner Center identity is incomplete: ' + ($missing -join ', ') + '. See docs/STORE-PUBLISHING.md.') }
        if ($settings.IdentityName -eq 'AppShelf.Dev' -or $settings.Publisher -eq $developmentPublisher) { throw 'A development identity cannot be submitted to Microsoft Store.' }
    }
    else {
        if (-not $settings.IdentityName) { $settings.IdentityName = 'AppShelf.Dev' }
        if (-not $settings.Publisher) { $settings.Publisher = $developmentPublisher }
        if (-not $settings.PublisherDisplayName) { $settings.PublisherDisplayName = 'bertelfeed' }
    }
    [pscustomobject]$settings
}

function Test-AppShelfStoreRelease {
    param([Parameter(Mandatory)][string]$Tag, [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
    if ($Tag -cnotmatch '^v([0-9]+\.[0-9]+\.[0-9]+)$') { throw 'Use a version tag such as v1.0.1. Branch names and prerelease suffixes are not Store release tags.' }
    $tagVersion = $Matches[1]
    [xml]$build = Get-Content -LiteralPath (Join-Path $ProjectRoot 'Directory.Build.props') -Raw -Encoding UTF8
    $version = [string]$build.Project.PropertyGroup.Version
    if ($tagVersion -cne $version) { throw "Release tag $Tag does not match Directory.Build.props ($version)." }
    foreach ($part in $tagVersion.Split('.')) { if ([long]$part -gt 65535) { throw 'MSIX version components must be at most 65535.' } }
    $version
}
