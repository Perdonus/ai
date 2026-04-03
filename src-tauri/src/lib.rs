mod automation;
mod config;
mod provider;
mod runtime_tools;
mod screen;
mod tools;
mod weather;
mod widgets;

use crate::{
    automation::{keyboard_action, keyboard_combo, KeyboardActionRequest, mouse_action, MouseActionRequest},
    config::{create_backup as make_config_backup, export_config as export_config_file, load_config as read_config_file, save_config as write_config_file, AppConfig, ModelRoute},
    provider::{AgentAction, AgentActionKind},
    runtime_tools::InstalledRuntimeTool,
    screen::ScreenCapture,
    tools::CommandResult,
    weather::WeatherSnapshot,
    widgets::InstalledWidget,
};
use serde::Serialize;
use serde_json::Value;
use std::sync::{Arc, Mutex};
use tauri::{
    menu::{Menu, MenuItem},
    tray::TrayIconBuilder,
    AppHandle, Manager, State, WindowEvent,
};
use tauri_plugin_autostart::MacosLauncher;
use tokio::sync::oneshot;
use uuid::Uuid;

#[derive(Default)]
struct TaskControl {
    cancel: Option<oneshot::Sender<()>>,
}

#[derive(Default)]
struct SharedState {
    config: Mutex<AppConfig>,
    logs: Mutex<Vec<String>>,
    active_task: Mutex<Option<String>>,
    weather: Mutex<Option<WeatherSnapshot>>,
    last_capture: Mutex<Option<ScreenCapture>>,
    task_control: Mutex<TaskControl>,
}

#[derive(Debug, Serialize)]
struct AgentStatus {
    active_task: Option<String>,
    logs: Vec<String>,
}

fn push_log(state: &SharedState, message: impl Into<String>) {
    let mut logs = state.logs.lock().expect("logs lock");
    logs.push(message.into());
    while logs.len() > 120 {
        logs.remove(0);
    }
}

fn log_tool_result(state: &SharedState, label: &str, result: &str) {
    push_log(state, format!("{label}: {result}"));
}

fn string_arg(args: &Value, key: &str) -> Result<String, String> {
    args.get(key)
        .and_then(Value::as_str)
        .map(ToString::to_string)
        .ok_or_else(|| format!("Missing string arg: {key}"))
}

fn string_vec_arg(args: &Value, key: &str) -> Vec<String> {
    args.get(key)
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .map(ToString::to_string)
                .collect()
        })
        .unwrap_or_default()
}

