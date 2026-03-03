# AiyoPerps MCP Installer

Interactive installer for registering the AiyoPerps MCP bridge with supported AI agent hosts.

## Usage

```bash
npx -y @phidiassj/aiyoperps-mcp-installer
```

Optional MCP endpoint override:

```bash
npx -y @phidiassj/aiyoperps-mcp-installer --url http://127.0.0.1:5078/mcp
```

## Current host strategy

- `Codex`: patch `~/.codex/config.toml` by updating only the `mcp_servers.aiyoperps` block
- `Claude Code CLI`: use `claude mcp add-json` / `claude mcp remove`
- `Claude Desktop`: merge into the desktop JSON config
- `OpenClaw`: use `openclaw config set` / `openclaw config unset`

## Notes

- The installer creates `*.bak` backups before editing local config files.
- If a host is detected but not considered safe to modify, the installer reports it and skips automatic changes.
- For Codex, the installer writes `startup_timeout_sec = 60`.
- Before writing host config, the installer installs the bridge package into a stable local runtime directory instead of relying on `npx` at every startup.
- The generated MCP entry uses `node <local bridge script> --debug-log <local log path> --quiet --url <detected endpoint>`.
- The generated bridge debug log is stored in the local bridge runtime directory (for example `~/.aiyoperps/mcp-bridge/codex-debug.log` on Linux/WSL).
- Before installation, the installer probes candidate MCP URLs and only writes config if it finds a reachable endpoint.
