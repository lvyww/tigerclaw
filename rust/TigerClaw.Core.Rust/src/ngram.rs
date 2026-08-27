use memmap2::Mmap;
use std::fs::File;
use std::io;
use std::path::Path;
use std::sync::Mutex;

const LOG_PROBABILITY_CACHE_SIZE: usize = 1 << 18;
const OBSERVED_BIGRAM_CACHE_SIZE: usize = 1 << 16;
const NO_UNIGRAM_CACHE_KEY_FLAG: u64 = 1 << 63;

#[derive(Debug)]
pub struct NgramModel {
    map: Mmap,
    unigram_offset: usize,
    unigram_count: usize,
    bigram_offset: usize,
    bigram_count: usize,
    bigram_context_offset: usize,
    bigram_context_count: usize,
    trigram_offset: usize,
    trigram_count: usize,
    trigram_context_offset: usize,
    trigram_context_count: usize,
    unknown: f32,
    log_cache: Mutex<FixedSizeCache<f64>>,
    observed_cache: Mutex<FixedSizeCache<bool>>,
}

struct FixedSizeCache<T: Copy> {
    entries: Vec<CacheEntry<T>>,
    mask: usize,
}

#[derive(Clone, Copy)]
struct CacheEntry<T: Copy> {
    key: u64,
    value: T,
    occupied: bool,
}

impl<T: Copy + Default> FixedSizeCache<T> {
    fn new(size: usize) -> Self {
        assert!(size > 0 && size.is_power_of_two());
        Self {
            entries: vec![
                CacheEntry {
                    key: 0,
                    value: T::default(),
                    occupied: false
                };
                size
            ],
            mask: size - 1,
        }
    }

    fn get(&self, key: u64) -> Option<T> {
        let entry = self.entries[mix64(key) & self.mask];
        if entry.occupied && entry.key == key {
            Some(entry.value)
        } else {
            None
        }
    }

    fn set(&mut self, key: u64, value: T) {
        self.entries[mix64(key) & self.mask] = CacheEntry {
            key,
            value,
            occupied: true,
        };
    }
}

impl<T: Copy> std::fmt::Debug for FixedSizeCache<T> {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("FixedSizeCache")
            .field("slots", &self.entries.len())
            .finish()
    }
}

fn mix64(mut key: u64) -> usize {
    key ^= key >> 33;
    key = key.wrapping_mul(0xff51afd7ed558ccd);
    key ^= key >> 33;
    key = key.wrapping_mul(0xc4ceb9fe1a85ec53);
    key ^= key >> 33;
    key as usize
}

impl NgramModel {
    pub fn load(path: impl AsRef<Path>) -> io::Result<Self> {
        let file = File::open(path)?;
        let map = unsafe { Mmap::map(&file)? };
        let mut p = 0;
        if read_bytes(&map, &mut p, 8) != b"TCSKNM01" {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid n-gram magic"));
        }
        if read_i32(&map, &mut p)? != 1 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "unsupported n-gram version"));
        }
        let unigram_count = read_i32(&map, &mut p)? as usize;
        let unigram_offset = p;
        p = checked_advance(&map, p, unigram_count * 8)?;
        let bigram_count = read_i64(&map, &mut p)? as usize;
        let bigram_offset = p;
        p = checked_advance(&map, p, bigram_count * 12)?;
        let bigram_context_count = read_i32(&map, &mut p)? as usize;
        let bigram_context_offset = p;
        p = checked_advance(&map, p, bigram_context_count * 8)?;
        let trigram_count = read_i64(&map, &mut p)? as usize;
        let trigram_offset = p;
        p = checked_advance(&map, p, trigram_count * 12)?;
        let trigram_context_count = read_i64(&map, &mut p)? as usize;
        let trigram_context_offset = p;
        checked_advance(&map, p, trigram_context_count * 12)?;
        let unknown = lookup_i32(&map, unigram_offset, unigram_count, 0, 0.0);
        if unknown <= 0.0 || !unknown.is_finite() {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "missing unknown probability"));
        }
        Ok(Self {
            map,
            unigram_offset,
            unigram_count,
            bigram_offset,
            bigram_count,
            bigram_context_offset,
            bigram_context_count,
            trigram_offset,
            trigram_count,
            trigram_context_offset,
            trigram_context_count,
            unknown,
            log_cache: Mutex::new(FixedSizeCache::new(LOG_PROBABILITY_CACHE_SIZE)),
            observed_cache: Mutex::new(FixedSizeCache::new(OBSERVED_BIGRAM_CACHE_SIZE)),
        })
    }

    pub fn log_probability(&self, previous2: &str, previous1: &str, target: &str) -> f64 {
        self.log_probability_ex(previous2, previous1, target, true)
    }

    pub fn log_probability_ex(
        &self,
        previous2: &str,
        previous1: &str,
        target: &str,
        include_unigram: bool,
    ) -> f64 {
        let first = resolve_scalar(previous2);
        let second = resolve_scalar(previous1);
        let third = resolve_scalar(target);
        let mut cache_key = pack_triple(first as u32, second as u32, third as u32);
        if !include_unigram {
            cache_key |= NO_UNIGRAM_CACHE_KEY_FLAG;
        }
        if let Ok(cache) = self.log_cache.lock() {
            if let Some(cached) = cache.get(cache_key) {
                return cached;
            }
        }
        let unigram = if include_unigram {
            lookup_i32(
                &self.map,
                self.unigram_offset,
                self.unigram_count,
                third,
                self.unknown,
            ) as f64
        } else {
            0.0
        };
        let mut bigram = lookup_u64(
            &self.map,
            self.bigram_offset,
            self.bigram_count,
            pack_pair(second as u32, third as u32),
            0.0,
        );
        let bigram_lambda = lookup_i32(
            &self.map,
            self.bigram_context_offset,
            self.bigram_context_count,
            second,
            1.0,
        ) as f64;
        bigram += bigram_lambda * unigram;
        let mut trigram = lookup_u64(
            &self.map,
            self.trigram_offset,
            self.trigram_count,
            pack_triple(first as u32, second as u32, third as u32),
            0.0,
        );
        let trigram_lambda = lookup_u64(
            &self.map,
            self.trigram_context_offset,
            self.trigram_context_count,
            pack_pair(first as u32, second as u32),
            1.0,
        );
        trigram += trigram_lambda * bigram;
        let result = trigram.max(1e-300).ln();
        if let Ok(mut cache) = self.log_cache.lock() {
            cache.set(cache_key, result);
        }
        result
    }

    pub fn has_observed_bigram(&self, previous: &str, target: &str) -> bool {
        let left = resolve_scalar(previous) as u32;
        let right = resolve_scalar(target) as u32;
        let cache_key = pack_pair(left, right);
        if let Ok(cache) = self.observed_cache.lock() {
            if let Some(cached) = cache.get(cache_key) {
                return cached;
            }
        }
        let result = contains_u64(
            &self.map,
            self.bigram_offset,
            self.bigram_count,
            cache_key,
        );
        if let Ok(mut cache) = self.observed_cache.lock() {
            cache.set(cache_key, result);
        }
        result
    }

    pub fn score(&self, text: &str) -> f64 {
        let mut previous2 = BOS.to_owned();
        let mut previous1 = BOS.to_owned();
        let mut total = 0.0;
        for ch in text.chars() {
            let target = ch.to_string();
            total += self.log_probability(&previous2, &previous1, &target);
            previous2 = previous1;
            previous1 = target;
        }
        total
    }
}

