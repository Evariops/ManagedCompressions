# Brotli compression, x64

AMD Ryzen 9 7950X, Windows 11, .NET 11.0.0, one thread kept on logical processor 2. Native is the brotli
1.1.1 .NET ships, through `BrotliEncoder.TryCompress` at the quality and window 22, which creates and
frees brotli's state at every call; managed is a reused `BrotliCompressor`. Measured with:

```sh
bench/ManagedCompressions.Bench --codecs brotli --sets silesia-sample,github --levels 0,1,2,3,4,5,6,7,8,9,10,11 --directions compression --seconds 2 --core 2
```

The managed compressor writes brotli's bytes, so the ratios are equal by construction, and checked.
Qualities 4 to 9 move by up to 20 % from one process to the next, on both sides; the intervals below are
those of one process. The native side is bimodal at quality 4: 124 MB/s here, 77.8 MB/s in the process
measured before, which made the managed compressor look 1.60× faster there.

## silesia-sample: 1 MiB every 16 MiB of silesia.tar, 13.6 MB, one stream

| level | ratio native | ratio managed | compression native | compression managed | × |
|--:|--:|--:|--:|--:|--:|
| 0 | 2.249 | 2.249 | 550 | 550 | 1.00× (1.00–1.00) |
| 1 | 2.442 | 2.442 | 411 | 460 | 1.12× (1.12–1.12) |
| 2 | 2.602 | 2.602 | 216 | 220 | 1.02× (1.02–1.02) |
| 3 | 2.622 | 2.622 | 186 | 186 | 1.00× (1.00–1.00) |
| 4 | 2.660 | 2.660 | 124 | 134 | 1.08× (1.08–1.10) |
| 5 | 2.884 | 2.884 | 50.4 | 65.6 | 1.29× (1.27–1.35) |
| 6 | 2.910 | 2.910 | 36.4 | 43.8 | 1.20× (1.20–1.26) |
| 7 | 2.926 | 2.926 | 24.8 | 28.8 | 1.16× (1.16–1.21) |
| 8 | 2.936 | 2.936 | 19.1 | 22.4 | 1.18× (1.16–1.19) |
| 9 | 2.945 | 2.945 | 14.6 | 17.2 | 1.18× (1.16–1.19) |
| 10 | 3.206 | 3.206 | 2.18 | 2.56 | 1.18× (1.17–1.18) |
| 11 | 3.312 | 3.312 | 1.05 | 1.20 | 1.15× (1.14–1.15) |

## 500 JSON records of 800 bytes, a stream each

| level | ratio native | ratio managed | compression native | compression managed | × |
|--:|--:|--:|--:|--:|--:|
| 0 | 2.426 | 2.426 | 312 | 341 | 1.09× (1.08–1.11) |
| 1 | 2.781 | 2.781 | 189 | 204 | 1.06× (1.05–1.10) |
| 2 | 2.856 | 2.856 | 211 | 250 | 1.19× (1.17–1.21) |
| 3 | 3.071 | 3.071 | 91.3 | 112 | 1.23× (1.21–1.24) |
| 4 | 3.047 | 3.047 | 59.7 | 69.6 | 1.16× (1.15–1.18) |
| 5 | 3.309 | 3.309 | 5.30 | 63.7 | 12.35× (11.89–12.41) |
| 6 | 3.309 | 3.309 | 4.11 | 62.1 | 15.60× (15.12–15.99) |
| 7 | 3.319 | 3.319 | 3.08 | 54.5 | 17.42× (17.09–18.12) |
| 8 | 3.319 | 3.319 | 2.72 | 52.4 | 20.11× (19.18–22.74) |
| 9 | 3.331 | 3.331 | 2.38 | 47.6 | 21.00× (19.80–21.20) |
| 10 | 3.327 | 3.327 | 1.81 | 2.05 | 1.13× (1.11–1.14) |
| 11 | 3.466 | 3.466 | 1.07 | 1.09 | 1.03× (1.02–1.03) |

Throughputs in MB/s of uncompressed data (10^6 bytes a second), median of the rounds; a crowd's (a set named `x N`) are N threads' together. × is the managed speedup over the native one, paired round by round, with its 95% interval where measured. Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.
