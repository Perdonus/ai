use crate::config::app_data_dir;
use crate::tools;
use anyhow::{anyhow, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    collections::HashMap,
    fs,
    io::{Cursor, Read},
    path::{Path, PathBuf},
};
use zip::ZipArchive;

pub const DEFAULT_TOOLS_REPO: &str = "https://github.com/Perdonus/ai";
pub const DEFAULT_TOOLS_BRANCH: &str = "tools";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RuntimeToolKind {
    Command,
    PowerShell,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RuntimeToolManifest {
    pub id: String,
    pub name: String,
    pub description: String,
    pub kind: RuntimeToolKind,
    pub entry: String,
    #[serde(default)]
    pub args_template: Vec<String>,
    #[serde(default)]
    pub cwd: String,
    #[serde(default)]
    pub version: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct InstalledRuntimeTool {
    pub manifest: RuntimeToolManifest,
    pub folder: String,
}

fn tools_root() -> PathBuf {
    app_data_dir().join("tools")
}

fn local_tool_roots() -> Vec<PathBuf> {
    vec![PathBuf::from("tools"), tools_root()]
}

fn tool_manifest_path(dir: &Path) -> PathBuf {
    dir.join("tool.json")
}

fn replace_placeholders(input: &str, args: &Value, tool_dir: &Path) -> String {
    let mut value = input.replace("{{tool_dir}}", &tool_dir.to_string_lossy());
    if let Some(object) = args.as_object() {
        for (key, replacement) in object {
            let as_string = replacement
                .as_str()
                .map(ToString::to_string)
                .unwrap_or_else(|| replacement.to_string());
            value = value.replace(&format!("{{{{{key}}}}}"), &as_string);
        }
    }
    value
}

pub fn list_tools() -> Result<Vec<InstalledRuntimeTool>> {
    let mut found = Vec::new();
    for root in local_tool_roots() {
        if !root.exists() {
            continue;
        }
        for entry in fs::read_dir(root)? {
            let entry = entry?;
            if !entry.file_type()?.is_dir() {
                continue;
            }
            let path = entry.path();
            let manifest_path = tool_manifest_path(&path);
            if !manifest_path.exists() {
                continue;
            }
            let raw = fs::read_to_string(&manifest_path)?;
            let manifest: RuntimeToolManifest = serde_json::from_str(&raw)?;
            found.push(InstalledRuntimeTool {
                manifest,
                folder: path.to_string_lossy().to_string(),
            });
        }
    }
    found.sort_by(|a, b| a.manifest.name.cmp(&b.manifest.name));
    Ok(found)
}

pub async fn install_tool_from_github(tool_name: &str, repo: Option<&str>, branch: Option<&str>) -> Result<InstalledRuntimeTool> {
    let branch_name = branch.unwrap_or(DEFAULT_TOOLS_BRANCH);
    let clean = repo
        .unwrap_or(DEFAULT_TOOLS_REPO)
        .trim_end_matches(".git")
        .trim_end_matches('/');
    let slug = clean
        .split("github.com/")
        .nth(1)
        .ok_or_else(|| anyhow!("Expected GitHub repo URL"))?;
    let zip_url = format!("https://codeload.github.com/{}/zip/refs/heads/{}", slug, branch_name);
    let bytes = reqwest::get(zip_url).await?.error_for_status()?.bytes().await?;
    let target_root = tools_root().join(tool_name);
    fs::create_dir_all(&target_root)?;

    let reader = Cursor::new(bytes);
    let mut archive = ZipArchive::new(reader)?;
    let prefix = format!("{slug}-{branch_name}/tools/{tool_name}/");

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

    let raw = fs::read_to_string(tool_manifest_path(&target_root))?;
    let manifest: RuntimeToolManifest = serde_json::from_str(&raw)?;
    Ok(InstalledRuntimeTool {
        manifest,
        folder: target_root.to_string_lossy().to_string(),
    })
}

pub async fn execute_tool(tool_id: &str, args: Value) -> Result<tools::CommandResult> {
    let installed = list_tools()?
        .into_iter()
        .find(|tool| tool.manifest.id == tool_id)
        .ok_or_else(|| anyhow!("Tool not found: {}", tool_id))?;

    let tool_dir = PathBuf::from(&installed.folder);
    let cwd = if installed.manifest.cwd.trim().is_empty() {
        Some(installed.folder.as_str())
    } else {
        None
    };

    match installed.manifest.kind {
        RuntimeToolKind::Command => {
            let entry = replace_placeholders(&installed.manifest.entry, &args, &tool_dir);
            let rendered_args = installed
                .manifest
                .args_template
                .iter()
                .map(|item| replace_placeholders(item, &args, &tool_dir))
                .collect::<Vec<_>>();
            tools::run_command(&entry, &rendered_args, cwd).await
        }
        RuntimeToolKind::PowerShell => {
            let script_path = tool_dir.join(&installed.manifest.entry);
            let script = fs::read_to_string(script_path)?;
            let rendered = replace_placeholders(&script, &args, &tool_dir);
            tools::run_powershell(&rendered).await
        }
    }
}
