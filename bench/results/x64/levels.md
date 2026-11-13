# Level by level, AMD Ryzen 9 7950X (x64), JIT

`bench/ManagedCompressions.Bench`: NativeCompressions.Zstandard 1.3.0 (zstd 1.5.7) and NativeCompressions.LZ4
1.2.0 (lz4 1.10.0) against the managed codecs, timed in alternating rounds on one thread, each figure the
median of its rounds, in MB/s of uncompressed data. × is the managed speed over the native speed.

The machine is an AMD Ryzen 9 7950X (Zen 4, x86-64 with AVX-512) under Windows 11 (build 26300), .NET
11.0.0. The process runs at a high priority, its timed thread kept on logical processor 2.
NativeCompressions' win-x64 libraries are built with GCC 16.2 (MSYS2), the same files as in its 1.1.0: its
libzstd has zstd's assembly Huffman decoder and its BMI2 code, which the arm64 build has no counterpart of.

```sh
dotnet run -c Release -f net11.0 --project bench/ManagedCompressions.Bench -- --codecs zstd,lz4 --seconds 3 --core 2
```

BenchmarkDotNet ran at the same time on the processor's other eight cores, the other CCD with its own L3
cache. The native speeds came out at 0.985 to 1.10 times those of the previous run, made alone, 1.015 on
average. LZ4's 64 KiB pages were decompressed at 0.86× to 0.89× in that run, and at 0.98× to 1.02× in
three more processes and in the .NET 10 comparison's .NET 11 run: one process in five ran that case
slower, as one earlier .NET 11 run had. Its rows here are the second of the three processes.
### zstd, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.896 | 2.896 | 706 | 710 | 1.01× | 2155 | 2167 | 1.01× |
| 3 | 3.205 | 3.205 | 446 | 432 | 0.97× | 1880 | 2041 | 1.09× |
| 5 | 3.378 | 3.378 | 222 | 219 | 0.99× | 1851 | 2035 | 1.10× |
| 7 | 3.518 | 3.518 | 146 | 145 | 1.00× | 2003 | 2183 | 1.09× |
| 9 | 3.588 | 3.588 | 113 | 113 | 1.00× | 2040 | 2218 | 1.09× |
| 13 | 3.674 | 3.674 | 25.0 | 25.8 | 1.03× | 2110 | 2274 | 1.08× |
| 16 | 3.834 | 3.834 | 10.5 | 11.3 | 1.08× | 2038 | 2188 | 1.07× |
| 19 | 4.011 | 4.011 | 5.29 | 5.19 | 0.98× | 1882 | 2045 | 1.09× |

### zstd, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.763 | 2.763 | 684 | 697 | 1.02× | 1936 | 1965 | 1.02× |
| 3 | 2.882 | 2.882 | 520 | 500 | 0.96× | 1815 | 1903 | 1.05× |
| 5 | 2.980 | 2.980 | 209 | 202 | 0.96× | 1625 | 1755 | 1.08× |
| 7 | 3.056 | 3.056 | 131 | 128 | 0.98× | 1775 | 1887 | 1.06× |
| 9 | 3.078 | 3.078 | 101 | 99.6 | 0.99× | 1805 | 1924 | 1.07× |
| 13 | 3.099 | 3.099 | 31.1 | 31.4 | 1.01× | 1731 | 1837 | 1.06× |
| 16 | 3.244 | 3.244 | 12.4 | 13.6 | 1.09× | 1524 | 1633 | 1.07× |
| 19 | 3.258 | 3.258 | 6.00 | 6.61 | 1.10× | 1518 | 1635 | 1.08× |

