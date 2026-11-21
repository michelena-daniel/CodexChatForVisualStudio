param(
    [string]$PackagePath = "$PSScriptRoot/../src/CodexForVS.Extension/bin/Release/net48/CodexForVS.Extension.vsix"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$source = [xml](Get-Content "$PSScriptRoot/../src/CodexForVS.Extension/source.extension.vsixmanifest" -Raw)
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
try {
    $manifest = $archive.GetEntry('extension.vsixmanifest')
    if (!$manifest) { throw 'The VSIX has no extension.vsixmanifest.' }
    $reader = [IO.StreamReader]::new($manifest.Open())
    try { $packaged = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }

    $identity = $packaged.PackageManifest.Metadata.Identity
    $expected = $source.PackageManifest.Metadata.Identity
    if (!$identity.Id -or $identity.Id -cne $expected.Id -or $identity.Version -cne $expected.Version) {
        throw "Expected $($expected.Id) $($expected.Version), found '$($identity.Id) $($identity.Version)' in the VSIX."
    }

    $asset = @($packaged.PackageManifest.Assets.Asset | Where-Object Type -eq 'Microsoft.VisualStudio.VsPackage')
    if ($asset.Count -ne 1 -or $asset[0].Path -notlike '*.pkgdef') {
        throw 'Expected a VSPackage asset pointing to a packaged .pkgdef.'
    }
    foreach ($path in @($asset[0].Path, 'CodexForVS.Extension.dll', 'CodexForVS.Client.dll', 'CodexForVS.Core.dll', 'Newtonsoft.Json.dll')) {
        if ($archive.GetEntry($path).Length -le 0) { throw "Missing or empty VSIX entry '$path'." }
    }
    Write-Output "Verified $($identity.Id) $($identity.Version) and packaged VSPackage files."
} finally {
    $archive.Dispose()
}
