#[cfg(windows)]
use std::{ffi::OsStr, os::windows::ffi::OsStrExt, ptr};
#[cfg(windows)]
use windows_sys::Win32::System::SystemInformation::GetTickCount64;
#[cfg(windows)]
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
#[cfg(windows)]
use windows_sys::Win32::System::Memory::{CreateFileMappingW, MapViewOfFile, UnmapViewOfFile, FILE_MAP_WRITE, MEMORY_MAPPED_VIEW_ADDRESS, PAGE_READWRITE};

#[cfg(windows)]
pub struct Publisher { handle: HANDLE, view: *mut u8, sequence: i64 }
#[cfg(windows)] unsafe impl Send for Publisher {}
#[cfg(windows)] unsafe impl Sync for Publisher {}

#[cfg(windows)]
impl Publisher {
    pub fn new(name: &str, size: u32) -> Option<Self> {
        let wide: Vec<u16> = OsStr::new(name).encode_wide().chain(Some(0)).collect();
        let handle = unsafe { CreateFileMappingW(INVALID_HANDLE_VALUE, ptr::null(), PAGE_READWRITE, 0, size, wide.as_ptr()) };
        if handle.is_null() { return None; }
        let address = unsafe { MapViewOfFile(handle, FILE_MAP_WRITE, 0, 0, size as usize) };
        if address.Value.is_null() { unsafe { CloseHandle(handle) }; return None; }
        Some(Self { handle, view: address.Value as *mut u8, sequence: 0 })
    }
    pub fn heartbeat(&mut self) { self.sequence += 1; let tick = monotonic_ms(); unsafe { ptr::copy_nonoverlapping(self.sequence.to_le_bytes().as_ptr(), self.view, 8); ptr::copy_nonoverlapping(tick.to_le_bytes().as_ptr(), self.view.add(8), 8); } }
    pub fn publish_json(&mut self, payload: &[u8]) { if payload.len() > 131052 { return; } self.sequence += 1; let tick = monotonic_ms(); unsafe { ptr::copy_nonoverlapping(self.sequence.to_le_bytes().as_ptr(), self.view, 8); ptr::copy_nonoverlapping(tick.to_le_bytes().as_ptr(), self.view.add(8), 8); ptr::copy_nonoverlapping((payload.len() as i32).to_le_bytes().as_ptr(), self.view.add(16), 4); ptr::copy_nonoverlapping(payload.as_ptr(), self.view.add(20), payload.len()); } }
}
#[cfg(windows)]
fn monotonic_ms() -> i64 {
    unsafe { GetTickCount64() as i64 }
}
#[cfg(windows)]
impl Drop for Publisher { fn drop(&mut self) { unsafe { UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: self.view as *mut _ }); CloseHandle(self.handle); } } }
