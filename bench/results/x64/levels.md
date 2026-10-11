# Level by level, AMD Ryzen 9 7950X (x64), JIT

`bench/ManagedCompressions.Bench`: NativeCompressions 1.1.0 (zstd 1.5.7, lz4 1.10.0) and the managed codecs,
timed in alternating rounds on one thread, each figure the median of its rounds, in MB/s of uncompressed
data. × is the managed speed over the native speed.

The machine is an AMD Ryzen 9 7950X (Zen 4, x86-64 with AVX-512) under Windows 11 (build 26200), .NET
11.0.0-rc.1. The process runs at a high priority, its timed thread kept on logical processor 2.
NativeCompressions' win-x64 libraries are built with GCC 16.2 (MSYS2): its libzstd has zstd's assembly
Huffman decoder and its BMI2 code, which the arm64 build has no counterpart of.

```sh
dotnet run -c Release --project bench/ManagedCompressions.Bench -- --codecs zstd,lz4 --seconds 3 --core 2
```

### zstd, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.896 | 2.896 | 702 | 670 | 0.95× | 2155 | 2155 | 1.00× |
| 3 | 3.205 | 3.205 | 444 | 410 | 0.92× | 1882 | 2010 | 1.07× |
| 5 | 3.378 | 3.378 | 222 | 213 | 0.96× | 1842 | 1999 | 1.09× |
| 7 | 3.518 | 3.518 | 145 | 141 | 0.97× | 1992 | 2152 | 1.08× |
| 9 | 3.588 | 3.588 | 109 | 108 | 0.99× | 2034 | 2186 | 1.08× |
| 13 | 3.674 | 3.674 | 24.3 | 24.5 | 1.01× | 2104 | 2242 | 1.07× |
| 16 | 3.834 | 3.834 | 10.3 | 10.6 | 1.03× | 2024 | 2147 | 1.06× |
| 19 | 4.011 | 4.011 | 4.99 | 5.33 | 1.07× | 1844 | 1993 | 1.08× |

### zstd, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.763 | 2.763 | 680 | 640 | 0.94× | 1926 | 1917 | 1.00× |
| 3 | 2.882 | 2.882 | 510 | 442 | 0.87× | 1814 | 1868 | 1.03× |
| 5 | 2.980 | 2.980 | 202 | 188 | 0.93× | 1586 | 1714 | 1.08× |
| 7 | 3.056 | 3.056 | 128 | 125 | 0.98× | 1727 | 1828 | 1.06× |
| 9 | 3.078 | 3.078 | 98.9 | 99.6 | 1.01× | 1766 | 1854 | 1.05× |
| 13 | 3.099 | 3.099 | 30.5 | 30.6 | 1.00× | 1696 | 1774 | 1.05× |
| 16 | 3.244 | 3.244 | 11.6 | 12.3 | 1.05× | 1459 | 1533 | 1.05× |
| 19 | 3.258 | 3.258 | 5.46 | 6.04 | 1.11× | 1495 | 1585 | 1.06× |

### zstd, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.866 | 2.866 | 328 | 415 | 1.27× | 922 | 1005 | 1.09× |
| 3 | 2.992 | 2.992 | 300 | 349 | 1.16× | 668 | 710 | 1.06× |
| 5 | 3.019 | 3.019 | 183 | 197 | 1.07× | 668 | 716 | 1.07× |
| 7 | 3.019 | 3.019 | 171 | 186 | 1.08× | 668 | 715 | 1.07× |
| 9 | 3.019 | 3.019 | 129 | 150 | 1.17× | 669 | 716 | 1.07× |
| 13 | 3.070 | 3.070 | 38.0 | 40.2 | 1.06× | 677 | 726 | 1.07× |
| 16 | 3.063 | 3.063 | 11.6 | 12.5 | 1.08× | 676 | 723 | 1.07× |
| 19 | 3.070 | 3.070 | 8.97 | 9.91 | 1.11× | 677 | 727 | 1.07× |

### zstd, github-dict

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 9.886 | 9.886 | 973 | 1492 | 1.53× | 3114 | 3579 | 1.15× |
| 3 | 9.922 | 9.922 | 895 | 1381 | 1.54× | 3200 | 3722 | 1.16× |
| 5 | 10.527 | 10.527 | 355 | 348 | 0.98× | 3391 | 4019 | 1.19× |
| 7 | 10.524 | 10.524 | 189 | 186 | 0.98× | 2920 | 3285 | 1.12× |
| 9 | 10.344 | 10.344 | 115 | 119 | 1.03× | 2808 | 3200 | 1.14× |
| 13 | 10.225 | 10.225 | 67.8 | 83.1 | 1.23× | 2976 | 3309 | 1.11× |
| 16 | 10.764 | 10.764 | 6.57 | 7.54 | 1.15× | 3466 | 3953 | 1.14× |
| 19 | 10.760 | 10.760 | 6.58 | 7.57 | 1.15× | 3523 | 4039 | 1.15× |

### lz4, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.101 | 2.101 | 1021 | 987 | 0.97× | 6361 | 6546 | 1.03× |
| 2 | 2.378 | 2.378 | 453 | 454 | 1.00× | 5544 | 5683 | 1.02× |
| 3 | 2.606 | 2.606 | 165 | 159 | 0.96× | 5800 | 5923 | 1.02× |
| 6 | 2.704 | 2.704 | 92.6 | 88.9 | 0.96× | 6024 | 6153 | 1.02× |
| 9 | 2.721 | 2.721 | 55.9 | 53.1 | 0.95× | 6081 | 6198 | 1.02× |
| 12 | 2.743 | 2.743 | 18.6 | 17.4 | 0.93× | 6247 | 6346 | 1.02× |

### lz4, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.069 | 2.069 | 957 | 894 | 0.93× | 5169 | 5215 | 1.01× |
| 2 | 2.251 | 2.251 | 375 | 381 | 1.02× | 5604 | 5633 | 1.01× |
| 3 | 2.409 | 2.409 | 182 | 176 | 0.97× | 5871 | 5871 | 1.00× |
| 6 | 2.467 | 2.467 | 117 | 112 | 0.96× | 6064 | 6059 | 1.00× |
| 9 | 2.476 | 2.476 | 80.7 | 73.5 | 0.91× | 6118 | 6117 | 1.00× |
| 12 | 2.490 | 2.490 | 26.3 | 24.5 | 0.93× | 6222 | 6231 | 1.00× |

### lz4, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.289 | 2.289 | 1823 | 2130 | 1.17× | 10302 | 10967 | 1.06× |
| 2 | 2.345 | 2.345 | 362 | 1230 | 3.40× | 10487 | 10996 | 1.05× |
| 3 | 2.364 | 2.364 | 279 | 636 | 2.28× | 10624 | 11056 | 1.04× |
| 6 | 2.364 | 2.364 | 280 | 633 | 2.26× | 10596 | 11056 | 1.04× |
| 9 | 2.364 | 2.364 | 279 | 479 | 1.71× | 10596 | 11026 | 1.04× |
| 12 | 2.364 | 2.364 | 104 | 114 | 1.09× | 9783 | 10174 | 1.04× |

Throughputs in MB/s of uncompressed data (10^6 bytes a second), one thread, median of the rounds. Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.
