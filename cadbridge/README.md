# CadBridge: install

This folder is everything a user needs. Copy it to the PC, or download the repository, then:

1. If this Windows user has never started the CAD, start it once and close it.
2. Double-click **`Install.cmd`** and answer the steps:
   1. which CAD programs to set up (AutoCAD 2021-2026, AutoCAD-based verticals, CAD&PILLAR 11; the installer finds them),
   2. which AI apps (Claude Code, Claude Desktop, Codex, Cursor, Windsurf, VS Code Copilot),
   3. close those programs (save your work first),
   4. it installs: anything missing (uv, Python, libraries) is downloaded automatically,
   5. done.
3. Start the CAD, open a drawing, start the AI app and ask: *"use cad_status, then list the layers of the open drawing"*.

To remove it, double-click **`Uninstall.cmd`**. The full documentation is in the [repository README](../README.md).

| Folder | Contents |
|---|---|
| `bin/net8/CadBridge.dll` | plugin for AutoCAD 2025+ and CAD&PILLAR 11 (.NET 8) |
| `bin/net48/CadBridge.dll` | plugin for AutoCAD 2021-2024 (.NET Framework 4.8) |
| `src/` | plugin source (`CadBridge.cs`), its build script, the MCP server (`cadbridge_mcp.py`) |
| `examples/` | the 2-bedroom house test (sketch, then blocks and landscape) |
