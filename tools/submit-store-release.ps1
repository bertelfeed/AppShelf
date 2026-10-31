param([Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$PackagePath, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'store-config.ps1')
. (Join-Path $PSScriptRoot 'test-store-package.ps1')
$version = Test-AppShelfStoreRelease -Tag $Tag -ProjectRoot $projectRoot
$settings = Get-AppShelfStoreSettings -ProjectRoot $projectRoot -RequireProduction:(-not $DryRun)
Test-AppShelfStorePackage -Path $PackagePath -Settings $settings -Version $version
if ($DryRun) { Write-Output 'MSIX validated. Dry run complete: no authentication, upload or submission was performed.'; return }
if ($env:MSSTORE_PUBLISH_ENABLED -ne 'true') { throw 'Store submission is disabled. Set MSSTORE_PUBLISH_ENABLED=true after setup.' }
foreach ($name in @('AZURE_AD_TENANT_ID', 'SELLER_ID', 'AZURE_AD_APPLICATION_CLIENT_ID', 'AZURE_AD_APPLICATION_SECRET')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing GitHub environment secret: $name." }
}
Get-Command msstore -ErrorAction Stop | Out-Null
& msstore reconfigure --tenantId $env:AZURE_AD_TENANT_ID --sellerId $env:SELLER_ID --clientId $env:AZURE_AD_APPLICATION_CLIENT_ID --clientSecret $env:AZURE_AD_APPLICATION_SECRET
if ($LASTEXITCODE -ne 0) { throw 'Microsoft Store CLI authentication failed.' }
& msstore publish ([IO.Path]::GetFullPath($PackagePath)) -id $settings.ProductId
if ($LASTEXITCODE -ne 0) { throw 'Microsoft Store submission failed. Check the Partner Center submission status before retrying.' }
Write-Output 'The package was submitted to Microsoft Store. Availability depends on Microsoft certification.'
