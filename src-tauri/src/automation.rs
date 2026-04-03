use anyhow::{anyhow, Result};
use serde::{Deserialize, Serialize};
use std::{thread, time::Duration};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, VkKeyScanW, INPUT, INPUT_0, INPUT_KEYBOARD, INPUT_MOUSE, KEYBDINPUT,
    KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, MOUSEEVENTF_ABSOLUTE, MOUSEEVENTF_LEFTDOWN as LEFTDOWN,
    MOUSEEVENTF_LEFTUP as LEFTUP, MOUSEEVENTF_MOVE, MOUSEEVENTF_RIGHTDOWN as RIGHTDOWN,
    MOUSEEVENTF_RIGHTUP as RIGHTUP, MOUSEEVENTF_WHEEL, MOUSEINPUT, MOUSE_EVENT_FLAGS, VIRTUAL_KEY,
    VK_CONTROL, VK_LBUTTON, VK_LWIN, VK_MENU, VK_RBUTTON, VK_RETURN, VK_SHIFT, VK_SPACE, VK_TAB,
};
use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXSCREEN, SM_CYSCREEN};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MouseButton {
    Left,
    Right,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MouseActionKind {
    Move,
    LeftClick,
    RightClick,
    DoubleClick,
    ScrollUp,
    ScrollDown,
    MouseDown,
    MouseUp,
    Hold,
    Drag,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MouseActionRequest {
    pub x: i32,
    pub y: i32,
    pub kind: MouseActionKind,
    #[serde(default = "default_left_button")]
    pub button: MouseButton,
    #[serde(default)]
    pub end_x: Option<i32>,
    #[serde(default)]
    pub end_y: Option<i32>,
    #[serde(default = "default_duration_ms")]
    pub duration_ms: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum KeyboardActionKind {
    Combo,
    KeyDown,
    KeyUp,
    KeyPress,
    KeyHold,
    TypeText,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct KeyboardActionRequest {
    pub kind: KeyboardActionKind,
    #[serde(default)]
    pub keys: Vec<String>,
    #[serde(default)]
    pub text: String,
    #[serde(default = "default_duration_ms")]
    pub duration_ms: u64,
}

fn default_left_button() -> MouseButton {
    MouseButton::Left
}

fn default_duration_ms() -> u64 {
    300
}

fn normalized_absolute(value: i32, max: i32) -> i32 {
    if max <= 1 {
        return 0;
    }
    ((value.clamp(0, max - 1) * 65535) / (max - 1)).clamp(0, 65535)
}

fn send_inputs(inputs: &mut [INPUT]) -> Result<()> {
    let sent = unsafe { SendInput(inputs, std::mem::size_of::<INPUT>() as i32) };
    if sent == 0 {
        return Err(anyhow!("SendInput failed"));
    }
    Ok(())
}

fn mouse_button_flags(button: &MouseButton) -> (MOUSE_EVENT_FLAGS, MOUSE_EVENT_FLAGS) {
    match button {
        MouseButton::Left => (LEFTDOWN, LEFTUP),
        MouseButton::Right => (RIGHTDOWN, RIGHTUP),
    }
}

fn key_from_name(name: &str) -> Option<VIRTUAL_KEY> {
    match name.to_ascii_lowercase().as_str() {
        "ctrl" | "control" => Some(VK_CONTROL),
        "shift" => Some(VK_SHIFT),
        "alt" | "ralt" | "rightalt" => Some(VK_MENU),
        "win" | "meta" => Some(VK_LWIN),
        "enter" => Some(VK_RETURN),
        "tab" => Some(VK_TAB),
        "space" => Some(VK_SPACE),
        "lbutton" => Some(VK_LBUTTON),
        "rbutton" => Some(VK_RBUTTON),
        value if value.len() == 1 => {
            let byte = value.as_bytes()[0].to_ascii_uppercase();
            Some(VIRTUAL_KEY(byte as u16))
        }
        _ => None,
    }
}

fn move_mouse(x: i32, y: i32) -> Result<()> {
    let screen_w = unsafe { GetSystemMetrics(SM_CXSCREEN) };
    let screen_h = unsafe { GetSystemMetrics(SM_CYSCREEN) };
    let dx = normalized_absolute(x, screen_w);
    let dy = normalized_absolute(y, screen_h);
    let mut inputs = [INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dx,
                dy,
                mouseData: 0,
                dwFlags: MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }];
    send_inputs(&mut inputs)
}

fn mouse_transition(flag: MOUSE_EVENT_FLAGS) -> Result<()> {
    let mut inputs = [INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dx: 0,
                dy: 0,
                mouseData: 0,
                dwFlags: flag,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }];
    send_inputs(&mut inputs)
}

fn key_event(vk: VIRTUAL_KEY, key_up: bool) -> Result<()> {
    let mut inputs = [INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: vk,
                wScan: 0,
                dwFlags: if key_up { KEYEVENTF_KEYUP } else { Default::default() },
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }];
    send_inputs(&mut inputs)
}

pub fn mouse_action(request: MouseActionRequest) -> Result<()> {
    move_mouse(request.x, request.y)?;
    let (down_flag, up_flag) = mouse_button_flags(&request.button);

    match request.kind {
        MouseActionKind::Move => Ok(()),
        MouseActionKind::LeftClick => {
            mouse_transition(LEFTDOWN)?;
            mouse_transition(LEFTUP)
        }
        MouseActionKind::RightClick => {
            mouse_transition(RIGHTDOWN)?;
            mouse_transition(RIGHTUP)
        }
        MouseActionKind::DoubleClick => {
            mouse_transition(LEFTDOWN)?;
            mouse_transition(LEFTUP)?;
            thread::sleep(Duration::from_millis(60));
            mouse_transition(LEFTDOWN)?;
            mouse_transition(LEFTUP)
        }
        MouseActionKind::ScrollUp | MouseActionKind::ScrollDown => {
            let delta = if matches!(request.kind, MouseActionKind::ScrollUp) {
                120
            } else {
                -120i32 as u32
            };
            let mut inputs = [INPUT {
                r#type: INPUT_MOUSE,
                Anonymous: INPUT_0 {
                    mi: MOUSEINPUT {
                        dx: 0,
                        dy: 0,
                        mouseData: delta,
                        dwFlags: MOUSEEVENTF_WHEEL,
                        time: 0,
                        dwExtraInfo: 0,
                    },
                },
            }];
            send_inputs(&mut inputs)
        }
        MouseActionKind::MouseDown => mouse_transition(down_flag),
        MouseActionKind::MouseUp => mouse_transition(up_flag),
        MouseActionKind::Hold => {
            mouse_transition(down_flag)?;
            thread::sleep(Duration::from_millis(request.duration_ms));
            mouse_transition(up_flag)
        }
        MouseActionKind::Drag => {
            let end_x = request.end_x.ok_or_else(|| anyhow!("end_x is required for drag"))?;
            let end_y = request.end_y.ok_or_else(|| anyhow!("end_y is required for drag"))?;
            mouse_transition(down_flag)?;
            let steps = 24.max((request.duration_ms / 16) as i32);
            for step in 1..=steps {
                let x = request.x + (end_x - request.x) * step / steps;
                let y = request.y + (end_y - request.y) * step / steps;
                move_mouse(x, y)?;
                thread::sleep(Duration::from_millis((request.duration_ms / steps as u64).max(5)));
            }
            mouse_transition(up_flag)
        }
    }
}

pub fn keyboard_combo(keys: Vec<String>) -> Result<()> {
    if keys.is_empty() {
        return Err(anyhow!("No keys supplied"));
    }

    let virtual_keys: Vec<VIRTUAL_KEY> = keys
        .iter()
        .map(|key| key_from_name(key).ok_or_else(|| anyhow!("Unsupported key: {}", key)))
        .collect::<Result<Vec<_>>>()?;

    for vk in &virtual_keys {
        key_event(*vk, false)?;
    }
    for vk in virtual_keys.iter().rev() {
        key_event(*vk, true)?;
    }
    Ok(())
}

pub fn keyboard_action(request: KeyboardActionRequest) -> Result<()> {
    match request.kind {
        KeyboardActionKind::Combo => keyboard_combo(request.keys),
        KeyboardActionKind::KeyDown => {
            let key = request
                .keys
                .first()
                .ok_or_else(|| anyhow!("Missing key"))?;
            key_event(
                key_from_name(key).ok_or_else(|| anyhow!("Unsupported key: {}", key))?,
                false,
            )
        }
        KeyboardActionKind::KeyUp => {
            let key = request
                .keys
                .first()
                .ok_or_else(|| anyhow!("Missing key"))?;
            key_event(
                key_from_name(key).ok_or_else(|| anyhow!("Unsupported key: {}", key))?,
                true,
            )
        }
        KeyboardActionKind::KeyPress => {
            let key = request
                .keys
                .first()
                .ok_or_else(|| anyhow!("Missing key"))?;
            let vk = key_from_name(key).ok_or_else(|| anyhow!("Unsupported key: {}", key))?;
            key_event(vk, false)?;
            key_event(vk, true)
        }
        KeyboardActionKind::KeyHold => {
            let key = request
                .keys
                .first()
                .ok_or_else(|| anyhow!("Missing key"))?;
            let vk = key_from_name(key).ok_or_else(|| anyhow!("Unsupported key: {}", key))?;
            key_event(vk, false)?;
            thread::sleep(Duration::from_millis(request.duration_ms));
            key_event(vk, true)
        }
        KeyboardActionKind::TypeText => type_text(&request.text),
    }
}

pub fn type_text(text: &str) -> Result<()> {
    for ch in text.encode_utf16() {
        let mut inputs = [
            INPUT {
                r#type: INPUT_KEYBOARD,
                Anonymous: INPUT_0 {
                    ki: KEYBDINPUT {
                        wVk: VIRTUAL_KEY(0),
                        wScan: ch,
                        dwFlags: KEYEVENTF_UNICODE,
                        time: 0,
                        dwExtraInfo: 0,
                    },
                },
            },
            INPUT {
                r#type: INPUT_KEYBOARD,
                Anonymous: INPUT_0 {
                    ki: KEYBDINPUT {
                        wVk: VIRTUAL_KEY(0),
                        wScan: ch,
                        dwFlags: KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time: 0,
                        dwExtraInfo: 0,
                    },
                },
            },
        ];
        send_inputs(&mut inputs)?;
    }
    Ok(())
}
