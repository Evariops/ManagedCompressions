# Brotli compression, x64

AMD Ryzen 9 7950X, Windows 11, .NET 11.0.0, one thread kept on logical processor 2. Native is the brotli
1.1.1 .NET ships, through `BrotliEncoder.TryCompress` at the quality and window 22, which creates and
frees brotli's state at every call; managed is a reused `BrotliCompressor`, in safe code. Measured with:

```sh
bench/ManagedCompressions.Bench --codecs brotli --sets silesia-sample,github --levels 0,1,2,3,4,5,6,7,8,9,10,11 --directions compression --seconds 2 --core 2
```

The managed compressor writes brotli's bytes, so the ratios are equal by construction, and checked.
Qualities 4 to 9 move by up to 20 % from one process to the next, on both sides; the intervals below are
those of one process. The native side is bimodal at quality 4: from 77.8 to 124 MB/s depending on the
process, 118 MB/s here.

## silesia-sample: 1 MiB every 16 MiB of silesia.tar, 13.6 MB, one stream

| level | ratio native | ratio managed | compression native | compression managed | × |
|--:|--:|--:|--:|--:|--:|
| 0 | 2.249 | 2.249 | 563 | 457 | 0.81× (0.81–0.82) |
| 1 | 2.442 | 2.442 | 406 | 387 | 0.95× (0.94–0.96) |
| 2 | 2.602 | 2.602 | 210 | 170 | 0.81× (0.79–0.82) |
| 3 | 2.622 | 2.622 | 181 | 148 | 0.82× (0.81–0.82) |
| 4 | 2.660 | 2.660 | 118 | 106 | 0.90× (0.89–0.92) |
| 5 | 2.884 | 2.884 | 50.4 | 52.8 | 1.04× (1.03–1.08) |
| 6 | 2.910 | 2.910 | 35.4 | 37.7 | 1.06× (1.04–1.11) |
| 7 | 2.926 | 2.926 | 23.4 | 26.4 | 1.13× (1.12–1.13) |
| 8 | 2.936 | 2.936 | 17.6 | 19.5 | 1.11× (1.07–1.26) |
| 9 | 2.945 | 2.945 | 13.5 | 15.7 | 1.14× (1.14–1.18) |
| 10 | 3.206 | 3.206 | 2.04 | 2.20 | 1.06× (1.05–1.07) |
| 11 | 3.312 | 3.312 | 1.02 | 1.04 | 1.00× (1.00–1.03) |

## 500 JSON records of 800 bytes, a stream each

| level | ratio native | ratio managed | compression native | compression managed | × |
|--:|--:|--:|--:|--:|--:|
| 0 | 2.426 | 2.426 | 311 | 295 | 0.94× (0.93–0.95) |
| 1 | 2.781 | 2.781 | 189 | 188 | 0.99× (0.98–1.00) |
| 2 | 2.856 | 2.856 | 209 | 185 | 0.88× (0.88–0.89) |
| 3 | 3.071 | 3.071 | 90.9 | 99.6 | 1.09× (1.08–1.10) |
| 4 | 3.047 | 3.047 | 57.7 | 59.4 | 1.03× (1.01–1.04) |
| 5 | 3.309 | 3.309 | 5.33 | 56.0 | 10.71× (10.51–10.93) |
| 6 | 3.309 | 3.309 | 4.08 | 55.3 | 13.53× (13.08–14.47) |
| 7 | 3.319 | 3.319 | 3.08 | 49.5 | 16.41× (15.60–17.18) |
| 8 | 3.319 | 3.319 | 2.70 | 48.7 | 18.38× (17.79–22.08) |
| 9 | 3.331 | 3.331 | 2.38 | 44.9 | 18.60× (18.39–19.07) |
| 10 | 3.327 | 3.327 | 1.80 | 1.85 | 1.02× (1.01–1.03) |
| 11 | 3.466 | 3.466 | 1.05 | 0.92 | 0.88× (0.87–0.88) |

Throughputs in MB/s of uncompressed data (10^6 bytes a second), median of the rounds; a crowd's (a set named `x N`) are N threads' together. × is the managed speedup over the native one, paired round by round, with its 95% interval where measured. Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.
