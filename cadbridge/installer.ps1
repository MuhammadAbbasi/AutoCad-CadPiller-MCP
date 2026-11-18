# CAD&PILLAR MCP - interactive installer / uninstaller (Windows PowerShell 5.1+, per Windows user, no admin).
# Normally started by double-clicking Install.cmd or Uninstall.cmd.
#   -Uninstall            remove everything this installer added
#   -Clients "a,b"        skip the question (claude-code, claude-desktop, codex, cursor, windsurf, vscode, all)
#   -Yes                  do not ask (assume yes, do not wait for apps to be closed)
param(
    [switch]$Uninstall,
    [string]$Clients = "",
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Name = 'cadpillar'
$DllDir = Join-Path $env:APPDATA 'CadBridge'
$PyDir = Join-Path $env:LOCALAPPDATA 'CadBridge'
$Py = Join-Path $PyDir 'venv\Scripts\python.exe'
$Server = Join-Path $PyDir 'cadbridge_mcp.py'
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Ask($q, $default = 'Y') {
    if ($Yes) { return $true }
    $a = Read-Host "$q [$(if ($default -eq 'Y') {'Y/n'} else {'y/N'})]"
    if ($a -eq '') { $a = $default }
    return $a -match '^[Yy]'
}
function Write-Utf8($path, $text) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}
function Backup($path) { if (Test-Path $path) { Copy-Item $path "$path.bak-$Name-$Stamp" } }

