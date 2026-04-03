use crate::config::AppConfig;
use anyhow::{anyhow, Result};
use base64::{engine::general_purpose, Engine as _};
use reqwest::header::{AUTHORIZATION, CONTENT_TYPE};
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AgentActionKind {
    Respond,
    CaptureScreen,
    OpenPath,
    ReadFile,
    WriteFile,
    EditFile,
    RunPowershell,
    RunCommand,
    CloneRepo,
    GitCommand,
    DownloadFile,
    InstallWidgetFromGithub,
    MouseAction,
    KeyboardCombo,
    KeyboardAction,
    TypeText,
    Finish,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AgentAction {
    pub kind: AgentActionKind,
    #[serde(default)]
    pub args: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AgentTurn {
    pub thought: String,
    #[serde(default)]
    pub actions: Vec<AgentAction>,
}

#[derive(Debug, Serialize)]
struct ChatRequest {
    model: String,
    messages: Vec<Message>,
    response_format: Value,
}

#[derive(Debug, Serialize)]
struct Message {
    role: String,
    content: Vec<ContentPart>,
}

#[derive(Debug, Serialize)]
#[serde(tag = "type")]
enum ContentPart {
    #[serde(rename = "text")]
    Text { text: String },
    #[serde(rename = "image_url")]
    Image { image_url: ImageUrl },
}

#[derive(Debug, Serialize)]
struct ImageUrl {
    url: String,
}

#[derive(Debug, Deserialize)]
struct ChatResponse {
    choices: Vec<Choice>,
}

#[derive(Debug, Deserialize)]
struct Choice {
    message: ChoiceMessage,
}

#[derive(Debug, Deserialize)]
struct ChoiceMessage {
    content: String,
}

pub async fn next_turn(
    config: &AppConfig,
    prompt: &str,
    history: &[String],
    screenshot_path: Option<&str>,
) -> Result<AgentTurn> {
    if config.api_key.trim().is_empty() {
        return Err(anyhow!("API key is empty"));
    }

    let url = format!("{}/chat/completions", config.base_url.trim_end_matches('/'));
    let client = reqwest::Client::new();
    let history_text = if history.is_empty() {
        "No previous steps.".to_string()
    } else {
        history.join("\n")
    };

    let system_text = r#"You are a Windows desktop agent with broad local tools.
Return only JSON with keys:
- thought: short reasoning summary
- actions: array of actions
Each action must contain:
- kind: one of respond, capture_screen, open_path, read_file, write_file, edit_file, run_powershell, run_command, clone_repo, git_command, download_file, install_widget_from_github, mouse_action, keyboard_combo, keyboard_action, type_text, finish
- args: JSON object for the action
Rules:
- Prefer direct actions over plain text.
- Use finish when the task is complete.
- You can inspect the attached screenshot to reason about the current screen.
- Keep each turn progressive and small."#;

    let mut user_parts = vec![ContentPart::Text {
        text: format!("User goal:\n{}\n\nCurrent step history:\n{}", prompt, history_text),
    }];

    let model = if let Some(path) = screenshot_path {
        let bytes = std::fs::read(path)?;
        let encoded = general_purpose::STANDARD.encode(bytes);
        user_parts.push(ContentPart::Image {
            image_url: ImageUrl {
                url: format!("data:image/png;base64,{}", encoded),
            },
        });
        config.vision_model.clone()
    } else {
        config.text_model.clone()
    };

    let body = ChatRequest {
        model,
        messages: vec![
            Message {
                role: "system".to_string(),
                content: vec![ContentPart::Text {
                    text: system_text.to_string(),
                }],
            },
            Message {
                role: "user".to_string(),
                content: user_parts,
            },
        ],
        response_format: serde_json::json!({ "type": "json_object" }),
    };

    let response = client
        .post(url)
        .header(AUTHORIZATION, format!("Bearer {}", config.api_key))
        .header(CONTENT_TYPE, "application/json")
        .json(&body)
        .send()
        .await?
        .error_for_status()?;

    let payload: ChatResponse = response.json().await?;
    let content = payload
        .choices
        .into_iter()
        .next()
        .map(|choice| choice.message.content)
        .ok_or_else(|| anyhow!("Provider returned no choices"))?;
    Ok(serde_json::from_str(&content)?)
}