pub const BOS: &str = "\u{02}";
pub const EOS: &str = "\u{03}";

fn resolve_scalar(token: &str) -> i32 {
    if token.is_empty() {
        return 0;
    }
    let mut chars = token.chars();
    let Some(first) = chars.next() else {
        return 0;
    };
    if chars.next().is_some() {
        return 0;
    }
    first as i32
}

fn checked_advance(map: &[u8], p: usize, amount: usize) -> io::Result<usize> {
    p.checked_add(amount).filter(|end| *end <= map.len()).ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "truncated n-gram model"))
}
fn read_bytes<'a>(map: &'a [u8], p: &mut usize, n: usize) -> &'a [u8] { let end = *p + n; let out = &map[*p..end]; *p = end; out }
fn read_i32(map: &[u8], p: &mut usize) -> io::Result<i32> { let b = read_bytes(map, p, 4); Ok(i32::from_le_bytes(b.try_into().unwrap())) }
fn read_i64(map: &[u8], p: &mut usize) -> io::Result<i64> { let b = read_bytes(map, p, 8); Ok(i64::from_le_bytes(b.try_into().unwrap())) }
fn pack_pair(a: u32, b: u32) -> u64 { ((a as u64) << 21) | ((b as u64) & ((1 << 21) - 1)) }
fn pack_triple(a: u32, b: u32, c: u32) -> u64 { ((a as u64) << 42) | ((b as u64) << 21) | ((c as u64) & ((1 << 21) - 1)) }
fn lookup_i32(map: &[u8], offset: usize, count: usize, key: i32, fallback: f32) -> f32 { let mut lo=0; let mut hi=count; while lo<hi { let m=(lo+hi)/2; let pos=offset+m*8; let k=i32::from_le_bytes(map[pos..pos+4].try_into().unwrap()); if k<key {lo=m+1} else {hi=m} } if lo<count { let pos=offset+lo*8; if i32::from_le_bytes(map[pos..pos+4].try_into().unwrap())==key { return f32::from_le_bytes(map[pos+4..pos+8].try_into().unwrap()) } } fallback }
fn lookup_u64(map: &[u8], offset: usize, count: usize, key: u64, fallback: f64) -> f64 { let mut lo=0; let mut hi=count; while lo<hi { let m=(lo+hi)/2; let pos=offset+m*12; let k=u64::from_le_bytes(map[pos..pos+8].try_into().unwrap()); if k<key {lo=m+1} else {hi=m} } if lo<count { let pos=offset+lo*12; if u64::from_le_bytes(map[pos..pos+8].try_into().unwrap())==key { return f32::from_le_bytes(map[pos+8..pos+12].try_into().unwrap()) as f64 } } fallback }
fn contains_u64(map: &[u8], offset: usize, count: usize, key: u64) -> bool {
    let mut lo = 0;
    let mut hi = count;
    while lo < hi {
        let m = (lo + hi) / 2;
        let pos = offset + m * 12;
        let k = u64::from_le_bytes(map[pos..pos + 8].try_into().unwrap());
        if k < key {
            lo = m + 1;
        } else {
            hi = m;
        }
    }
    if lo < count {
        let pos = offset + lo * 12;
        return u64::from_le_bytes(map[pos..pos + 8].try_into().unwrap()) == key;
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_release_model_and_scores_text() {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../release/Models/sentence-ngram-v2.bin");
        if !path.exists() {
            return;
        }
        let model = NgramModel::load(path).unwrap();
        let score = model.score("今天");
        assert!(score.is_finite());
        let again = model.score("今天");
        assert!((score - again).abs() < 1e-12);
        assert_eq!(
            model.has_observed_bigram("今", "天"),
            model.has_observed_bigram("今", "天")
        );
    }
}
