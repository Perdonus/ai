use crate::config::logs_dir;
use chrono::Utc;
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    panic,
    path::PathBuf,
    sync::{Mutex, OnceLock},
};

static STARTUP_LOG: OnceLock<Mutex<File>> = OnceLock::new();

fn startup_log_path() -> PathBuf {
    logs_dir().join("startup.log")
}

fn write_line(file: &mut File, level: &str, message: &str) {
    let _ = writeln!(
        file,
        "[{}] [{}] {}",
        Utc::now().format("%Y-%m-%d %H:%M:%S%.3f UTC"),
        level,
        message
    );
    let _ = file.flush();
}

pub fn init_startup_logging() {
    let path = startup_log_path();
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }

    let file = match OpenOptions::new().create(true).append(true).open(&path) {
        Ok(file) => file,
        Err(_) => return,
    };

    let logger = STARTUP_LOG.get_or_init(|| Mutex::new(file));
    if let Ok(mut file) = logger.lock() {
        write_line(&mut file, "INFO", "========== app launch ==========");
    }

    panic::set_hook(Box::new(|info| {
        log_error(&format!("panic: {info}"));
    }));
}

pub fn log_info(message: impl AsRef<str>) {
    log("INFO", message.as_ref());
}

pub fn log_warn(message: impl AsRef<str>) {
    log("WARN", message.as_ref());
}

pub fn log_error(message: impl AsRef<str>) {
    log("ERROR", message.as_ref());
}

fn log(level: &str, message: &str) {
    if let Some(logger) = STARTUP_LOG.get() {
        if let Ok(mut file) = logger.lock() {
            write_line(&mut file, level, message);
        }
    }
}
