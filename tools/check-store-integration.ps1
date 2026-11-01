$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'store-config.ps1')
. (Join-Path $PSScriptRoot 'test-store-package.ps1')
$testRoot = Join-Path $projectRoot ('artifacts\store-integration-checks\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'packaging') -Force | Out-Null
$environmentNames = @('MSSTORE_PRODUCT_ID', 'MSSTORE_IDENTITY_NAME', 'MSSTORE_PUBLISHER', 'MSSTORE_PUBLISHER_DISPLAY_NAME', 'MSSTORE_PUBLISH_ENABLED',
    'AZURE_AD_TENANT_ID', 'SELLER_ID', 'AZURE_AD_APPLICATION_CLIENT_ID', 'AZURE_AD_APPLICATION_SECRET')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $null) }
$global:AppShelfStoreTestCalls = [Collections.Generic.List[object]]::new()
$global:AppShelfStoreTestExitCode = 0
function Assert-StoreCheck([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Reject-StoreCheck([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-StoreCheck $rejected $Message
}
# This local function replaces the CLI only inside this test script. No Store request is made.
function msstore { $global:AppShelfStoreTestCalls.Add(@($args)); $global:LASTEXITCODE = $global:AppShelfStoreTestExitCode }
try {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Destination (Join-Path $testRoot 'Directory.Build.props')
    '<Project><PropertyGroup><StoreProductId/><StoreIdentityName/><StorePublisher/><StorePublisherDisplayName>Tests</StorePublisherDisplayName></PropertyGroup></Project>' |
        Set-Content -LiteralPath (Join-Path $testRoot 'packaging\StoreIdentity.props') -Encoding utf8
    $development = Get-AppShelfStoreSettings -ProjectRoot $testRoot
    Assert-StoreCheck ($development.ProductId -eq '' -and $development.IdentityName -eq 'AppShelf.Dev') 'Development defaults changed.'
    Reject-StoreCheck { Get-AppShelfStoreSettings -ProjectRoot $testRoot -RequireProduction } 'An empty production identity was accepted.'
    Reject-StoreCheck { Get-AppShelfStoreSettings -ProjectRoot $testRoot -Overrides @{ ProductId = '9AAAA&evil=1' } } 'An invalid Store ID was accepted.'
    Reject-StoreCheck { Get-AppShelfStoreSettings -ProjectRoot $testRoot -Overrides @{ PublisherDisplayName = "bad`nname" } } 'A multiline public setting was accepted.'
    $version = [string]([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    Assert-StoreCheck ((Test-AppShelfStoreRelease -Tag "v$version" -ProjectRoot $testRoot) -eq $version) 'The current release tag was rejected.'
    Reject-StoreCheck { Test-AppShelfStoreRelease -Tag 'v65535.65535.65535' -ProjectRoot $testRoot } 'A mismatched source version was accepted.'
    Reject-StoreCheck { Test-AppShelfStoreRelease -Tag 'v1.0.0;Write-Host bad' -ProjectRoot $testRoot } 'An executable tag string was accepted.'
    Reject-StoreCheck { Test-AppShelfStoreRelease -Tag 'main' -ProjectRoot $testRoot } 'A branch was accepted as a release tag.'
    Write-Output 'PASS Store configuration and release-tag validation'

    # Synthetic identity for local fixtures. It is never sent to Microsoft or embedded in a shipped package.
    $env:MSSTORE_PRODUCT_ID = '9AAAAAAAAAAA'
    $env:MSSTORE_IDENTITY_NAME = 'AppShelf.Tests'
    $env:MSSTORE_PUBLISHER = 'CN=Tests'
    $env:MSSTORE_PUBLISHER_DISPLAY_NAME = 'Tests'
    $settings = Get-AppShelfStoreSettings -ProjectRoot $testRoot -RequireProduction
    Reject-StoreCheck { Get-AppShelfStoreSettings -ProjectRoot $testRoot -RequireProduction -Overrides @{ IdentityName = 'AppShelf.Dev' } } 'A development package was accepted for production.'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    function New-StoreFixture([string]$Path, [switch]$PrivateFile, [switch]$Headless) {
        $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in @('AppxManifest.xml', 'AppShelf.exe', 'AppShelf.Mcp.exe', 'resources.pri', 'ru/AppShelf.Core.resources.dll', 'LICENSE', 'licenses/NET-LICENSE.txt')) {
                $writer = [IO.StreamWriter]::new($archive.CreateEntry($entry).Open())
                try {
                    if ($entry -eq 'AppxManifest.xml') {
                        $hidden = if ($Headless) { '<Application Id="Hidden"><uap:VisualElements AppListEntry="none" /></Application>' } else { '' }
                        $writer.Write('<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">' +
                            '<Identity Name="AppShelf.Tests" Publisher="CN=Tests" Version="' + $version + '.0" ProcessorArchitecture="x64" /><Applications>' +
                            '<Application Id="App"><uap:VisualElements /><Extensions><desktop:ExecutionAlias Alias="appshelf.exe" /></Extensions></Application>' + $hidden + '</Applications></Package>')
                    }
                    else { $writer.Write('test fixture') }
                } finally { $writer.Dispose() }
            }
            if ($PrivateFile) { $archive.CreateEntry('data/shelf.json') | Out-Null }
        } finally { $archive.Dispose() }
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash + '  fixture.msix') | Set-Content -LiteralPath ($Path + '.sha256') -Encoding ascii
    }
    $package = Join-Path $testRoot 'fixture.msix'
    New-StoreFixture $package
    Test-AppShelfStorePackage -Path $package -Settings $settings -Version $version
    $wrong = [pscustomobject]@{ IdentityName = 'Another.App'; Publisher = 'CN=Tests' }
    Reject-StoreCheck { Test-AppShelfStorePackage -Path $package -Settings $wrong -Version $version } 'An MSIX for another app was accepted.'
    $private = Join-Path $testRoot 'private-fixture.msix'
    New-StoreFixture $private -PrivateFile
    Reject-StoreCheck { Test-AppShelfStorePackage -Path $private -Settings $settings -Version $version } 'A package containing a profile was accepted.'
    $headless = Join-Path $testRoot 'headless-fixture.msix'
    New-StoreFixture $headless -Headless
    Reject-StoreCheck { Test-AppShelfStorePackage -Path $headless -Settings $settings -Version $version } 'A package with an application hidden from Start was accepted.'
    Write-Output 'PASS MSIX identity, version, layout, Start entries and checksum validation'

    & (Join-Path $PSScriptRoot 'submit-store-release.ps1') -Tag "v$version" -PackagePath $package -DryRun | Out-Null
    Assert-StoreCheck ($global:AppShelfStoreTestCalls.Count -eq 0) 'Dry run contacted the CLI.'
    Reject-StoreCheck { & (Join-Path $PSScriptRoot 'submit-store-release.ps1') -Tag "v$version" -PackagePath $package } 'A disabled Store integration submitted a package.'
    $env:MSSTORE_PUBLISH_ENABLED = 'true'
    Reject-StoreCheck { & (Join-Path $PSScriptRoot 'submit-store-release.ps1') -Tag "v$version" -PackagePath $package } 'Submission accepted missing credentials.'
    Assert-StoreCheck ($global:AppShelfStoreTestCalls.Count -eq 0) 'A rejected run contacted the CLI.'
    $env:AZURE_AD_TENANT_ID = '00000000-0000-0000-0000-000000000001'
    $env:AZURE_AD_APPLICATION_CLIENT_ID = '00000000-0000-0000-0000-000000000002'
    $env:SELLER_ID = 'unit-test-seller'
    $env:AZURE_AD_APPLICATION_SECRET = 'unit-test-not-a-credential'
    & (Join-Path $PSScriptRoot 'submit-store-release.ps1') -Tag "v$version" -PackagePath $package | Out-Null
    Assert-StoreCheck ($global:AppShelfStoreTestCalls.Count -eq 2 -and $global:AppShelfStoreTestCalls[0][0] -eq 'reconfigure' -and $global:AppShelfStoreTestCalls[1][0] -eq 'publish') 'Submission did not authenticate before publishing.'
    Assert-StoreCheck ($global:AppShelfStoreTestCalls[1][-1] -eq '9AAAAAAAAAAA') 'Submission used the wrong product.'
    $global:AppShelfStoreTestCalls.Clear()
    $global:AppShelfStoreTestExitCode = 7
    Reject-StoreCheck { & (Join-Path $PSScriptRoot 'submit-store-release.ps1') -Tag "v$version" -PackagePath $package } 'An authentication failure was ignored.'
    Assert-StoreCheck ($global:AppShelfStoreTestCalls.Count -eq 1) 'Publishing continued after authentication failed.'
    Write-Output 'PASS Dry run, disabled publishing, missing secrets, mocked submission and authentication failure'
    [IO.File]::AppendAllText($package, 'changed')
    Reject-StoreCheck { Test-AppShelfStorePackage -Path $package -Settings $settings -Version $version } 'A changed package passed its checksum.'
    Write-Output 'PASS A modified MSIX is rejected before upload'
    Write-Output 'RESULT Store integration checks passed; no network or Store submission was performed.'
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    Remove-Variable -Name AppShelfStoreTestCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable -Name AppShelfStoreTestExitCode -Scope Global -ErrorAction SilentlyContinue
    $global:LASTEXITCODE = 0
}
