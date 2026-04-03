use crate::config::{AppConfig, ModelRoute, ProviderKind};
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

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelOption {
    pub id: String,
    pub label: String,
}

#[derive(Debug, Serialize)]
struct OpenAiRequest {
    model: String,
    messages: Vec<OpenAiMessage>,
    response_format: Value,
}

#[derive(Debug, Serialize)]
struct OpenAiMessage {
    role: String,
    content: Value,
}

#[derive(Debug, Deserialize)]
struct OpenAiResponse {
    choices: Vec<OpenAiChoice>,
}

#[derive(Debug, Deserialize)]
struct OpenAiChoice {
    message: OpenAiChoiceMessage,
}

#[derive(Debug, Deserialize)]
struct OpenAiChoiceMessage {
    content: Value,
}

#[derive(Debug, Serialize)]
struct GeminiRequest {
    contents: Vec<GeminiContent>,
    generation_config: GeminiGenerationConfig,
}

#[derive(Debug, Serialize)]
struct GeminiGenerationConfig {
    response_mime_type: String,
}

#[derive(Debug, Serialize)]
struct GeminiContent {
    role: String,
    parts: Vec<GeminiPart>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "snake_case")]
enum GeminiPart {
    Text { text: String },
    InlineData { mime_type: String, data: String },
}

#[derive(Debug, Deserialize)]
struct GeminiResponse {
    candidates: Option<Vec<GeminiCandidate>>,
}

#[derive(Debug, Deserialize)]
struct GeminiCandidate {
    content: Option<GeminiCandidateContent>,
}

#[derive(Debug, Deserialize)]
struct GeminiCandidateContent {
    parts: Option<Vec<GeminiCandidatePart>>,
}

#[derive(Debug, Deserialize)]
struct GeminiCandidatePart {
    text: Option<String>,
}

#[derive(Debug, Deserialize)]
struct OpenAiModelsResponse {
    data: Vec<OpenAiModel>,
}

#[derive(Debug, Deserialize)]
struct OpenAiModel {
    id: String,
}

#[derive(Debug, Deserialize)]
struct GeminiModelsResponse {
    models: Option<Vec<GeminiModel>>,
}

#[derive(Debug, Deserialize)]
struct GeminiModel {
    name: String,
    display_name: Option<String>,
}

#[derive(Debug, Deserialize)]
struct HuggingFaceModel {
    id: String,
}

fn route_for_vision(config: &AppConfig) -> &ModelRoute {
    if config.use_separate_vision && !config.vision_route.model.trim().is_empty() {
        &config.vision_route
    } else {
        &config.text_route
    }
}

fn route_for_ocr(config: &AppConfig) -> &ModelRoute {
    if config.use_separate_ocr && !config.ocr_route.model.trim().is_empty() {
        &config.ocr_route
    } else if config.use_separate_vision && !config.vision_route.model.trim().is_empty() {
        &config.vision_route
    } else {
        &config.text_route
    }
}

fn ensure_route(route: &ModelRoute) -> Result<()> {
    if route.api_key.trim().is_empty() {
        return Err(anyhow!("API key is empty"));
    }
    if route.model.trim().is_empty() {
        return Err(anyhow!("Model is empty"));
    }
    Ok(())
}

fn openai_base_url(route: &ModelRoute) -> String {
    let fallback = match route.provider {
        ProviderKind::SosiskiBot => "https://sosiskibot.ru/v1",
        ProviderKind::OpenAi => "https://api.openai.com/v1",
        ProviderKind::OpenRouter => "https://openrouter.ai/api/v1",
        ProviderKind::Mistral => "https://api.mistral.ai/v1",
        ProviderKind::HuggingFace => "https://router.huggingface.co/v1",
        ProviderKind::Gemini => "https://generativelanguage.googleapis.com/v1beta",
    };
    if route.base_url.trim().is_empty() {
        fallback.to_string()
    } else {
        route.base_url.trim_end_matches('/').to_string()
    }
}

fn image_data_url(path: &str) -> Result<String> {
    let bytes = std::fs::read(path)?;
    Ok(format!(
        "data:image/png;base64,{}",
        general_purpose::STANDARD.encode(bytes)
    ))
}

fn image_bytes_b64(path: &str) -> Result<String> {
    let bytes = std::fs::read(path)?;
    Ok(general_purpose::STANDARD.encode(bytes))
}

