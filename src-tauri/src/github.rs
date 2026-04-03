use crate::config::AppConfig;
use anyhow::{anyhow, Result};
use reqwest::header::{ACCEPT, AUTHORIZATION, USER_AGENT};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GithubRepoRequest {
    pub name: String,
    #[serde(default)]
    pub private: bool,
    #[serde(default)]
    pub description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GithubRepoResponse {
    pub name: String,
    pub html_url: String,
    pub private: bool,
}

fn client(token: &str) -> reqwest::Client {
    reqwest::Client::builder()
        .build()
        .expect("github client")
}

fn auth_headers(request: reqwest::RequestBuilder, token: &str) -> reqwest::RequestBuilder {
    request
        .header(USER_AGENT, "desktop-ai-agent")
        .header(ACCEPT, "application/vnd.github+json")
        .header(AUTHORIZATION, format!("Bearer {}", token))
}

pub async fn create_repo(config: &AppConfig, payload: GithubRepoRequest) -> Result<GithubRepoResponse> {
    if config.github_token.trim().is_empty() {
        return Err(anyhow!("GitHub token is empty"));
    }
    let body = serde_json::json!({
        "name": payload.name,
        "private": payload.private,
        "description": payload.description,
        "auto_init": true
    });
    let response = auth_headers(
        client(&config.github_token).post("https://api.github.com/user/repos"),
        &config.github_token,
    )
    .json(&body)
    .send()
    .await?
    .error_for_status()?;
    Ok(response.json().await?)
}

pub async fn get_repo(config: &AppConfig, owner: &str, repo: &str) -> Result<GithubRepoResponse> {
    if config.github_token.trim().is_empty() {
        return Err(anyhow!("GitHub token is empty"));
    }
    let url = format!("https://api.github.com/repos/{owner}/{repo}");
    let response = auth_headers(client(&config.github_token).get(url), &config.github_token)
        .send()
        .await?
        .error_for_status()?;
    Ok(response.json().await?)
}

pub async fn dispatch_workflow(
    config: &AppConfig,
    owner: &str,
    repo: &str,
    workflow_id: &str,
    git_ref: &str,
) -> Result<()> {
    if config.github_token.trim().is_empty() {
        return Err(anyhow!("GitHub token is empty"));
    }
    let url = format!(
        "https://api.github.com/repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches"
    );
    let body = serde_json::json!({ "ref": git_ref });
    auth_headers(client(&config.github_token).post(url), &config.github_token)
        .json(&body)
        .send()
        .await?
        .error_for_status()?;
    Ok(())
}
