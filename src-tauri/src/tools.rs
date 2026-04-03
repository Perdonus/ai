use anyhow::{anyhow, Result};
use serde::Serialize;
use std::{path::Path, process::Stdio};
use tokio::process::Command;

#[derive(Debug, Clone, Serialize)]
pub struct CommandResult {
    pub success: bool,
    pub stdout: String,
    pub stderr: String,
}

pub async fn open_path(path: &str) -> Result<()> {
    if path.trim().is_empty() {
        return Err(anyhow!("Path is empty"));
    }
    Command::new("cmd")
        .args(["/C", "start", "", path])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()?;
    Ok(())
}

pub async fn run_powershell(script: &str) -> Result<CommandResult> {
    let output = Command::new("powershell")
        .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script])
        .output()
        .await?;
    Ok(CommandResult {
        success: output.status.success(),
        stdout: String::from_utf8_lossy(&output.stdout).to_string(),
        stderr: String::from_utf8_lossy(&output.stderr).to_string(),
    })
}

pub async fn run_command(program: &str, args: &[String], cwd: Option<&str>) -> Result<CommandResult> {
    if program.trim().is_empty() {
        return Err(anyhow!("Program is empty"));
    }
    let mut command = Command::new(program);
    command.args(args);
    if let Some(dir) = cwd {
        command.current_dir(dir);
    }
    let output = command.output().await?;
    Ok(CommandResult {
        success: output.status.success(),
        stdout: String::from_utf8_lossy(&output.stdout).to_string(),
        stderr: String::from_utf8_lossy(&output.stderr).to_string(),
    })
}

pub async fn git_command(args: &[String], cwd: Option<&str>) -> Result<CommandResult> {
    run_command("git", args, cwd).await
}

pub async fn clone_repo(repo: &str, destination: &str) -> Result<CommandResult> {
    let args = vec!["clone".to_string(), repo.to_string(), destination.to_string()];
    git_command(&args, None).await
}

pub async fn download_file(url: &str, destination: &str) -> Result<()> {
    let response = reqwest::get(url).await?.error_for_status()?;
    let bytes = response.bytes().await?;
    if let Some(parent) = Path::new(destination).parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(destination, bytes)?;
    Ok(())
}

pub fn read_file(path: &str) -> Result<String> {
    Ok(std::fs::read_to_string(path)?)
}

pub fn write_file(path: &str, content: &str) -> Result<()> {
    if let Some(parent) = Path::new(path).parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, content)?;
    Ok(())
}

pub fn edit_file(path: &str, find: &str, replace: &str) -> Result<()> {
    let content = std::fs::read_to_string(path)?;
    std::fs::write(path, content.replacen(find, replace, 1))?;
    Ok(())
}