# ---------------------------------------------------------------- the AI apps
$ClaudeDesktopFiles = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json')
Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
    ForEach-Object { $ClaudeDesktopFiles += Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json' }

$Apps = [ordered]@{
    'claude-code'    = @{ Label = 'Claude Code (CLI / IDE)'; Found = [bool](Get-Command claude -ErrorAction SilentlyContinue); Procs = @() }
    'claude-desktop' = @{ Label = 'Claude Desktop'; Found = (Test-Path (Join-Path $env:APPDATA 'Claude')) -or ($ClaudeDesktopFiles.Count -gt 1); Procs = @('Claude|WindowsApps\\Claude_|AnthropicClaude') }
    'codex'          = @{ Label = 'OpenAI Codex (CLI / IDE)'; Found = Test-Path (Join-Path $env:USERPROFILE '.codex'); Procs = @('codex|') }
    'cursor'         = @{ Label = 'Cursor'; Found = Test-Path (Join-Path $env:USERPROFILE '.cursor'); Procs = @('Cursor|') }
    'windsurf'       = @{ Label = 'Windsurf'; Found = Test-Path (Join-Path $env:USERPROFILE '.codeium\windsurf'); Procs = @('Windsurf|') }
    'vscode'         = @{ Label = 'VS Code (GitHub Copilot agent)'; Found = Test-Path (Join-Path $env:APPDATA 'Code\User'); Procs = @('Code|\\Microsoft VS Code\\') }
}

function Set-JsonServer($path, $root, $entry, [switch]$Remove) {
    # Adds/replaces (or removes) $root.$Name in a JSON config. Never rewrites a file it cannot parse.
    $obj = New-Object PSObject
    if (Test-Path $path) {
        $raw = [IO.File]::ReadAllText($path)
        if ($raw.Trim()) {
            try { $obj = $raw | ConvertFrom-Json } catch {
                Say "  ! $path is not plain JSON (comments?); left untouched. Add this by hand under `"$root`":" Yellow
                Say ("  `"$Name`": " + ($entry | ConvertTo-Json -Depth 10)) Yellow
                return
            }
        }
    } elseif ($Remove) { return }
    if (-not ($obj.PSObject.Properties.Name -contains $root)) {
        if ($Remove) { return }
        $obj | Add-Member -NotePropertyName $root -NotePropertyValue (New-Object PSObject)
    }
    $servers = $obj.$root
    if ($servers.PSObject.Properties.Name -contains $Name) { $servers.PSObject.Properties.Remove($Name) }
    if (-not $Remove) { $servers | Add-Member -NotePropertyName $Name -NotePropertyValue $entry }
    Backup $path
    Write-Utf8 $path ($obj | ConvertTo-Json -Depth 50)
    Say "  - $path" DarkGray
}

function Set-CodexToml([switch]$Remove) {
    $path = Join-Path $env:USERPROFILE '.codex\config.toml'
    $lines = @(); if (Test-Path $path) { $lines = @(Get-Content $path) } elseif ($Remove) { return }
    $out = New-Object System.Collections.Generic.List[string]; $skip = $false
    foreach ($l in $lines) {
        if ($l -match '^\s*\[') { $skip = $l -match "^\s*\[mcp_servers\.$Name(\.|\])" }
        if (-not $skip) { $out.Add($l) }
    }
    while ($out.Count -and -not $out[$out.Count - 1].Trim()) { $out.RemoveAt($out.Count - 1) }
    if (-not $Remove) {
        $out.Add(''); $out.Add("[mcp_servers.$Name]"); $out.Add("command = '$Py'"); $out.Add("args = ['$Server']")
        $out.Add('startup_timeout_sec = 60')
    }
    Backup $path
    Write-Utf8 $path (($out -join "`r`n") + "`r`n")
    Say "  - $path" DarkGray
}

function Configure($key, [switch]$Remove) {
    $stdio = [ordered]@{ command = $Py; args = @($Server) }
    switch ($key) {
        'claude-code' {
            if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { Say '  ! claude CLI not found; skipped' Yellow; return }
            cmd /c "claude mcp remove $Name -s user >nul 2>&1"
            if (-not $Remove) { claude mcp add $Name -s user -- $Py $Server | Out-Null }
            Say '  - claude mcp (user scope)' DarkGray
        }
        'claude-desktop' {
            foreach ($f in $ClaudeDesktopFiles) {
                if ((Test-Path (Split-Path -Parent $f)) -or $f -eq $ClaudeDesktopFiles[0]) { Set-JsonServer $f 'mcpServers' $stdio -Remove:$Remove }
            }
        }
        'codex' { Set-CodexToml -Remove:$Remove }
        'cursor' { Set-JsonServer (Join-Path $env:USERPROFILE '.cursor\mcp.json') 'mcpServers' $stdio -Remove:$Remove }
        'windsurf' { Set-JsonServer (Join-Path $env:USERPROFILE '.codeium\windsurf\mcp_config.json') 'mcpServers' $stdio -Remove:$Remove }
        'vscode' {
            $e = [ordered]@{ type = 'stdio'; command = $Py; args = @($Server) }
            Set-JsonServer (Join-Path $env:APPDATA 'Code\User\mcp.json') 'servers' $e -Remove:$Remove
        }
    }
}

function Wait-Closed($procs) {
    # $procs: "ProcessName|path regex" (empty regex = any path). Claude Desktop and the Claude Code CLI
    # are both claude.exe, so they are told apart by where the exe lives.
    while ($true) {
        $running = @(foreach ($p in $procs) {
            $n, $rx = $p -split '\|', 2
            Get-Process -Name $n -ErrorAction SilentlyContinue |
                Where-Object { -not $rx -or ($_.Path -and $_.Path -match $rx) } | ForEach-Object { $_.ProcessName }
        }) | Select-Object -Unique
        if (-not $running) { return }
        Say ("  Still running: " + ($running -join ', ')) Yellow
        if ($running -contains 'cadp' -and $Yes) { throw 'CAD&PILLAR must be closed (the plugin file is in use).' }
        if ($Yes) { Say '  (continuing; restart those apps afterwards)' Yellow; return }
        $a = Read-Host '  Close them (save your work first), then press Enter.  S = skip'
        if ($a -match '^[Ss]') {
            if ($running -contains 'cadp') { throw 'CAD&PILLAR must be closed (the plugin file is in use).' }
            return
        }
    }
}

# ---------------------------------------------------------------- CAD&PILLAR
function Find-Cad {
    $found = @()
    $root = 'HKLM:\SOFTWARE\Digicorp Ingegneria\CADPILLAR'
    if (Test-Path $root) {
        Get-ChildItem $root | ForEach-Object { Get-ChildItem $_.PSPath } |
            Where-Object { $_.PSChildName -match '^cadp-\d+:\d+$' } | ForEach-Object {
                $p = Get-ItemProperty $_.PSPath
                if ($p.Location) {
                    $rel = $_.Name -replace '^HKEY_LOCAL_MACHINE\\', ''
                    $found += [pscustomobject]@{ Name = $p.ProductName; Location = $p.Location; UserKey = "HKCU:\$rel" }
                }
            }
    }
    return $found
}

Say ''
Say '  CAD&PILLAR MCP - lets AI agents edit the drawing open in CAD&PILLAR (live DWG, no DXF)' Cyan
Say '  ------------------------------------------------------------------------------------' Cyan

$cads = @(Find-Cad)
if (-not $cads) { Say '  CAD&PILLAR was not found on this PC.' Red; exit 1 }
$cad = $cads[0]
if ($cads.Count -gt 1) {
    for ($i = 0; $i -lt $cads.Count; $i++) { Say "  $($i + 1)) $($cads[$i].Name)  $($cads[$i].Location)" }
    $cad = $cads[[int](Read-Host '  Which one') - 1]
}
Say "  CAD: $($cad.Name)  ($($cad.Location))"

# ---------------------------------------------------------------- which apps
$keys = @($Apps.Keys)
if ($Clients) {
    $chosen = if ($Clients -eq 'all') { $keys } else { @($Clients -split '[,\s]+' | Where-Object { $_ }) }
} else {
    Say ''
    Say ($(if ($Uninstall) { '  Remove the MCP from which apps?' } else { '  Install the MCP for which AI apps?' }))
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $a = $Apps[$keys[$i]]
        Say ("  {0}) {1,-34} {2}" -f ($i + 1), $a.Label, $(if ($a.Found) { '(found)' } else { '' }))
    }
    $default = (@(for ($i = 0; $i -lt $keys.Count; $i++) { if ($Apps[$keys[$i]].Found) { $i + 1 } }) -join ',')
    $ans = Read-Host "  Numbers separated by commas, A = all  [$default]"
    if (-not $ans) { $ans = $default }
    $chosen = if ($ans -match '^[Aa]') { $keys } else { @($ans -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { $keys[[int]$_ - 1] }) }
}
foreach ($c in $chosen) { if (-not $Apps.Contains($c)) { Say "  Unknown app: $c" Red; exit 1 } }
if (-not $chosen) { Say '  Nothing selected.' Yellow; exit 0 }
Say ('  Selected: ' + (($chosen | ForEach-Object { $Apps[$_].Label }) -join ', '))

