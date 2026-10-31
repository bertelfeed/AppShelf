function Test-AppShelfStorePackage {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Settings,
        [Parameter(Mandatory)][string]$Version
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'The MSIX file is missing.' }
    if (-not (Test-Path -LiteralPath ($Path + '.sha256') -PathType Leaf)) { throw 'The MSIX checksum is missing.' }
    $expected = ((Get-Content -LiteralPath ($Path + '.sha256') -Raw).Trim() -split '\s+')[0]
    if ($expected -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $expected) { throw 'MSIX checksum mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('AppxManifest.xml', 'AppShelf.exe', 'AppShelf.Mcp.exe', 'resources.pri', 'ru/AppShelf.Core.resources.dll', 'LICENSE', 'licenses/NET-LICENSE.txt')) {
            if ($entries -notcontains $required) { throw "MSIX is missing $required." }
        }
        if ($entries | Where-Object { $_ -match '(^|/)(data/|portable\.flag$|\.env($|\.))|\.pdb$' }) { throw 'The MSIX contains profile, portable or debug files.' }
        $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $identity = $manifest.Package.Identity
        if ($identity.Name -cne $Settings.IdentityName -or $identity.Publisher -cne $Settings.Publisher -or $identity.Version -cne ($Version + '.0') -or $identity.ProcessorArchitecture -ne 'x64') {
            throw 'MSIX identity or version does not match this release configuration.'
        }
    }
    finally { $archive.Dispose() }
}
