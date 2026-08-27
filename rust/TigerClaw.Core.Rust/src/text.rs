//! Text-file decoding shared by configuration and code-table loaders.
//!
//! TigerClaw's Windows implementation accepts the encodings commonly emitted
//! by the old editor and by Rime (UTF-8, UTF-8 BOM, UTF-16 LE/BE, and the
//! active Windows code page).  Keeping the detector in one small module makes
//! directory loading transactional and prevents a single legacy file from
//! silently disappearing just because `read_to_string` rejected it.

use std::fs;
use std::io;
use std::path::Path;

pub fn read_text(path: impl AsRef<Path>) -> io::Result<String> {
    let bytes = fs::read(path)?;
    Ok(decode_bytes(&bytes))
}

pub fn decode_bytes(bytes: &[u8]) -> String {
    if bytes.starts_with(&[0xEF, 0xBB, 0xBF]) {
        return String::from_utf8_lossy(&bytes[3..]).into_owned();
    }
    if bytes.starts_with(&[0xFF, 0xFE]) {
        return decode_utf16(&bytes[2..], false);
    }
    if bytes.starts_with(&[0xFE, 0xFF]) {
        return decode_utf16(&bytes[2..], true);
    }
    if let Ok(value) = std::str::from_utf8(bytes) {
        return value.to_owned();
    }

    // Files produced by the historical Windows editor are normally in the
    // system ANSI code page.  On Windows use MultiByteToWideChar(CP_ACP),
    // which is the same detector used by the C# Core.  Non-Windows tests have
    // no system code page, so retain every byte with a deterministic lossless
    // one-byte fallback rather than dropping the file.
    #[cfg(windows)]
    {
        use windows_sys::Win32::Globalization::{MultiByteToWideChar, CP_ACP, MB_ERR_INVALID_CHARS};
        let length = unsafe {
            MultiByteToWideChar(CP_ACP, MB_ERR_INVALID_CHARS, bytes.as_ptr(), bytes.len() as i32, std::ptr::null_mut(), 0)
        };
        if length > 0 {
            let mut wide = vec![0u16; length as usize];
            let written = unsafe {
                MultiByteToWideChar(CP_ACP, 0, bytes.as_ptr(), bytes.len() as i32, wide.as_mut_ptr(), length)
            };
            if written > 0 {
                return String::from_utf16_lossy(&wide[..written as usize]);
            }
        }
    }

    bytes.iter().map(|byte| char::from(*byte)).collect()
}

fn decode_utf16(bytes: &[u8], big_endian: bool) -> String {
    let mut units = Vec::with_capacity(bytes.len() / 2);
    let mut index = 0;
    while index + 1 < bytes.len() {
        let pair = [bytes[index], bytes[index + 1]];
        units.push(if big_endian { u16::from_be_bytes(pair) } else { u16::from_le_bytes(pair) });
        index += 2;
    }
    String::from_utf16_lossy(&units)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_utf8_bom_and_utf16_variants() {
        assert_eq!(decode_bytes(&[0xEF, 0xBB, 0xBF, b'a']), "a");
        assert_eq!(decode_bytes(&[0xFF, 0xFE, b'a', 0]), "a");
        assert_eq!(decode_bytes(&[0xFE, 0xFF, 0, b'a']), "a");
    }

    #[test]
    fn invalid_utf8_is_not_dropped() {
        assert!(!decode_bytes(&[0x81, 0x40]).is_empty());
    }
}
