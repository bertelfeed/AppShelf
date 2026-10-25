param(
    [switch]$Check,
    [switch]$Publish,
    [switch]$Package,
    [string]$RuntimeVersion = '10.0.13',
    [string]$OutputDirectory = 'artifacts/AppShelf'
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
    if ($Publish -or $Package) {
        $version = [string]([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
        if ($version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid application version.' }
        # Packages always publish into a fresh directory, never the user's portable data folder.
        if ($Package) {
            $stage = Join-Path $PSScriptRoot ('artifacts\package-stage\' + $version + '-' + [guid]::NewGuid().ToString('N'))
            $publishTarget = Join-Path $stage 'AppShelf'
        }
        elseif ([IO.Path]::IsPathRooted($OutputDirectory)) { $publishTarget = [IO.Path]::GetFullPath($OutputDirectory) }
        else { $publishTarget = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory)) }
        & dotnet publish src/AppShelf.App/AppShelf.App.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o $publishTarget
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close AppShelf before updating a running portable folder.' }
        & dotnet publish src/AppShelf.Mcp/AppShelf.Mcp.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o $publishTarget
        if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed. Close AppShelf.Mcp before updating a running portable folder.' }
        New-Item -ItemType File -Path (Join-Path $publishTarget 'portable.flag') -Force | Out-Null
        foreach ($file in @('LICENSE', 'QUICKSTART.txt', 'QUICKSTART.ru.txt', 'MCP.md', 'THIRD-PARTY-NOTICES.txt')) {
            Copy-Item -LiteralPath $file -Destination (Join-Path $publishTarget $file) -Force
        }
        Copy-Item -LiteralPath 'QUICKSTART.ru.txt' -Destination (Join-Path $publishTarget 'Прочитай меня.txt') -Force
        Copy-Item -LiteralPath 'MCP.md' -Destination (Join-Path $publishTarget 'MCP — подключение.md') -Force
        $licenses = Join-Path $publishTarget 'licenses'
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        $runtimeRoot = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64\$RuntimeVersion"
        Copy-Item -LiteralPath (Join-Path $runtimeRoot 'LICENSE.TXT') -Destination (Join-Path $licenses 'NET-LICENSE.txt') -Force
        Copy-Item -LiteralPath (Join-Path $runtimeRoot 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'NET-THIRD-PARTY-NOTICES.txt') -Force
        Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.host.win-x64\$RuntimeVersion\THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $licenses 'NET-HOST-THIRD-PARTY-NOTICES.txt') -Force
        Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES "microsoft.windowsdesktop.app.runtime.win-x64\$RuntimeVersion\LICENSE") -Destination (Join-Path $licenses 'WPF-LICENSE.txt') -Force
        if ($Package) {
            if (Test-Path -LiteralPath (Join-Path $publishTarget 'data')) { throw 'Refusing to package a directory containing user data.' }
            # Debug symbols contain build-machine paths and are not needed to run the app.
            Get-ChildItem -LiteralPath $publishTarget -File -Filter '*.pdb' | Remove-Item -Force
            $zip = Join-Path $PSScriptRoot "artifacts\AppShelf-$version-win-x64.zip"
            Compress-Archive -LiteralPath $publishTarget -DestinationPath $zip -CompressionLevel Optimal -Force
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [IO.Compression.ZipFile]::OpenRead($zip)
            try {
                $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
                if ($entries | Where-Object { $_ -match '(^|/)data/|\.pdb$|(^|/)\.env($|\.)' }) { throw 'Private or debug files in package.' }
                foreach ($required in @('AppShelf/AppShelf.exe', 'AppShelf/AppShelf.Mcp.exe', 'AppShelf/LICENSE', 'AppShelf/licenses/NET-LICENSE.txt', 'AppShelf/ru/AppShelf.Core.resources.dll', 'AppShelf/QUICKSTART.ru.txt')) {
                    if ($entries -notcontains $required) { throw "Missing package entry: $required" }
                }
            }
            finally { $archive.Dispose() }
            $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
            ($hash + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
            Write-Host "Package: $zip"
            Write-Host "SHA256: $hash"
        }
    }
}
finally { Pop-Location }