# ---------------------------------------------------------------- close sessions
Say ''
Say '  Please close CAD&PILLAR and these apps now (save your work first):' Cyan
Say ('    CAD&PILLAR, ' + (($chosen | ForEach-Object { $Apps[$_].Label }) -join ', '))
Say '  Close every open session of them, including Claude Code / Codex terminals,' Cyan
Say '  so they pick up the new tools when restarted.' Cyan
if (-not $Yes) { Read-Host '  Press Enter when done' | Out-Null }
Wait-Closed (@('cadp|') + ($chosen | ForEach-Object { $Apps[$_].Procs }))

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    foreach ($c in $chosen) { Say "  $($Apps[$c].Label)"; Configure $c -Remove }
    if (Ask '  Also remove the CAD&PILLAR plugin and the Python environment?') {
        Remove-Item (Join-Path $cad.UserKey 'Applications\CadBridge') -Recurse -ErrorAction SilentlyContinue
        Remove-Item $DllDir, $PyDir -Recurse -Force -ErrorAction SilentlyContinue
        Say '  - plugin, autoload key and Python environment removed' DarkGray
    }
    Say ''; Say '  Uninstalled.' Green; exit 0
}

# ---------------------------------------------------------------- install
if (-not (Test-Path $cad.UserKey)) {
    Say '  CAD&PILLAR has never been started by this Windows user.' Yellow
    Say '  Start it once, close it, then run this installer again.' Yellow
    exit 1
}

Say ''; Say '  [1/4] CAD&PILLAR plugin' Cyan
New-Item -ItemType Directory -Force $DllDir | Out-Null
Copy-Item (Join-Path $Here 'bin\CadBridge.dll') $DllDir -Force
$appKey = Join-Path $cad.UserKey 'Applications\CadBridge'
New-Item $appKey -Force | Out-Null
Set-ItemProperty $appKey DESCRIPTION 'CadBridge: live-DWG pipe server for the cadpillar MCP'
Set-ItemProperty $appKey LOADER (Join-Path $DllDir 'CadBridge.dll')
New-ItemProperty $appKey LOADCTRLS -Value 2 -PropertyType DWord -Force | Out-Null   # load at startup
New-ItemProperty $appKey MANAGED -Value 1 -PropertyType DWord -Force | Out-Null     # .NET assembly
Say "  - $DllDir\CadBridge.dll, loads with every CAD&PILLAR start" DarkGray

Say ''; Say '  [2/4] Python environment' Cyan
$uv = Get-Command uv -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $uv) { $uv = @("$env:USERPROFILE\.local\bin\uv.exe", "$env:USERPROFILE\.cargo\bin\uv.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $uv) {
    if (-not (Ask '  uv (the Python manager from astral.sh) is needed. Download and install it now?')) { exit 1 }
    Invoke-RestMethod https://astral.sh/uv/install.ps1 | Invoke-Expression
    $uv = "$env:USERPROFILE\.local\bin\uv.exe"
}
New-Item -ItemType Directory -Force $PyDir | Out-Null
if (-not (Test-Path $Py)) { & $uv venv --python 3.12 (Join-Path $PyDir 'venv') | Out-Host }
& $uv pip install --quiet --python $Py 'fastmcp>=4.2,<5' pywin32 pillow | Out-Host
if ($LASTEXITCODE) { throw 'package install failed' }
Copy-Item (Join-Path $Here 'src\cadbridge_mcp.py') $Server -Force
Push-Location $PyDir
try { & $Py -c "import cadbridge_mcp" } finally { Pop-Location }
if ($LASTEXITCODE) { throw 'the MCP server does not start' }
Say "  - $Server (Python venv in $PyDir)" DarkGray

Say ''; Say '  [3/4] AI apps' Cyan
foreach ($c in $chosen) { Say "  $($Apps[$c].Label)"; Configure $c }

Say ''; Say '  [4/4] Done' Green
Say '  1. Start CAD&PILLAR and open a drawing.'
Say '  2. Start your AI app and ask, for example: "use cad_status, then list the layers of the open drawing".'
Say "  Plugin log: $DllDir\cadbridge.log    Uninstall: double-click Uninstall.cmd"
Say ''
