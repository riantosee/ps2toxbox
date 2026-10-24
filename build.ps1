param([string]$OutputName = 'ps2toxbox.exe')
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'C# compiler for .NET Framework was not found. Install Visual Studio Build Tools or the .NET Framework developer tools.'
}

$source = Join-Path $PSScriptRoot 'ps2toxbox.cs'
$nativeDll = Join-Path $PSScriptRoot 'vendor\ViGEmClient.dll'
$logo = Join-Path $PSScriptRoot 'assets\ps2toxbox-logo.png'
$icon = Join-Path $PSScriptRoot 'assets\ps2toxbox.ico'
foreach ($file in @($source, $nativeDll, $logo, $icon)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required build file is missing: $file" }
}

$output = Join-Path $PSScriptRoot $OutputName
& $compiler /nologo /platform:x64 /target:winexe "/out:$output" "/win32icon:$icon" `
    "/resource:$nativeDll,ps2toxbox.ViGEmClient.dll" "/resource:$logo,ps2toxbox.Logo.png" `
    "/resource:$icon,ps2toxbox.AppIcon.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $source
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Write-Output "Built single-file app: $output"
