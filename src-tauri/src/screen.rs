use crate::config::app_data_dir;
use anyhow::{anyhow, Result};
use image::{ImageBuffer, Rgba};
use serde::Serialize;
use std::path::PathBuf;
use uuid::Uuid;
use windows::Win32::Foundation::HWND;
use windows::Win32::Graphics::Gdi::{
    BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDC, GetDIBits,
    ReleaseDC, SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HBITMAP,
    HGDIOBJ, RGBQUAD, SRCCOPY,
};
use windows::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXSCREEN, SM_CYSCREEN};

#[derive(Debug, Clone, Serialize)]
pub struct ScreenCapture {
    pub path: String,
    pub width: u32,
    pub height: u32,
}

fn screenshots_dir() -> PathBuf {
    app_data_dir().join("screenshots")
}

pub fn capture_primary_screen() -> Result<ScreenCapture> {
    let width = unsafe { GetSystemMetrics(SM_CXSCREEN) };
    let height = unsafe { GetSystemMetrics(SM_CYSCREEN) };
    if width <= 0 || height <= 0 {
        return Err(anyhow!("Invalid screen size"));
    }

    let screen_dc = unsafe { GetDC(HWND(0)) };
    if screen_dc.0 == 0 {
        return Err(anyhow!("GetDC failed"));
    }

    let memory_dc = unsafe { CreateCompatibleDC(screen_dc) };
    if memory_dc.0 == 0 {
        unsafe { ReleaseDC(HWND(0), screen_dc) };
        return Err(anyhow!("CreateCompatibleDC failed"));
    }

    let bitmap = unsafe { CreateCompatibleBitmap(screen_dc, width, height) };
    if bitmap.0 == 0 {
        unsafe {
            DeleteDC(memory_dc);
            ReleaseDC(HWND(0), screen_dc);
        }
        return Err(anyhow!("CreateCompatibleBitmap failed"));
    }

    unsafe {
        SelectObject(memory_dc, HGDIOBJ(bitmap.0));
        let copied = BitBlt(memory_dc, 0, 0, width, height, screen_dc, 0, 0, SRCCOPY);
        if !copied.as_bool() {
            DeleteObject(HGDIOBJ(bitmap.0));
            DeleteDC(memory_dc);
            ReleaseDC(HWND(0), screen_dc);
            return Err(anyhow!("BitBlt failed"));
        }
    }

    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        bmiColors: [RGBQUAD::default(); 1],
    };
    let mut buffer = vec![0u8; (width * height * 4) as usize];
    let copied = unsafe {
        GetDIBits(
            memory_dc,
            HBITMAP(bitmap.0),
            0,
            height as u32,
            Some(buffer.as_mut_ptr() as *mut _),
            &mut info,
            DIB_RGB_COLORS,
        )
    };

    unsafe {
        DeleteObject(HGDIOBJ(bitmap.0));
        DeleteDC(memory_dc);
        ReleaseDC(HWND(0), screen_dc);
    }

    if copied == 0 {
        return Err(anyhow!("GetDIBits failed"));
    }

    let mut rgba = Vec::with_capacity(buffer.len());
    for chunk in buffer.chunks_exact(4) {
        rgba.extend_from_slice(&[chunk[2], chunk[1], chunk[0], 255]);
    }
    let image = ImageBuffer::<Rgba<u8>, _>::from_raw(width as u32, height as u32, rgba)
        .ok_or_else(|| anyhow!("Failed to build image buffer"))?;

    let dir = screenshots_dir();
    std::fs::create_dir_all(&dir)?;
    let path = dir.join(format!("{}.png", Uuid::new_v4()));
    image.save(&path)?;

    Ok(ScreenCapture {
        path: path.to_string_lossy().to_string(),
        width: width as u32,
        height: height as u32,
    })
}
