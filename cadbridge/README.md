# CAD&PILLAR MCP

Lets AI agents (Claude Code, Claude Desktop, Codex, Cursor, Windsurf, VS Code Copilot) **read and edit the drawing
open in CAD&PILLAR, live, as DWG**. There is no DXF conversion and no copy of the file.

## Install (about 2 minutes)

1. Copy this folder to the PC (anywhere).
2. Start CAD&PILLAR once if this Windows user has never used it, then close it.
3. Double-click **`Install.cmd`** and answer the questions:
   - which AI apps to set up (the ones found on the PC are suggested),
   - then close CAD&PILLAR and those apps (save your work first) and press Enter.
4. Start CAD&PILLAR, open a drawing, start your AI app and ask:
   *"use cad_status, then list the layers of the open drawing"*.

Requirements: Windows, CAD&PILLAR 11, internet access during install (downloads Python packages; if
[uv](https://docs.astral.sh/uv/) is missing it asks before installing it). No administrator rights needed.
Everything is installed for the current Windows user only.

To remove everything, double-click **`Uninstall.cmd`**.

## What the installer changes

| What | Where |
|---|---|
| CAD&PILLAR plugin (`CadBridge.dll`) | `%APPDATA%\CadBridge` |
| Load the plugin at every CAD&PILLAR start | `HKCU\SOFTWARE\Digicorp Ingegneria\CADPILLAR\<release>\cadp-<id>:<lang>\Applications\CadBridge` |
| MCP server + private Python environment | `%LOCALAPPDATA%\CadBridge` |
| Claude Code | `claude mcp add cadpillar -s user` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` (and the Microsoft Store copy) |
| Codex | `%USERPROFILE%\.codex\config.toml` |
| Cursor | `%USERPROFILE%\.cursor\mcp.json` |
| Windsurf | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` |
| VS Code (Copilot agent) | `%APPDATA%\Code\User\mcp.json` |

The installer only adds or replaces the `cadpillar` entry; other servers and settings stay. Every config file is
backed up first (`*.bak-cadpillar-<date>`), and a file it cannot parse (for example JSON with comments) is left
untouched: the installer prints the entry to paste by hand.

Any other MCP client works too. Use the command `%LOCALAPPDATA%\CadBridge\venv\Scripts\python.exe` with the
argument `%LOCALAPPDATA%\CadBridge\cadbridge_mcp.py`, over stdio.

## Tools the agent gets

| Tool | Does |
|---|---|
| `cad_status`, `cad_info`, `cad_list` | open drawings; layers, blocks, units, extents; entities with geometry |
| `cad_ops` | a batch of edits in **one all-or-nothing transaction**: layers, lines, polylines, circles, arcs, text, dimensions, hatches, block definitions, blocks imported from a DWG, inserts, move / copy / rotate / delete, properties, system variables |
| `cad_command` | any CAD&PILLAR command line (e.g. `_.ZOOM _E`) |
| `cad_open`, `cad_save` | open a DWG; save as DWG |
| `cad_screenshot` | picture of the CAD window, to show the user the result |

## How it works

CAD&PILLAR switches off AutoCAD's COM automation, so ordinary AutoCAD MCP servers cannot connect to it. The
plugin runs inside CAD&PILLAR and listens on a local named pipe that only the same Windows user can open
(`\\.\pipe\cadbridge`). It runs each request on CAD&PILLAR's main thread through its .NET API, directly on the
open DWG. The MCP server (`src/cadbridge_mcp.py`) turns MCP tool calls into those requests.

## Troubleshooting

- **"CadBridge pipe not reachable"**: CAD&PILLAR is not running, or the plugin did not load. Check
  `%APPDATA%\CadBridge\cadbridge.log`; it should end with `pipe server started`.
- **"timeout: CAD main thread busy"**: a dialog is open in CAD&PILLAR, or a command is waiting for input. Close it.
- **The app does not show the tools**: restart the AI app, since apps read their MCP config only at start.

## For developers

- `src/CadBridge.cs`: the plugin. `src/build.ps1` rebuilds `bin/CadBridge.dll` with the C# compiler bundled in
  PowerShell 7 (`pwsh src/build.ps1 -Out bin`), so no .NET SDK is needed. Close CAD&PILLAR before rebuilding.
- `src/cadbridge_mcp.py`: the MCP server.
- `examples/`: the 2-bedroom house test drawn on `test2.dwg` (sketch, then blocks and landscape).
