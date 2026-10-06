# Desktop AI Agent

Windows desktop agent running a native WinUI shell on a fully local vision-language model.

## Current app direction

- Native WinUI launcher instead of a webview shell
- Compact top-right prompt panel
- Global hotkey on `Right Ctrl`
- Auto-focus into the input field on open
- Hide with animation when focus is lost
- Separate native settings window opened by agent command
- **Local model only** — no providers, no API keys, no rate limits

## Local model runtime

The agent talks to [koboldcpp](https://github.com/LostRuins/koboldcpp) over its OpenAI
compatible endpoint and starts the server itself.

| Piece | Value |
| --- | --- |
| Model | `EvoCUA-8B-20260105` Q4_K_S (Qwen3-VL-8B fine-tune, `qwen3vl` arch) |
| Vision | `mmproj-Evocua-F16.gguf` |
| Served by | `koboldcpp-nocuda.exe` + `--usevulkan` |
| Endpoint | `http://127.0.0.1:5002/v1` |

EvoCUA is the top open-source computer-use agent on OSWorld (46.1% for the 8B checkpoint).
The shell drives it with its native paper contract — the `S2` prompt style, where every step
is an `Action:` line plus a single `<tool_call>` block for the `computer_use` function, and
coordinates arrive on a relative `0..999` grid.

### Why this fits an 8 GB Radeon RX 570

- Model 4.80 GB + projector 1.16 GB ≈ **5.96 GB resident**
- `--gpulayers 999` pins every layer to the GPU, so nothing spills into system RAM
- `--contextsize 8192` + `--quantkv q8_0` keeps the cache around **0.3 GB**
- `--image_max_pixels 1310720` caps screenshot prefill cost, which dominates step time on
  a Polaris card
- The `0..999` coordinate grid is resolution independent, so a downscaled screenshot does
  not shift where the agent clicks

If koboldcpp reports `offloaded X/Y layers`, and `X` is less than `Y`, the model did not fit:
lower `context_size`, turn on `mmproj_on_cpu`, or use a smaller quant.

### Getting the model

```bat
native\support\download-evocua-model.bat
```

That pulls both GGUF files into `%USERPROFILE%\llm\models`. To run the model by hand
instead of letting the app manage it, use `native\support\start_evocua.bat`.

## Long tasks

The model itself only sees one screenshot plus the last actions, which is not enough to carry
data between applications. The shell closes that gap without touching EvoCUA's action space:

- **Session notepad.** When the agent selects text and presses `ctrl+c`, the shell reads the
  clipboard and appends it to a notepad that is re-injected on every step. That is how data
  collected from one app survives until it is pasted into another. Capped at 4000 characters,
  oldest entries dropped first.
- **Screenshot file per step.** Every step writes its PNG to
  `%TEMP%\DesktopAIAgent\screenshots\step-NNN.png` and the path is stated in the prompt, so the
  agent can attach a picture by typing the path into a file dialog. Old files are pruned.
- **Unstick recovery.** When the model repeats the same action, the shell presses `ESC`, then
  focuses a window whose title matches a word from the task, or clicks the notification area
  chevron so a tray-minimised window becomes visible. Up to three recoveries per task.
- **Task start focus.** If the task names something that is already open, that window is
  brought to the front before the first screenshot.

Context budget with `context_size` 8192: system prompt about 1600 tokens, screenshot about
1300, notepad up to about 2000, actions a few hundred. Enabling `use_ocr_hints` adds the window
geometry and OCR dump on top, so raise `context_size` to 12288 if you turn it on.

## Native shell

Main project:

- `native/AgentShell/AgentShell.csproj`

Key windows:

- `native/AgentShell/LauncherWindow.xaml`
- `native/AgentShell/SettingsWindow.xaml`

Core native services:

- `native/AgentShell/Services/LocalKoboldService.cs` — koboldcpp process lifecycle and autodiscovery
- `native/AgentShell/Services/LocalRuntimeLocator` — finds the runtime, model and projector
- `native/AgentShell/Services/AgentChatService.cs` — local multimodal chat requests
- `native/AgentShell/Services/EvoCuaS2.cs` — EvoCUA prompt contract and action parsing
- `native/AgentShell/Services/AgentLoopService.cs` — one action per step, with repetition guards
- `native/AgentShell/Services/InputAutomationService.cs` — mouse, keyboard and clipboard
- `native/AgentShell/Services/ScreenCaptureService.cs` + `QwenImageProcessor.cs` — screenshots
- `native/AgentShell/Services/ShellConfigService.cs` — config, backups and export
- `native/AgentShell/Services/GlobalHotkeyService.cs`
- `native/AgentShell/Services/WindowVisualService.cs`

## Settings structure

The settings UI is organized into:

- Локальная модель
- Тулзы
- Виджеты

Everything is stored in `%APPDATA%\DesktopAIAgent\native-shell.json`. Paths, context size,
GPU layers, KV quant, port and thread count live there; leaving the paths empty enables
autodiscovery.

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

## Requirements

- .NET SDK `8.0.408`
- Windows 10 19041 or newer
- A Vulkan capable GPU with enough VRAM for the model and its projector