fn parse_openai_content(content: Value) -> Result<String> {
    match content {
        Value::String(text) => Ok(text),
        Value::Array(parts) => Ok(parts
            .into_iter()
            .filter_map(|part| part.get("text").and_then(Value::as_str).map(ToString::to_string))
            .collect::<Vec<_>>()
            .join("\n")),
        other => Ok(other.to_string()),
    }
}

async fn openai_compatible_json(route: &ModelRoute, system: &str, user: &str, image_path: Option<&str>) -> Result<String> {
    ensure_route(route)?;
    let client = reqwest::Client::new();
    let system_message = OpenAiMessage {
        role: "system".to_string(),
        content: Value::String(system.to_string()),
    };
    let user_content = if let Some(path) = image_path {
        serde_json::json!([
            { "type": "text", "text": user },
            { "type": "image_url", "image_url": { "url": image_data_url(path)? } }
        ])
    } else {
        Value::String(user.to_string())
    };
    let user_message = OpenAiMessage {
        role: "user".to_string(),
        content: user_content,
    };
    let body = OpenAiRequest {
        model: route.model.clone(),
        messages: vec![system_message, user_message],
        response_format: serde_json::json!({ "type": "json_object" }),
    };
    let url = format!("{}/chat/completions", openai_base_url(route));
    let response = client
        .post(url)
        .header(AUTHORIZATION, format!("Bearer {}", route.api_key))
        .header(CONTENT_TYPE, "application/json")
        .json(&body)
        .send()
        .await?
        .error_for_status()?;
    let payload: OpenAiResponse = response.json().await?;
    let content = payload
        .choices
        .into_iter()
        .next()
        .ok_or_else(|| anyhow!("Provider returned no choices"))?
        .message
        .content;
    parse_openai_content(content)
}

async fn openai_compatible_text(route: &ModelRoute, system: &str, user: &str, image_path: Option<&str>) -> Result<String> {
    ensure_route(route)?;
    let client = reqwest::Client::new();
    let system_message = OpenAiMessage {
        role: "system".to_string(),
        content: Value::String(system.to_string()),
    };
    let user_content = if let Some(path) = image_path {
        serde_json::json!([
            { "type": "text", "text": user },
            { "type": "image_url", "image_url": { "url": image_data_url(path)? } }
        ])
    } else {
        Value::String(user.to_string())
    };
    let user_message = OpenAiMessage {
        role: "user".to_string(),
        content: user_content,
    };
    let body = serde_json::json!({
        "model": route.model,
        "messages": [system_message, user_message]
    });
    let url = format!("{}/chat/completions", openai_base_url(route));
    let response = client
        .post(url)
        .header(AUTHORIZATION, format!("Bearer {}", route.api_key))
        .header(CONTENT_TYPE, "application/json")
        .json(&body)
        .send()
        .await?
        .error_for_status()?;
    let payload: OpenAiResponse = response.json().await?;
    let content = payload
        .choices
        .into_iter()
        .next()
        .ok_or_else(|| anyhow!("Provider returned no choices"))?
        .message
        .content;
    parse_openai_content(content)
}

async fn gemini_text(route: &ModelRoute, system: &str, user: &str, image_path: Option<&str>, expect_json: bool) -> Result<String> {
    ensure_route(route)?;
    let mut parts = vec![GeminiPart::Text {
        text: format!("System:\n{}\n\nUser:\n{}", system, user),
    }];
    if let Some(path) = image_path {
        parts.push(GeminiPart::InlineData {
            mime_type: "image/png".to_string(),
            data: image_bytes_b64(path)?,
        });
    }
    let body = GeminiRequest {
        contents: vec![GeminiContent {
            role: "user".to_string(),
            parts,
        }],
        generation_config: GeminiGenerationConfig {
            response_mime_type: if expect_json {
                "application/json".to_string()
            } else {
                "text/plain".to_string()
            },
        },
    };
    let base = openai_base_url(route);
    let url = format!("{}/models/{}:generateContent?key={}", base, route.model, route.api_key);
    let response = reqwest::Client::new()
        .post(url)
        .header(CONTENT_TYPE, "application/json")
        .json(&body)
        .send()
        .await?
        .error_for_status()?;
    let payload: GeminiResponse = response.json().await?;
    let text = payload
        .candidates
        .unwrap_or_default()
        .into_iter()
        .flat_map(|candidate| candidate.content.into_iter())
        .flat_map(|content| content.parts.unwrap_or_default().into_iter())
        .filter_map(|part| part.text)
        .collect::<Vec<_>>()
        .join("\n");
    if text.trim().is_empty() {
        return Err(anyhow!("Gemini returned empty content"));
    }
    Ok(text)
}

