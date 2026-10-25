param(
    [switch]$Check,
    [switch]$Publish,
    [string]$RuntimeVersion = '10.0.13'
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
    & dotnet build AppShelf.slnx -c Release -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Check) {
        & '.\tests\AppShelf.Checks\bin\Release\net10.0-windows\AppShelf.Checks.exe' (Join-Path $PSScriptRoot 'artifacts\checks')
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    }
    if ($Publish) {
        & dotnet publish src/AppShelf.App/AppShelf.App.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o artifacts/AppShelf
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close running AppShelf before rebuilding.' }
        New-Item -ItemType File -Path 'artifacts\AppShelf\portable.flag' -Force | Out-Null
        Copy-Item -LiteralPath 'QUICKSTART.txt' -Destination 'artifacts\AppShelf\Прочитай меня.txt' -Force
        Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.txt' -Destination 'artifacts\AppShelf\THIRD-PARTY-NOTICES.txt' -Force
        New-Item -ItemType Directory -Path 'artifacts\AppShelf\licenses' -Force | Out-Null
        Copy-Item -LiteralPath ".packages\microsoft.netcore.app.runtime.win-x64\$RuntimeVersion\LICENSE.TXT" -Destination 'artifacts\AppShelf\licenses\NET-LICENSE.txt' -Force
        Copy-Item -LiteralPath ".packages\microsoft.netcore.app.runtime.win-x64\$RuntimeVersion\THIRD-PARTY-NOTICES.TXT" -Destination 'artifacts\AppShelf\licenses\NET-THIRD-PARTY-NOTICES.txt' -Force
        Copy-Item -LiteralPath ".packages\microsoft.netcore.app.host.win-x64\$RuntimeVersion\THIRD-PARTY-NOTICES.TXT" -Destination 'artifacts\AppShelf\licenses\NET-HOST-THIRD-PARTY-NOTICES.txt' -Force
        Copy-Item -LiteralPath ".packages\microsoft.windowsdesktop.app.runtime.win-x64\$RuntimeVersion\LICENSE" -Destination 'artifacts\AppShelf\licenses\WPF-LICENSE.txt' -Force
    }
}
finally { Pop-Location }