async fn execute_action(state: &SharedState, _config: &AppConfig, action: AgentAction) -> Result<bool, String> {
    match action.kind {
        AgentActionKind::Respond => {
            log_tool_result(state, "agent", &action.args.to_string());
            Ok(false)
        }
        AgentActionKind::CaptureScreen => {
            let capture = screen::capture_primary_screen().map_err(|e| e.to_string())?;
            log_tool_result(state, "capture_screen", &capture.path);
            *state.last_capture.lock().map_err(|e| e.to_string())? = Some(capture);
            Ok(false)
        }
        AgentActionKind::OpenPath => {
            let path = string_arg(&action.args, "path")?;
            tools::open_path(&path).await.map_err(|e| e.to_string())?;
            log_tool_result(state, "open_path", &path);
            Ok(false)
        }
        AgentActionKind::ReadFile => {
            let path = string_arg(&action.args, "path")?;
            let content = tools::read_file(&path).map_err(|e| e.to_string())?;
            log_tool_result(state, "read_file", &format!("{path}\n{content}"));
            Ok(false)
        }
        AgentActionKind::WriteFile => {
            let path = string_arg(&action.args, "path")?;
            let content = string_arg(&action.args, "content")?;
            tools::write_file(&path, &content).map_err(|e| e.to_string())?;
            log_tool_result(state, "write_file", &path);
            Ok(false)
        }
        AgentActionKind::EditFile => {
            let path = string_arg(&action.args, "path")?;
            let find = string_arg(&action.args, "find")?;
            let replace = string_arg(&action.args, "replace")?;
            tools::edit_file(&path, &find, &replace).map_err(|e| e.to_string())?;
            log_tool_result(state, "edit_file", &path);
            Ok(false)
        }
        AgentActionKind::RunPowershell => {
            let script = string_arg(&action.args, "script")?;
            let result = tools::run_powershell(&script).await.map_err(|e| e.to_string())?;
            log_tool_result(state, "powershell", &format!("{}\n{}", result.stdout, result.stderr));
            Ok(false)
        }
        AgentActionKind::RunCommand => {
            let program = string_arg(&action.args, "program")?;
            let args = string_vec_arg(&action.args, "args");
            let cwd = action.args.get("cwd").and_then(Value::as_str);
            let result = tools::run_command(&program, &args, cwd).await.map_err(|e| e.to_string())?;
            log_tool_result(state, "run_command", &format!("{}\n{}", result.stdout, result.stderr));
            Ok(false)
        }
        AgentActionKind::CloneRepo => {
            let repo = string_arg(&action.args, "repo")?;
            let destination = string_arg(&action.args, "destination")?;
            let result = tools::clone_repo(&repo, &destination).await.map_err(|e| e.to_string())?;
            log_tool_result(state, "clone_repo", &format!("{}\n{}", result.stdout, result.stderr));
            Ok(false)
        }
        AgentActionKind::GitCommand => {
            let args = string_vec_arg(&action.args, "args");
            let cwd = action.args.get("cwd").and_then(Value::as_str);
            let result = tools::git_command(&args, cwd).await.map_err(|e| e.to_string())?;
            log_tool_result(state, "git", &format!("{}\n{}", result.stdout, result.stderr));
            Ok(false)
        }
        AgentActionKind::DownloadFile => {
            let url = string_arg(&action.args, "url")?;
            let destination = string_arg(&action.args, "destination")?;
            tools::download_file(&url, &destination).await.map_err(|e| e.to_string())?;
            log_tool_result(state, "download_file", &destination);
            Ok(false)
        }
        AgentActionKind::InstallWidgetFromGithub => {
            let repo = action.args.get("repo").and_then(Value::as_str);
            let widget_name = string_arg(&action.args, "widget_name")?;
            let branch = action.args.get("branch").and_then(Value::as_str);
            let widget = widgets::install_widget_from_github(repo, &widget_name, branch)
                .await
                .map_err(|e| e.to_string())?;
            log_tool_result(state, "install_widget", &widget.manifest.name);
            Ok(false)
        }
        AgentActionKind::InstallToolFromGithub => {
            let tool_name = string_arg(&action.args, "tool_name")?;
            let repo = action.args.get("repo").and_then(Value::as_str);
            let branch = action.args.get("branch").and_then(Value::as_str);
            let tool = runtime_tools::install_tool_from_github(&tool_name, repo, branch)
                .await
                .map_err(|e| e.to_string())?;
            log_tool_result(state, "install_tool", &tool.manifest.name);
            Ok(false)
        }
        AgentActionKind::RunInstalledTool => {
            let tool_id = string_arg(&action.args, "tool_id")?;
            let args = action.args.get("args").cloned().unwrap_or_else(|| serde_json::json!({}));
            let result = runtime_tools::execute_tool(&tool_id, args)
                .await
                .map_err(|e| e.to_string())?;
            log_tool_result(state, "run_installed_tool", &format!("{}\n{}", result.stdout, result.stderr));
            Ok(false)
        }
        AgentActionKind::MouseAction => {
            let request: MouseActionRequest =
                serde_json::from_value(action.args).map_err(|e| e.to_string())?;
            mouse_action(request).map_err(|e| e.to_string())?;
            log_tool_result(state, "mouse_action", "ok");
            Ok(false)
        }
        AgentActionKind::KeyboardCombo => {
            let keys = string_vec_arg(&action.args, "keys");
            keyboard_combo(keys).map_err(|e| e.to_string())?;
            log_tool_result(state, "keyboard_combo", "ok");
            Ok(false)
        }
        AgentActionKind::KeyboardAction => {
            let request: KeyboardActionRequest =
                serde_json::from_value(action.args).map_err(|e| e.to_string())?;
            keyboard_action(request).map_err(|e| e.to_string())?;
            log_tool_result(state, "keyboard_action", "ok");
            Ok(false)
        }
        AgentActionKind::TypeText => {
            let text = string_arg(&action.args, "text")?;
            keyboard_action(KeyboardActionRequest {
                kind: automation::KeyboardActionKind::TypeText,
                keys: Vec::new(),
                text,
                duration_ms: 0,
            })
            .map_err(|e| e.to_string())?;
            log_tool_result(state, "type_text", "ok");
            Ok(false)
        }
        AgentActionKind::Finish => Ok(true),
    }
}

#[tauri::command]
fn load_config(state: State<'_, Arc<SharedState>>) -> Result<AppConfig, String> {
    Ok(state.config.lock().map_err(|e| e.to_string())?.clone())
}

#[tauri::command]
async fn list_models(route: ModelRoute) -> Result<Vec<provider::ModelOption>, String> {
    provider::list_models(&route).await.map_err(|e| e.to_string())
}

#[tauri::command]
fn save_config(state: State<'_, Arc<SharedState>>, config: AppConfig) -> Result<(), String> {
    write_config_file(&config).map_err(|e| e.to_string())?;
    *state.config.lock().map_err(|e| e.to_string())? = config;
    push_log(&state, "Config updated.");
    Ok(())
}

