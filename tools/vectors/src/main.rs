const DATA_KEY: [u8; 32] = [
    102, 151, 245, 119, 91, 149, 80, 222, 49, 53, 203, 172, 165, 151, 24, 28, 157, 228, 33, 16, 155, 235, 43, 88, 180,
    208, 176, 75, 147, 173, 242, 41,
];
const VERIFICATION_KEY: [u8; 32] = [
    127, 24, 87, 214, 206, 86, 237, 102, 18, 127, 249, 19, 231, 165, 195, 243,
    164, 205, 38, 213, 181, 219, 73, 230, 65, 36, 152, 127, 40, 251, 148, 195,
];

fn splitmix64_next(state: &mut u64) -> u64 {
    *state = state.wrapping_add(0x9E3779B97F4A7C15);
    let mut z = *state;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

fn create_random_data(n: usize, seed: u64) -> Vec<u8> {
    let mut ret = Vec::with_capacity(n + 7);
    let mut state = seed;
    while ret.len() < n {
        let next_u64 = splitmix64_next(&mut state);
        ret.extend_from_slice(&next_u64.to_le_bytes());
    }
    ret.resize(n, 0);
    ret
}

fn xet_hex(raw: &[u8; 32]) -> String {
    let mut s = String::new();
    for group in raw.chunks_exact(8) {
        let v = u64::from_le_bytes(group.try_into().unwrap());
        s.push_str(&format!("{v:016x}"));
    }
    s
}

fn main() {
    let data = create_random_data(1_000_000, 0);
    let boundaries = [
        84493usize, 134421, 144853, 243318, 271793, 336457, 467529, 494581, 582000, 596735, 616815, 653164, 678202,
        724510, 815591, 827760, 958832, 991092, 1000000,
    ];

    let mut start = 0usize;
    let mut chunk_hashes: Vec<[u8; 32]> = Vec::new();
    for &end in &boundaries {
        let h = blake3::keyed_hash(&DATA_KEY, &data[start..end]);
        chunk_hashes.push(*h.as_bytes());
        start = end;
    }

    println!("empty      {}", xet_hex(blake3::keyed_hash(&DATA_KEY, &[]).as_bytes()));
    println!("sm64_s0_1k {}", xet_hex(blake3::keyed_hash(&DATA_KEY, &create_random_data(1000, 0)).as_bytes()));
    for (i, h) in chunk_hashes.iter().enumerate() {
        println!("chunk[{i:02}]  {}", xet_hex(h));
    }

    let concat: Vec<u8> = chunk_hashes.iter().flat_map(|h| h.to_vec()).collect();
    println!("range_hash {}", xet_hex(blake3::keyed_hash(&VERIFICATION_KEY, &concat).as_bytes()));
}