async fn chat_json(route: &ModelRoute, system: &str, user: &str, image_path: Option<&str>) -> Result<String> {
    match route.provider {
        ProviderKind::Gemini => gemini_text(route, system, user, image_path, true).await,
        ProviderKind::SosiskiBot
        | ProviderKind::OpenAi
        | ProviderKind::OpenRouter
        | ProviderKind::Mistral
        | ProviderKind::HuggingFace => openai_compatible_json(route, system, user, image_path).await,
    }
}

async fn plain_text(route: &ModelRoute, system: &str, user: &str, image_path: Option<&str>) -> Result<String> {
    match route.provider {
        ProviderKind::Gemini => gemini_text(route, system, user, image_path, false).await,
        ProviderKind::SosiskiBot
        | ProviderKind::OpenAi
        | ProviderKind::OpenRouter
        | ProviderKind::Mistral
        | ProviderKind::HuggingFace => openai_compatible_text(route, system, user, image_path).await,
    }
}

pub async fn list_models(route: &ModelRoute) -> Result<Vec<ModelOption>> {
    let client = reqwest::Client::new();
    let models = match route.provider {
        ProviderKind::Gemini => {
            let base = openai_base_url(route);
            let url = format!("{}/models?key={}", base, route.api_key);
            let payload: GeminiModelsResponse = client.get(url).send().await?.error_for_status()?.json().await?;
            payload
                .models
                .unwrap_or_default()
                .into_iter()
                .map(|model| ModelOption {
                    id: model.name.trim_start_matches("models/").to_string(),
                    label: model.display_name.unwrap_or_else(|| model.name.clone()),
                })
                .collect::<Vec<_>>()
        }
        ProviderKind::HuggingFace => {
            let mut request = client.get("https://huggingface.co/api/models?inference_provider=all&limit=100&sort=downloads");
            if !route.api_key.trim().is_empty() {
                request = request.header(AUTHORIZATION, format!("Bearer {}", route.api_key));
            }
            let payload: Vec<HuggingFaceModel> = request.send().await?.error_for_status()?.json().await?;
            payload
                .into_iter()
                .map(|model| ModelOption {
                    label: model.id.clone(),
                    id: model.id,
                })
                .collect::<Vec<_>>()
        }
        ProviderKind::SosiskiBot
        | ProviderKind::OpenAi
        | ProviderKind::OpenRouter
        | ProviderKind::Mistral => {
            let url = format!("{}/models", openai_base_url(route));
            let payload: OpenAiModelsResponse = client
                .get(url)
                .header(AUTHORIZATION, format!("Bearer {}", route.api_key))
                .send()
                .await?
                .error_for_status()?
                .json()
                .await?;
            payload
                .data
                .into_iter()
                .map(|model| ModelOption {
                    label: model.id.clone(),
                    id: model.id,
                })
                .collect::<Vec<_>>()
        }
    };

    let mut models = models;
    models.sort_by(|a, b| a.label.cmp(&b.label));
    Ok(models)
}

async fn extract_ocr_text(config: &AppConfig, screenshot_path: &str) -> Result<String> {
    let route = route_for_ocr(config);
    plain_text(
        route,
        "Extract all visible text from the screenshot. Return plain text only, preserving line breaks when useful.",
        "Read the screenshot as OCR text.",
        Some(screenshot_path),
    )
    .await
}

pub async fn next_turn(
    config: &AppConfig,
    prompt: &str,
    history: &[String],
    screenshot_path: Option<&str>,
) -> Result<AgentTurn> {
    let history_text = if history.is_empty() {
        "No previous steps.".to_string()
    } else {
        history.join("\n")
    };

    let mut user = format!("User goal:\n{}\n\nCurrent step history:\n{}", prompt, history_text);
    let route = if screenshot_path.is_some() {
        route_for_vision(config)
    } else {
        &config.text_route
    };

    if let Some(path) = screenshot_path {
        if let Ok(ocr_text) = extract_ocr_text(config, path).await {
            if !ocr_text.trim().is_empty() {
                user.push_str("\n\nOCR text from current screenshot:\n");
                user.push_str(&ocr_text);
            }
        }
    }

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
- Keep each turn progressive and small.
- If screen understanding is needed, use the screenshot and OCR text already provided.
- If text needs to be typed into UI, prefer type_text.
- If a click-drag or long hold is needed, use mouse_action with hold or drag."#;

    let content = chat_json(route, system_text, &user, screenshot_path).await?;
    Ok(serde_json::from_str(&content)?)
}
