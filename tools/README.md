# Runtime Tools

Runtime tools are loaded without rebuilding the app.

## Folder layout

Each tool lives in:

```text
tools/<tool-id>/
```

and must include `tool.json`.

## Manifest

```json
{
  "id": "open-terminal",
  "name": "Open Terminal",
  "description": "Open PowerShell in a target folder.",
  "kind": "command",
  "entry": "powershell",
  "args_template": ["-Command", "Write-Host {{text}}"],
  "version": "0.1.0"
}
```

Supported `kind` values:

- `command`
- `power_shell`

Supported placeholders:

- `{{tool_dir}}`
- any key from runtime `args`

The app installs remote tools from the `tools` branch of `Perdonus/ai`.
