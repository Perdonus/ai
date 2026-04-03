# Desktop AI Agent

Windows desktop agent moving to a native WinUI shell.

## Current app direction

- Native WinUI launcher instead of a webview shell
- Compact top-right prompt panel
- Global hotkey on `Right Ctrl`
- Auto-focus into the input field on open
- Hide with animation when focus is lost
- Separate native settings window opened by agent command

## Native shell

Main project:

- `native/AgentShell/AgentShell.csproj`

Key windows:

- `native/AgentShell/LauncherWindow.xaml`
- `native/AgentShell/SettingsWindow.xaml`

Core native services:

- `native/AgentShell/Services/ShellConfigService.cs`
- `native/AgentShell/Services/ModelDiscoveryService.cs`
- `native/AgentShell/Services/RuntimeCatalogService.cs`
- `native/AgentShell/Services/GlobalHotkeyService.cs`
- `native/AgentShell/Services/WindowVisualService.cs`

## Settings structure

The native settings UI is organized into:

- Providers
- Models
- Tools
- Widgets

Provider settings only store API keys.

Supported provider presets:

- SosiskiBot
- OpenAI
- OpenRouter
- Gemini
- Mistral
- Hugging Face

Default SosiskiBot base URL:

- `https://sosiskibot.ru/api/v1`

## Runtime catalogs

Runtime widgets are loaded from:

- `Z:\ai\widgets`
- `%APPDATA%\DesktopAIAgent\widgets`

Runtime tools are loaded from:

- `Z:\ai\tools`
- `%APPDATA%\DesktopAIAgent\tools`

## GitHub workflows

Primary application workflow:

- `.github/workflows/build-native-shell.yml`

Separate package workflows:

- `.github/workflows/build-widgets.yml`
- `.github/workflows/build-tools.yml`