### zstd, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.866 | 2.866 | 334 | 428 | 1.28× | 928 | 1101 | 1.19× |
| 3 | 2.992 | 2.992 | 306 | 354 | 1.16× | 670 | 750 | 1.12× |
| 5 | 3.019 | 3.019 | 186 | 198 | 1.06× | 671 | 751 | 1.12× |
| 7 | 3.019 | 3.019 | 174 | 188 | 1.08× | 671 | 751 | 1.12× |
| 9 | 3.019 | 3.019 | 131 | 154 | 1.18× | 671 | 750 | 1.12× |
| 13 | 3.070 | 3.070 | 38.2 | 41.2 | 1.08× | 682 | 759 | 1.11× |
| 16 | 3.063 | 3.063 | 11.6 | 12.8 | 1.10× | 680 | 762 | 1.12× |
| 19 | 3.070 | 3.070 | 9.08 | 9.95 | 1.10× | 681 | 769 | 1.13× |

### zstd, github-dict

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 9.886 | 9.886 | 1007 | 1442 | 1.43× | 3145 | 3560 | 1.13× |
| 3 | 9.922 | 9.922 | 932 | 1362 | 1.46× | 3290 | 3414 | 1.04× |
| 5 | 10.527 | 10.527 | 365 | 353 | 0.97× | 3446 | 4000 | 1.16× |
| 7 | 10.524 | 10.524 | 192 | 191 | 1.00× | 3004 | 3408 | 1.13× |
| 9 | 10.344 | 10.344 | 118 | 118 | 0.99× | 2906 | 3251 | 1.12× |
| 13 | 10.225 | 10.225 | 69.0 | 83.3 | 1.21× | 3013 | 3374 | 1.12× |
| 16 | 10.764 | 10.764 | 6.66 | 7.62 | 1.14× | 3544 | 4125 | 1.16× |
| 19 | 10.760 | 10.760 | 6.67 | 7.56 | 1.13× | 3481 | 4031 | 1.16× |

### lz4, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.101 | 2.101 | 1006 | 987 | 0.98× | 6399 | 6521 | 1.02× |
| 2 | 2.378 | 2.378 | 463 | 463 | 1.00× | 5570 | 5676 | 1.02× |
| 3 | 2.606 | 2.606 | 168 | 160 | 0.95× | 5827 | 5931 | 1.02× |
| 6 | 2.704 | 2.704 | 94.2 | 89.7 | 0.95× | 6054 | 6154 | 1.02× |
| 9 | 2.721 | 2.721 | 56.8 | 54.0 | 0.95× | 6100 | 6212 | 1.02× |
| 12 | 2.743 | 2.743 | 18.9 | 17.5 | 0.93× | 6279 | 6347 | 1.01× |

### lz4, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.069 | 2.069 | 965 | 907 | 0.94× | 5175 | 5188 | 1.00× |
| 2 | 2.251 | 2.251 | 378 | 382 | 1.01× | 5621 | 5511 | 0.98× |
| 3 | 2.409 | 2.409 | 185 | 178 | 0.96× | 5880 | 5831 | 0.99× |
| 6 | 2.467 | 2.467 | 119 | 113 | 0.95× | 6066 | 6004 | 0.99× |
| 9 | 2.476 | 2.476 | 81.4 | 74.7 | 0.92× | 6116 | 6059 | 0.99× |
| 12 | 2.490 | 2.490 | 26.6 | 24.8 | 0.93× | 6218 | 6173 | 0.99× |

### lz4, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.289 | 2.289 | 1834 | 2127 | 1.16× | 10407 | 11026 | 1.06× |
| 2 | 2.345 | 2.345 | 369 | 1255 | 3.40× | 10596 | 11086 | 1.05× |
| 3 | 2.364 | 2.364 | 293 | 646 | 2.20× | 10624 | 10996 | 1.04× |
| 6 | 2.364 | 2.364 | 293 | 639 | 2.18× | 10624 | 10967 | 1.03× |
| 9 | 2.364 | 2.364 | 293 | 502 | 1.72× | 10652 | 11056 | 1.04× |
| 12 | 2.364 | 2.364 | 108 | 115 | 1.06× | 9854 | 10225 | 1.04× |

Throughputs in MB/s of uncompressed data (10^6 bytes a second), one thread, median of the rounds. Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.
