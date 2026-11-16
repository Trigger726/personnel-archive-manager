param(
    [Parameter(Mandatory = $true)]
    [string]$OutputZip
)

$ErrorActionPreference = 'Stop'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputZip)
if ([IO.Path]::GetExtension($output) -ine '.zip') { throw 'Output must be a ZIP file.' }
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }

$staging = Join-Path ([IO.Path]::GetTempPath()) ('ArchiveManagerRelease-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $staging 'ArchiveManager'
$fonts = Join-Path $package 'Fonts'
$expectedFonts = @('SourceHanSansSC-Regular.otf', 'SourceHanSansSC-Medium.otf', 'LICENSE.txt')
[void][IO.Directory]::CreateDirectory($fonts)
try {
    foreach ($file in $expectedFonts) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Fonts/$file") -Destination $fonts
    }
    & (Join-Path $PSScriptRoot 'Build.ps1') -OutputPath (Join-Path $package 'ArchiveManager.exe') | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $output, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Output $output
}
finally {
    foreach ($file in $expectedFonts) {
        $path = Join-Path $fonts $file
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    $executable = Join-Path $package 'ArchiveManager.exe'
    if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable }
    Remove-Item -LiteralPath $fonts, $package, $staging
}
