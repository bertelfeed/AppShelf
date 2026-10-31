param([Parameter(Mandatory)][string]$Tag, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'store-config.ps1')
$version = Test-AppShelfStoreRelease -Tag $Tag -ProjectRoot $projectRoot
$settings = Get-AppShelfStoreSettings -ProjectRoot $projectRoot -RequireProduction:(-not $DryRun)
if (-not $DryRun) {
    if ($env:MSSTORE_PUBLISH_ENABLED -ne 'true') { throw 'Store submission is disabled. Set repository variable MSSTORE_PUBLISH_ENABLED=true after setup.' }
    foreach ($name in @('AZURE_AD_TENANT_ID', 'SELLER_ID', 'AZURE_AD_APPLICATION_CLIENT_ID', 'AZURE_AD_APPLICATION_SECRET')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing GitHub environment secret: $name." }
    }
}
Write-Output ("Store release v$version validated. Dry run: " + [bool]$DryRun)
if ($DryRun -and -not $settings.ProductId) { Write-Output 'No Store product is connected. This run will build a development MSIX without submitting it.' }
