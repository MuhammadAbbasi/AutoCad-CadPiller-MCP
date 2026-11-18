# Builds CadBridge.dll (net8.0) with the Roslyn that ships inside PowerShell 7 - no .NET SDK needed.
# Usage: pwsh bridge/build.ps1 [-CadDir "C:\Program Files\CADPILLAR 11.0"] [-Out "$env:APPDATA\CadBridge"]
param(
    [string]$CadDir = "C:\Program Files\CADPILLAR 11.0",
    [string]$Out = "$env:APPDATA\CadBridge"
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')

# AutoCAD 2025 hosts .NET 8: compile against the installed 8.0.x runtime, not pwsh's own.
$rt = Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory |
      Where-Object Name -like '8.0.*' | Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $rt) { throw '.NET 8 runtime not found' }

$refs = @(Get-ChildItem $rt.FullName -Filter *.dll | Where-Object {
    try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true } catch { $false } })
$wd = Join-Path (Split-Path (Split-Path $rt.FullName)) "Microsoft.WindowsDesktop.App\$($rt.Name)"
if (-not (Test-Path $wd)) { $wd = Get-ChildItem (Join-Path (Split-Path (Split-Path $rt.FullName)) 'Microsoft.WindowsDesktop.App') -Directory | Where-Object Name -like '8.0.*' | Select-Object -Last 1 -ExpandProperty FullName }
$refs += 'WindowsBase.dll' | ForEach-Object { Get-Item (Join-Path $wd $_) }
$refs += 'acmgd.dll','acdbmgd.dll','accoremgd.dll' | ForEach-Object { Get-Item (Join-Path $CadDir $_) }
[Microsoft.CodeAnalysis.MetadataReference[]]$mrefs = $refs | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_.FullName) }

$src = Get-Content (Join-Path $PSScriptRoot 'CadBridge.cs') -Raw
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($src)
$opts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$opts = $opts.WithPlatform([Microsoft.CodeAnalysis.Platform]::X64)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CadBridge', [Microsoft.CodeAnalysis.SyntaxTree[]]@($tree), $mrefs, $opts)

New-Item -ItemType Directory -Force $Out | Out-Null
$dll = Join-Path $Out 'CadBridge.dll'
$fs = [IO.File]::Create($dll)
try { $res = $comp.Emit($fs) } finally { $fs.Dispose() }
$res.Diagnostics | Where-Object { $_.Severity -ge 'Warning' } | ForEach-Object { $_.ToString() }
if (-not $res.Success) { Remove-Item $dll; throw 'build failed' }
"built $dll"
