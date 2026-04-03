use serde::{Deserialize, Serialize};
use chrono::Utc;
use std::{fs, path::PathBuf};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ProviderKind {
    SosiskiBot,
    OpenAi,
    OpenRouter,
    Gemini,
    Mistral,
    HuggingFace,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelRoute {
    pub provider: ProviderKind,
    pub base_url: String,
    pub api_key: String,
    pub model: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppConfig {
    #[serde(default = "default_text_route")]
    pub text_route: ModelRoute,
    #[serde(default)]
    pub use_separate_analysis: bool,
    #[serde(default = "default_analysis_route")]
    pub analysis_route: ModelRoute,
    #[serde(default)]
    pub use_separate_vision: bool,
    #[serde(default = "default_vision_route")]
    pub vision_route: ModelRoute,
    #[serde(default)]
    pub use_separate_ocr: bool,
    #[serde(default = "default_ocr_route")]
    pub ocr_route: ModelRoute,
    #[serde(default = "default_weather_location")]
    pub weather_location: String,
    #[serde(default)]
    pub weather_units: Units,
    #[serde(default = "default_max_steps")]
    pub max_steps: u8,
    #[serde(default)]
    pub confirmation_policy: ConfirmationPolicy,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum Units {
    #[default]
    Metric,
    Imperial,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum ConfirmationPolicy {
    Auto,
    #[default]
    Ask,
    Block,
}

fn default_weather_location() -> String {
    "Moscow".to_string()
}

fn default_max_steps() -> u8 {
    12
}

fn default_text_route() -> ModelRoute {
    ModelRoute {
        provider: ProviderKind::SosiskiBot,
        base_url: "https://sosiskibot.ru/v1".to_string(),
        api_key: String::new(),
        model: "gpt-4o-mini".to_string(),
    }
}

fn default_vision_route() -> ModelRoute {
    ModelRoute {
        provider: ProviderKind::SosiskiBot,
        base_url: "https://sosiskibot.ru/v1".to_string(),
        api_key: String::new(),
        model: String::new(),
    }
}

fn default_analysis_route() -> ModelRoute {
    ModelRoute {
        provider: ProviderKind::SosiskiBot,
        base_url: "https://sosiskibot.ru/v1".to_string(),
        api_key: String::new(),
        model: String::new(),
    }
}

fn default_ocr_route() -> ModelRoute {
    ModelRoute {
        provider: ProviderKind::SosiskiBot,
        base_url: "https://sosiskibot.ru/v1".to_string(),
        api_key: String::new(),
        model: String::new(),
    }
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            text_route: default_text_route(),
            use_separate_analysis: false,
            analysis_route: default_analysis_route(),
            use_separate_vision: false,
            vision_route: default_vision_route(),
            use_separate_ocr: false,
            ocr_route: default_ocr_route(),
            weather_location: default_weather_location(),
            weather_units: Units::Metric,
            max_steps: default_max_steps(),
            confirmation_policy: ConfirmationPolicy::Ask,
        }
    }
}

pub fn app_data_dir() -> PathBuf {
    let base = std::env::var("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."));
    base.join("DesktopAIAgent")
}

pub fn backups_dir() -> PathBuf {
    app_data_dir().join("backups")
}

pub fn exports_dir() -> PathBuf {
    app_data_dir().join("exports")
}

fn config_path() -> PathBuf {
    app_data_dir().join("config.json")
}

fn stamped_file(prefix: &str) -> String {
    format!("{}-{}.json", prefix, Utc::now().format("%Y%m%d-%H%M%S"))
}

pub fn load_config() -> anyhow::Result<AppConfig> {
    let path = config_path();
    if !path.exists() {
        return Ok(AppConfig::default());
    }
    let raw = fs::read_to_string(path)?;
    Ok(serde_json::from_str(&raw)?)
}

pub fn save_config(config: &AppConfig) -> anyhow::Result<()> {
    let path = config_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    if path.exists() {
        create_backup()?;
    }
    fs::write(path, serde_json::to_vec_pretty(config)?)?;
    Ok(())
}

pub fn create_backup() -> anyhow::Result<String> {
    let source = config_path();
    if !source.exists() {
        return Err(anyhow::anyhow!("Config file does not exist yet"));
    }
    let dir = backups_dir();
    fs::create_dir_all(&dir)?;
    let target = dir.join(stamped_file("config-backup"));
    fs::copy(&source, &target)?;
    Ok(target.to_string_lossy().to_string())
}

pub fn export_config() -> anyhow::Result<String> {
    let source = config_path();
    if !source.exists() {
        return Err(anyhow::anyhow!("Config file does not exist yet"));
    }
    let dir = exports_dir();
    fs::create_dir_all(&dir)?;
    let target = dir.join(stamped_file("config-export"));
    fs::copy(&source, &target)?;
    Ok(target.to_string_lossy().to_string())
}
