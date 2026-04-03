use anyhow::{anyhow, Result};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{Cursor, Read},
    path::{Path, PathBuf},
};
use zip::ZipArchive;

pub const DEFAULT_WIDGET_REPO: &str = "https://github.com/Perdonus/ai";
pub const DEFAULT_WIDGET_BRANCH: &str = "widgets";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WidgetManifest {
    pub id: String,
    pub name: String,
    pub description: String,
    pub entry_html: String,
    #[serde(default)]
    pub version: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct InstalledWidget {
    pub manifest: WidgetManifest,
    pub folder: String,
    pub entry_file: String,
}

fn data_root() -> PathBuf {
    let base = std::env::var("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."));
    base.join("DesktopAIAgent")
}

pub fn widgets_root() -> PathBuf {
    data_root().join("widgets")
}

fn local_widget_roots() -> Vec<PathBuf> {
    vec![PathBuf::from("widgets"), widgets_root()]
}

fn widget_manifest_path(dir: &Path) -> PathBuf {
    dir.join("widget.json")
}

pub fn list_widgets() -> Result<Vec<InstalledWidget>> {
    let mut found = Vec::new();

    for root in local_widget_roots() {
        if !root.exists() {
            continue;
        }
        for entry in fs::read_dir(root)? {
            let entry = entry?;
            if !entry.file_type()?.is_dir() {
                continue;
            }
            let path = entry.path();
            let manifest_path = widget_manifest_path(&path);
            if !manifest_path.exists() {
                continue;
            }
            let raw = fs::read_to_string(&manifest_path)?;
            let manifest: WidgetManifest = serde_json::from_str(&raw)?;
            let entry_file = path.join(&manifest.entry_html);
            found.push(InstalledWidget {
                manifest,
                folder: path.to_string_lossy().to_string(),
                entry_file: entry_file.to_string_lossy().to_string(),
            });
        }
    }

    found.sort_by(|a, b| a.manifest.name.cmp(&b.manifest.name));
    Ok(found)
}

pub async fn install_widget_from_github(repo: Option<&str>, widget_name: &str, branch: Option<&str>) -> Result<InstalledWidget> {
    let branch_name = branch.unwrap_or(DEFAULT_WIDGET_BRANCH);
    let clean = repo
        .unwrap_or(DEFAULT_WIDGET_REPO)
        .trim_end_matches(".git")
        .trim_end_matches('/');
    let slug = clean
        .split("github.com/")
        .nth(1)
        .ok_or_else(|| anyhow!("Expected GitHub repo URL"))?;
    let zip_url = format!("https://codeload.github.com/{}/zip/refs/heads/{}", slug, branch_name);
    let bytes = reqwest::get(zip_url).await?.error_for_status()?.bytes().await?;
    let target_root = widgets_root().join(widget_name);
    fs::create_dir_all(&target_root)?;

    let reader = Cursor::new(bytes);
    let mut archive = ZipArchive::new(reader)?;
    let prefix = format!("{slug}-{branch_name}/widgets/{widget_name}/");

    for index in 0..archive.len() {
        let mut file = archive.by_index(index)?;
        let name = file.name().replace('\\', "/");
        if !name.starts_with(&prefix) || name.ends_with('/') {
            continue;
        }
        let relative = name.trim_start_matches(&prefix);
        let destination = target_root.join(relative);
        if let Some(parent) = destination.parent() {
            fs::create_dir_all(parent)?;
        }
        let mut buffer = Vec::new();
        file.read_to_end(&mut buffer)?;
        fs::write(destination, buffer)?;
    }

    let manifest_path = widget_manifest_path(&target_root);
    if !manifest_path.exists() {
        return Err(anyhow!("Widget was not found in repo widgets/{widget_name}"));
    }
    let raw = fs::read_to_string(&manifest_path)?;
    let manifest: WidgetManifest = serde_json::from_str(&raw)?;
    let entry_file = target_root.join(&manifest.entry_html);
    Ok(InstalledWidget {
        manifest,
        folder: target_root.to_string_lossy().to_string(),
        entry_file: entry_file.to_string_lossy().to_string(),
    })
}