#[tauri::command]
fn backup_config() -> Result<String, String> {
    make_config_backup().map_err(|e| e.to_string())
}

#[tauri::command]
fn export_config() -> Result<String, String> {
    export_config_file().map_err(|e| e.to_string())
}

#[tauri::command]
fn agent_status(state: State<'_, Arc<SharedState>>) -> Result<AgentStatus, String> {
    Ok(AgentStatus {
        active_task: state.active_task.lock().map_err(|e| e.to_string())?.clone(),
        logs: state.logs.lock().map_err(|e| e.to_string())?.clone(),
    })
}

#[tauri::command]
fn capture_screen(state: State<'_, Arc<SharedState>>) -> Result<ScreenCapture, String> {
    let capture = screen::capture_primary_screen().map_err(|e| e.to_string())?;
    *state.last_capture.lock().map_err(|e| e.to_string())? = Some(capture.clone());
    Ok(capture)
}

#[tauri::command]
async fn run_task(
    app: AppHandle,
    state: State<'_, Arc<SharedState>>,
    prompt: String,
) -> Result<(), String> {
    let task_id = format!("task-{}", Uuid::new_v4());
    {
        let mut active = state.active_task.lock().map_err(|e| e.to_string())?;
        if active.is_some() {
            return Err("Another task is already running".to_string());
        }
        *active = Some(task_id.clone());
    }

    let config = state.config.lock().map_err(|e| e.to_string())?.clone();
    let shared = state.inner().clone();
    let (tx, mut rx) = oneshot::channel::<()>();
    state.task_control.lock().map_err(|e| e.to_string())?.cancel = Some(tx);
    push_log(&shared, format!("Starting {}", task_id));
    push_log(&shared, format!("User prompt: {}", prompt));

    tauri::async_runtime::spawn(async move {
        let mut history = Vec::new();
        for step in 0..config.max_steps {
            let screenshot_path = match screen::capture_primary_screen() {
                Ok(capture) => {
                    let path = capture.path.clone();
                    *shared.last_capture.lock().expect("capture lock") = Some(capture);
                    history.push(format!("step {} screenshot: {}", step + 1, path));
                    Some(path)
                }
                Err(error) => {
                    push_log(&shared, format!("Screen capture failed: {}", error));
                    None
                }
            };

            let result = tokio::select! {
                _ = &mut rx => Err(anyhow::anyhow!("Task cancelled")),
                response = provider::next_turn(&config, &prompt, &history, screenshot_path.as_deref()) => response,
            };

            match result {
                Ok(turn) => {
                    push_log(&shared, format!("step {} thought: {}", step + 1, turn.thought));
                    history.push(format!("step {} thought: {}", step + 1, turn.thought));
                    if turn.actions.is_empty() {
                        push_log(&shared, "No actions returned.");
                        break;
                    }

                    let mut finished = false;
                    for action in turn.actions {
                        match execute_action(&shared, &config, action).await {
                            Ok(done) => {
                                if done {
                                    finished = true;
                                    push_log(&shared, "Task finished by model.");
                                    break;
                                }
                            }
                            Err(error) => {
                                push_log(&shared, format!("Action failed: {}", error));
                                history.push(format!("action error: {}", error));
                                break;
                            }
                        }
                    }
                    if finished {
                        break;
                    }
                }
                Err(error) => {
                    push_log(&shared, format!("Task failed: {}", error));
                    break;
                }
            }
        }

        if let Some(window) = app.get_webview_window("main") {
            let _ = window.show();
            let _ = window.set_focus();
        }
        *shared.active_task.lock().expect("active task lock") = None;
        shared.task_control.lock().expect("task control lock").cancel = None;
    });

    Ok(())
}

#[tauri::command]
fn cancel_task(state: State<'_, Arc<SharedState>>) -> Result<(), String> {
    if let Some(cancel) = state.task_control.lock().map_err(|e| e.to_string())?.cancel.take() {
        let _ = cancel.send(());
        push_log(&state, "Cancellation requested.");
    }
    Ok(())
}

#[tauri::command]
async fn get_weather(state: State<'_, Arc<SharedState>>) -> Result<WeatherSnapshot, String> {
    let config = state.config.lock().map_err(|e| e.to_string())?.clone();
    match weather::fetch_weather(&config).await {
        Ok(snapshot) => {
            *state.weather.lock().map_err(|e| e.to_string())? = Some(snapshot.clone());
            Ok(snapshot)
        }
        Err(error) => {
            if let Some(cached) = weather::load_cached_weather() {
                push_log(&state, format!("Weather fallback cache used: {}", error));
                *state.weather.lock().map_err(|e| e.to_string())? = Some(cached.clone());
                Ok(cached)
            } else {
                Err(error.to_string())
            }
        }
    }
}

