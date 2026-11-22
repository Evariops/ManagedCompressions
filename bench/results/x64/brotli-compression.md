# Brotli compression, x64

AMD Ryzen 9 7950X, Windows 11, .NET 11.0.0, one thread kept on logical processor 2. Native is the brotli
1.1.1 .NET ships, through `BrotliEncoder.TryCompress` at the quality and window 22, which creates and
frees brotli's state at every call; managed is a reused `BrotliCompressor`. Measured with:

```sh
bench/ManagedCompressions.Bench --codecs brotli --sets silesia-sample,github --levels 0,1,2,3,4,5,6,7,8,9,10,11 --directions compression --seconds 2 --core 2
```

The managed compressor writes brotli's bytes, so the ratios are equal by construction, and checked.
Qualities 4 to 9 move by up to 20 % from one process to the next, on both sides; the intervals below are
those of one process.

## silesia-sample: 1 MiB every 16 MiB of silesia.tar, 13.6 MB, one stream

| level | ratio native | ratio managed | compression native | compression managed | × |
|--:|--:|--:|--:|--:|--:|
| 0 | 2.249 | 2.249 | 565 | 535 | 0.95× (0.95–0.95) |
| 1 | 2.442 | 2.442 | 408 | 432 | 1.06× (1.06–1.06) |
| 2 | 2.602 | 2.602 | 212 | 217 | 1.03× (1.02–1.03) |
| 3 | 2.622 | 2.622 | 182 | 183 | 1.00× (1.00–1.00) |
| 4 | 2.660 | 2.660 | 77.8 | 124 | 1.60× (1.59–1.60) |
| 5 | 2.884 | 2.884 | 48.6 | 56.5 | 1.16× (1.11–1.18) |
| 6 | 2.910 | 2.910 | 31.2 | 39.9 | 1.28× (1.21–1.38) |
| 7 | 2.926 | 2.926 | 23.2 | 23.6 | 1.01× (1.00–1.14) |
| 8 | 2.936 | 2.936 | 15.2 | 17.7 | 1.16× (1.14–1.21) |
| 9 | 2.945 | 2.945 | 12.4 | 13.1 | 1.05× (0.91–1.08) |
| 10 | 3.206 | 3.206 | 2.12 | 2.54 | 1.20× (1.19–1.21) |
| 11 | 3.312 | 3.312 | 1.03 | 1.17 | 1.14× (1.14–1.15) |

## 500 JSON records of 800 bytes, a stream each

| level | ratio native | ratio managed | compression native | compression managed | × |
|--:|--:|--:|--:|--:|--:|
| 0 | 2.426 | 2.426 | 313 | 282 | 0.90× (0.89–0.91) |
| 1 | 2.781 | 2.781 | 190 | 206 | 1.07× (1.06–1.10) |
| 2 | 2.856 | 2.856 | 208 | 238 | 1.14× (1.14–1.15) |
| 3 | 3.071 | 3.071 | 91.8 | 104 | 1.13× (1.12–1.15) |
| 4 | 3.047 | 3.047 | 59.9 | 65.9 | 1.10× (1.09–1.11) |
| 5 | 3.309 | 3.309 | 5.29 | 60.8 | 11.87× (11.49–11.98) |
| 6 | 3.309 | 3.309 | 3.99 | 61.5 | 15.12× (14.96–15.78) |
| 7 | 3.319 | 3.319 | 3.04 | 54.0 | 17.89× (17.27–19.26) |
| 8 | 3.319 | 3.319 | 2.76 | 52.6 | 19.71× (18.77–20.63) |
| 9 | 3.331 | 3.331 | 2.26 | 47.3 | 21.00× (19.60–24.38) |
| 10 | 3.327 | 3.327 | 1.82 | 2.04 | 1.13× (1.12–1.13) |
| 11 | 3.466 | 3.466 | 1.07 | 1.10 | 1.03× (1.03–1.03) |

Throughputs in MB/s of uncompressed data (10^6 bytes a second), median of the rounds; a crowd's (a set named `x N`) are N threads' together. × is the managed speedup over the native one, paired round by round, with its 95% interval where measured. Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.
