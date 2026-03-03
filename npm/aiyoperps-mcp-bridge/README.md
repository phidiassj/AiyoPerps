# AiyoPerps MCP Bridge

This package exposes a standard MCP `stdio` server that forwards requests to the local AiyoPerps HTTP MCP endpoint.

It supports both:
- classic `Content-Length` framed MCP stdio
- newline-delimited JSON messages used by current Codex MCP startup

## Usage

```bash
npx -y @phidiassj/aiyoperps-mcp-bridge
```

Custom endpoint:

```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --url http://127.0.0.1:5078/mcp
```

Use `--url` whenever your MCP endpoint is not the local default.

Or via environment variable:

```bash
AIYOPERPS_MCP_URL=http://127.0.0.1:5078/mcp npx -y @phidiassj/aiyoperps-mcp-bridge
```

Health check only:

```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --health-check --url http://127.0.0.1:5078/mcp
```

Startup validation before entering stdio mode:

```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --startup-ping --url http://127.0.0.1:5078/mcp
```

Write local diagnostics to a file:

```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --debug-log ~/.aiyoperps/mcp-bridge/codex-debug.log --quiet --url http://127.0.0.1:5078/mcp
```

## Typical MCP config

```json
{
  "mcpServers": {
    "aiyoperps": {
      "command": "npx",
      "args": [
        "-y",
        "@phidiassj/aiyoperps-mcp-bridge",
        "--debug-log",
        "~/.aiyoperps/mcp-bridge/codex-debug.log",
        "--quiet",
        "--url",
        "http://127.0.0.1:5078/mcp"
      ]
    }
  }
}
```