#[tauri::command]
fn list_widgets() -> Result<Vec<InstalledWidget>, String> {
    widgets::list_widgets().map_err(|e| e.to_string())
}

#[tauri::command]
fn list_tools() -> Result<Vec<InstalledRuntimeTool>, String> {
    runtime_tools::list_tools().map_err(|e| e.to_string())
}

#[tauri::command]
async fn install_widget_from_github(
    widget_name: String,
    branch: Option<String>,
) -> Result<InstalledWidget, String> {
    widgets::install_widget_from_github(None, &widget_name, branch.as_deref())
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn install_tool_from_github(
    tool_name: String,
    branch: Option<String>,
) -> Result<InstalledRuntimeTool, String> {
    runtime_tools::install_tool_from_github(&tool_name, None, branch.as_deref())
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn run_installed_tool(tool_id: String, args: Value) -> Result<CommandResult, String> {
    runtime_tools::execute_tool(&tool_id, args)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn open_path(path: String) -> Result<(), String> {
    tools::open_path(&path).await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn run_powershell(script: String) -> Result<CommandResult, String> {
    tools::run_powershell(&script).await.map_err(|e| e.to_string())
}

#[tauri::command]
async fn run_command(program: String, args: Vec<String>, cwd: Option<String>) -> Result<CommandResult, String> {
    tools::run_command(&program, &args, cwd.as_deref())
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn git_command(args: Vec<String>, cwd: Option<String>) -> Result<CommandResult, String> {
    tools::git_command(&args, cwd.as_deref())
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn clone_repo(repo: String, destination: String) -> Result<CommandResult, String> {
    tools::clone_repo(&repo, &destination)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn read_file(path: String) -> Result<String, String> {
    tools::read_file(&path).map_err(|e| e.to_string())
}

#[tauri::command]
fn write_file(path: String, content: String) -> Result<(), String> {
    tools::write_file(&path, &content).map_err(|e| e.to_string())
}

#[tauri::command]
fn edit_file(path: String, find: String, replace: String) -> Result<(), String> {
    tools::edit_file(&path, &find, &replace).map_err(|e| e.to_string())
}

#[tauri::command]
async fn download_file(url: String, destination: String) -> Result<(), String> {
    tools::download_file(&url, &destination)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn mouse_action_cmd(request: MouseActionRequest) -> Result<(), String> {
    mouse_action(request).map_err(|e| e.to_string())
}

#[tauri::command]
fn keyboard_combo_cmd(keys: Vec<String>) -> Result<(), String> {
    keyboard_combo(keys).map_err(|e| e.to_string())
}

#[tauri::command]
fn keyboard_action_cmd(request: KeyboardActionRequest) -> Result<(), String> {
    keyboard_action(request).map_err(|e| e.to_string())
}

fn build_tray(app: &AppHandle) -> tauri::Result<()> {
    let open = MenuItem::with_id(app, "open", "Open", true, None::<&str>)?;
    let settings = MenuItem::with_id(app, "settings", "Settings", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&open, &settings, &quit])?;

    let mut builder = TrayIconBuilder::new().menu(&menu);
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open" | "settings" => {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.show();
                    let _ = window.set_focus();
                }
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .build(app)?;
    Ok(())
}

pub fn run() {
    let initial_config = read_config_file().unwrap_or_default();
    let state = Arc::new(SharedState {
        config: Mutex::new(initial_config),
        ..Default::default()
    });

    tauri::Builder::default()
        .manage(state)
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_autostart::init(MacosLauncher::LaunchAgent, None))
        .setup(|app| {
            build_tray(app.handle())?;
            app.handle().plugin(
                tauri_plugin_global_shortcut::Builder::new()
                    .with_shortcuts(["AltRight"])?
                    .with_handler(|app, _shortcut, event| {
                        use tauri_plugin_global_shortcut::ShortcutState;
                        if event.state == ShortcutState::Pressed {
                            if let Some(window) = app.get_webview_window("main") {
                                if window.is_visible().unwrap_or(true) {
                                    let _ = window.hide();
                                } else {
                                    let _ = window.show();
                                    let _ = window.set_focus();
                                }
                            }
                        }
                    })
                    .build(),
            )?;
            let window = app.get_webview_window("main").expect("main window");
            let _ = window.set_always_on_top(true);

            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![
            load_config,
            list_models,
            save_config,
            backup_config,
            export_config,
            agent_status,
            capture_screen,
            run_task,
            cancel_task,
            get_weather,
            list_widgets,
            list_tools,
            install_widget_from_github,
            install_tool_from_github,
            run_installed_tool,
            open_path,
            run_powershell,
            run_command,
            git_command,
            clone_repo,
            read_file,
            write_file,
            edit_file,
            download_file,
            mouse_action_cmd,
            keyboard_combo_cmd,
            keyboard_action_cmd
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
