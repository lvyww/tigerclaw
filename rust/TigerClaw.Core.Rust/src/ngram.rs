use memmap2::Mmap;
use std::fs::File;
use std::io;
use std::path::Path;

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
        Ok(Self { map, unigram_offset, unigram_count, bigram_offset, bigram_count, bigram_context_offset, bigram_context_count, trigram_offset, trigram_count, trigram_context_offset, trigram_context_count, unknown })
    }

    pub fn score(&self, text: &str) -> f64 {
        let mut previous2 = 0;
        let mut previous1 = 0;
        let mut total = 0.0;
        for ch in text.chars() {
            let target = ch as u32;
            let unigram = lookup_i32(&self.map, self.unigram_offset, self.unigram_count, target as i32, self.unknown);
            let bigram = lookup_u64(&self.map, self.bigram_offset, self.bigram_count, pack_pair(previous1, target), 0.0)
                + lookup_i32(&self.map, self.bigram_context_offset, self.bigram_context_count, previous1 as i32, 1.0) as f64 * unigram as f64;
            let trigram = lookup_u64(&self.map, self.trigram_offset, self.trigram_count, pack_triple(previous2, previous1, target), 0.0)
                + lookup_u64(&self.map, self.trigram_context_offset, self.trigram_context_count, pack_pair(previous2, previous1), 1.0) * bigram;
            total += trigram.max(1e-300).ln();
            previous2 = previous1;
            previous1 = target;
        }
        total
    }
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
    }
}
