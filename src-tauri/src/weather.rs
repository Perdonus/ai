use crate::config::{AppConfig, Units};
use anyhow::{anyhow, Result};
use chrono::Utc;
use serde::{Deserialize, Serialize};
use std::fs;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WeatherSnapshot {
    pub location: String,
    pub temperature_c: Option<f32>,
    pub wind_speed_kmh: Option<f32>,
    pub description: String,
    pub updated_at: String,
    pub stale: bool,
}

#[derive(Debug, Deserialize)]
struct GeocodeResponse {
    results: Option<Vec<GeoPoint>>,
}

#[derive(Debug, Deserialize)]
struct GeoPoint {
    latitude: f32,
    longitude: f32,
    name: String,
    country: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ForecastResponse {
    current: CurrentWeather,
}

#[derive(Debug, Deserialize)]
struct CurrentWeather {
    temperature_2m: Option<f32>,
    wind_speed_10m: Option<f32>,
}

fn cache_path() -> std::path::PathBuf {
    let base = std::env::var("APPDATA")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|_| std::path::PathBuf::from("."));
    base.join("DesktopAIAgent").join("weather-cache.json")
}

pub async fn fetch_weather(config: &AppConfig) -> Result<WeatherSnapshot> {
    let geocode_url = format!(
        "https://geocoding-api.open-meteo.com/v1/search?name={}&count=1&language=ru&format=json",
        config.weather_location
    );
    let geo: GeocodeResponse = reqwest::get(geocode_url).await?.json().await?;
    let point = geo
        .results
        .and_then(|mut points| points.drain(..).next())
        .ok_or_else(|| anyhow!("Location not found"))?;

    let wind_unit = match config.weather_units {
        Units::Metric => "kmh",
        Units::Imperial => "mph",
    };

    let forecast_url = format!(
        "https://api.open-meteo.com/v1/forecast?latitude={}&longitude={}&current=temperature_2m,wind_speed_10m&wind_speed_unit={}",
        point.latitude, point.longitude, wind_unit
    );
    let forecast: ForecastResponse = reqwest::get(forecast_url).await?.json().await?;
    let snapshot = WeatherSnapshot {
        location: format!("{}, {}", point.name, point.country.unwrap_or_else(|| "Unknown".into())),
        temperature_c: forecast.current.temperature_2m,
        wind_speed_kmh: forecast.current.wind_speed_10m,
        description: "Open-Meteo current weather".to_string(),
        updated_at: Utc::now().to_rfc3339(),
        stale: false,
    };
    save_cache(&snapshot)?;
    Ok(snapshot)
}

pub fn load_cached_weather() -> Option<WeatherSnapshot> {
    let raw = fs::read_to_string(cache_path()).ok()?;
    let mut snapshot: WeatherSnapshot = serde_json::from_str(&raw).ok()?;
    snapshot.stale = true;
    Some(snapshot)
}

fn save_cache(snapshot: &WeatherSnapshot) -> Result<()> {
    let path = cache_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(path, serde_json::to_vec_pretty(snapshot)?)?;
    Ok(())
}

