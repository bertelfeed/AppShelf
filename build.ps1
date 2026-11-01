param(
    [switch]$Check,
    [switch]$Publish,
    [switch]$Package,
    # MSIX for the Microsoft Store. The default identity is for package development.
    # This command builds the package; it does not install it. With -Package both come from one publish.
    [switch]$Msix,
    [string]$IdentityName = '',
    [string]$Publisher = '',
    [string]$PublisherDisplayName = '',
    [string]$StoreProductId = '',
    [string]$RuntimeVersion = '10.0.13',
    [string]$OutputDirectory = 'artifacts/AppShelf'
)
$ErrorActionPreference = 'Stop'
if ($Msix -and $Publish) { throw 'Build MSIX separately from updating a portable folder: use -Msix with -Package, not with -Publish.' }
Push-Location -LiteralPath $PSScriptRoot
try {
    . (Join-Path $PSScriptRoot 'tools\store-config.ps1')
    $overrides = @{}
    foreach ($parameter in @('IdentityName', 'Publisher', 'PublisherDisplayName', 'StoreProductId')) {
        if ($PSBoundParameters.ContainsKey($parameter)) {
            $key = if ($parameter -eq 'StoreProductId') { 'ProductId' } else { $parameter }
            $overrides[$key] = $PSBoundParameters[$parameter]
        }
    }
    $store = Get-AppShelfStoreSettings -ProjectRoot $PSScriptRoot -Overrides $overrides
    $IdentityName = $store.IdentityName; $Publisher = $store.Publisher; $PublisherDisplayName = $store.PublisherDisplayName
    $StoreProductId = $store.ProductId
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
    & dotnet build AppShelf.slnx -c Release -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false "-p:StoreProductId=$StoreProductId"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Check) {
        & '.\tests\AppShelf.Checks\bin\Release\net10.0-windows\AppShelf.Checks.exe' (Join-Path $PSScriptRoot 'artifacts\checks')
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    }
    if ($Publish -or $Package -or $Msix) {
        $version = [string]([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
        if ($version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid application version.' }
        # Packages always publish into a fresh directory, never the user's portable data folder.
        # The ZIP and the MSIX are two layouts of the same published binaries.
        if ($Package -or $Msix) {
            $stage = Join-Path $PSScriptRoot ('artifacts\package-stage\' + $version + '-' + [guid]::NewGuid().ToString('N'))
            $binaries = Join-Path $stage 'bin'
        }
        elseif ([IO.Path]::IsPathRooted($OutputDirectory)) { $binaries = [IO.Path]::GetFullPath($OutputDirectory) }
        else { $binaries = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory)) }
        & dotnet publish src/AppShelf.App/AppShelf.App.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" "-p:StoreProductId=$StoreProductId" -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o $binaries
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close AppShelf before updating a running portable folder.' }
        & dotnet publish src/AppShelf.Mcp/AppShelf.Mcp.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" "-p:StoreProductId=$StoreProductId" -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 -o $binaries
        if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed. Close AppShelf.Mcp before updating a running portable folder.' }
        Copy-Item -LiteralPath 'LICENSE', 'THIRD-PARTY-NOTICES.txt' -Destination $binaries -Force
        $licenses = Join-Path $binaries 'licenses'
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        $runtimeRoot = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64\$RuntimeVersion"
        Copy-Item -LiteralPath (Join-Path $runtimeRoot 'LICENSE.TXT') -Destination (Join-Path $licenses 'NET-LICENSE.txt') -Force
        Copy-Item -LiteralPath (Join-Path $runtimeRoot 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'NET-THIRD-PARTY-NOTICES.txt') -Force
        # The app host comes from the SDK's packs folder when the SDK has this runtime version, and that pack has no
        # notices; its NuGet package carries the runtime's notices above. A separate copy is kept only if they differ.
        $hostNotices = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.host.win-x64\$RuntimeVersion\THIRD-PARTY-NOTICES.TXT"
        if ((Test-Path -LiteralPath $hostNotices) -and
            (Get-FileHash -LiteralPath $hostNotices).Hash -ne (Get-FileHash -LiteralPath (Join-Path $runtimeRoot 'THIRD-PARTY-NOTICES.TXT')).Hash) {
            Copy-Item -LiteralPath $hostNotices -Destination (Join-Path $licenses 'NET-HOST-THIRD-PARTY-NOTICES.txt') -Force
        }
        Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES "microsoft.windowsdesktop.app.runtime.win-x64\$RuntimeVersion\LICENSE") -Destination (Join-Path $licenses 'WPF-LICENSE.txt') -Force
        # A portable copy keeps its profile beside the program and ships its notes; the Store version has neither.
        function Add-PortableFiles([string]$target) {
            New-Item -ItemType File -Path (Join-Path $target 'portable.flag') -Force | Out-Null
            foreach ($file in @('QUICKSTART.txt', 'QUICKSTART.ru.txt', 'MCP.md')) {
                Copy-Item -LiteralPath $file -Destination (Join-Path $target $file) -Force
            }
            Copy-Item -LiteralPath 'QUICKSTART.ru.txt' -Destination (Join-Path $target 'Прочитай меня.txt') -Force
            Copy-Item -LiteralPath 'MCP.md' -Destination (Join-Path $target 'MCP — подключение.md') -Force
        }
        function Copy-Layout([string]$target) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $binaries -Destination $target -Recurse
            $target
        }
        if ($Publish) { Add-PortableFiles $binaries }
        else {
            if (Test-Path -LiteralPath (Join-Path $binaries 'data')) { throw 'Refusing to package a directory containing user data.' }
            # Debug symbols contain build-machine paths and are not needed to run the app.
            Get-ChildItem -LiteralPath $binaries -File -Filter '*.pdb' | Remove-Item -Force
        }
        if ($Package) {
            $portable = Copy-Layout (Join-Path $stage 'portable\AppShelf')
            Add-PortableFiles $portable
            $zip = Join-Path $PSScriptRoot "artifacts\AppShelf-$version-win-x64.zip"
            Compress-Archive -LiteralPath $portable -DestinationPath $zip -CompressionLevel Optimal -Force
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
        if ($Msix) {
            $publishTarget = Copy-Layout (Join-Path $stage 'msix\AppShelf')
            $kit = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
                Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
            if (-not $kit) { throw 'makeappx.exe not found. Install the Windows SDK.' }
            $makeappx = $kit.FullName; $makepri = Join-Path $kit.DirectoryName 'makepri.exe'
            # MSIX versions have four numbers; the Store requires the last one to be 0.
            $packageVersion = ($version -replace '-.*$', '') + '.0'
            $manifest = (Get-Content -LiteralPath 'packaging\AppxManifest.xml' -Raw -Encoding UTF8).
                Replace('$IdentityName$', [Security.SecurityElement]::Escape($IdentityName)).
                Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($PublisherDisplayName)).
                Replace('$Publisher$', [Security.SecurityElement]::Escape($Publisher)).
                Replace('$Version$', $packageVersion)
            if ($manifest -match '\$[A-Za-z]+\$') { throw "Unfilled manifest value: $($Matches[0])" }
            [IO.File]::WriteAllText((Join-Path $publishTarget 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))
            # Logos are indexed on their own, so the .NET "ru" satellite folder is not mistaken for a language resource.
            $pri = Join-Path $stage 'pri'
            New-Item -ItemType Directory -Path (Join-Path $pri 'Assets') -Force | Out-Null
            Copy-Item -Path 'packaging\Assets\*.png' -Destination (Join-Path $pri 'Assets')
            Copy-Item -Path 'packaging\Assets\*.png' -Destination (New-Item -ItemType Directory -Path (Join-Path $publishTarget 'Assets') -Force)
            Copy-Item -LiteralPath (Join-Path $publishTarget 'AppxManifest.xml') -Destination $pri
            & $makepri createconfig /cf (Join-Path $stage 'priconfig.xml') /dq en-US /o | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed.' }
            & $makepri new /pr $pri /cf (Join-Path $stage 'priconfig.xml') /mn (Join-Path $pri 'AppxManifest.xml') /of (Join-Path $publishTarget 'resources.pri') /o | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'makepri new failed.' }
            $msixPath = Join-Path $PSScriptRoot "artifacts\AppShelf-$version-x64.msix"
            & $makeappx pack /d $publishTarget /p $msixPath /o | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }
            $hash = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash
            ($hash + '  ' + [IO.Path]::GetFileName($msixPath)) | Set-Content -LiteralPath ($msixPath + '.sha256') -Encoding ascii
            Write-Host "MSIX: $msixPath ($IdentityName, $packageVersion)"
            Write-Host "SHA256: $hash"
        }
    }
}
finally { Pop-Location }
