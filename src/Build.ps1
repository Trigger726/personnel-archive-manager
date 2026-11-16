param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$wpf = Join-Path $framework 'WPF'
$output = if ($OutputPath) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath) }
else { Join-Path (Split-Path $PSScriptRoot -Parent) '人事档案文件管理.exe' }
$compilerArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/codepage:65001', '/langversion:5', "/out:$output"
    "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\WindowsBase.dll"
    "/reference:$wpf\UIAutomationTypes.dll", "/reference:$framework\System.Xaml.dll"
    "/resource:$PSScriptRoot\Theme.xaml,Theme.xaml", "/resource:$PSScriptRoot\Workspace.xaml,Workspace.xaml"
)
$compilerArgs += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName)
& (Join-Path $framework 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
Write-Output $output
