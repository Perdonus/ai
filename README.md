# Desktop AI Agent

Windows-only desktop agent scaffold on `Tauri v2 + Rust + React`.

## Included

- `Right Alt` global shortcut shell for panel toggle
- tray/background app pattern
- settings UI for OpenAI-compatible API provider
- API client pointed at `https://sosiskibot.ru/v1` by default
- weather widget backed by Open-Meteo
- local tools for files, arbitrary commands, PowerShell and path opening
- step-by-step agent run loop with structured tool actions, cancellation and logs
- native screenshot capture to PNG on Windows
- native mouse and keyboard input via `SendInput`
- git/clone/download commands
- runtime widget loading from local folders or the `Perdonus/ai` `widgets` branch

## Missing before production

- dangerous action confirmation UX
- packaging icons and build verification
- build verification in this environment

## Expected config

The app persists config under `%APPDATA%/DesktopAIAgent/config.json`.

Main fields:

- `base_url`
- `api_key`
- `text_model`
- `vision_model`
- `weather_location`
- `max_steps`
- `confirmation_policy`

## Widget contract

Widgets are loaded at runtime from:

- `Z:\ai\widgets`
- `%APPDATA%/DesktopAIAgent/widgets`

Each widget must live in its own folder and contain:

- `widget.json`
- an HTML entry file referenced by `entry_html`

Example `widget.json`:

```json
{
  "id": "weather-pro",
  "name": "Weather Pro",
  "description": "Compact animated weather widget",
  "entry_html": "dist/index.html",
  "version": "0.1.0"
}
```

Runtime install expects widgets to be published in the `widgets` branch of `https://github.com/Perdonus/ai`, under:

```text
widgets/<widget-name>/
```

inside the target repository branch.
