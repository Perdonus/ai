use serde::{Deserialize, Serialize};
use std::{fs, path::PathBuf};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppConfig {
    pub base_url: String,
    pub api_key: String,
    pub text_model: String,
    pub vision_model: String,
    pub weather_location: String,
    pub weather_units: Units,
    pub max_steps: u8,
    pub confirmation_policy: ConfirmationPolicy,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Units {
    Metric,
    Imperial,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ConfirmationPolicy {
    Auto,
    Ask,
    Block,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            base_url: "https://sosiskibot.ru/v1".to_string(),
            api_key: String::new(),
            text_model: "gpt-4o-mini".to_string(),
            vision_model: "gpt-4o".to_string(),
            weather_location: "Moscow".to_string(),
            weather_units: Units::Metric,
            max_steps: 12,
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

fn config_path() -> PathBuf {
    app_data_dir().join("config.json")
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
    fs::write(path, serde_json::to_vec_pretty(config)?)?;
    Ok(())
}
