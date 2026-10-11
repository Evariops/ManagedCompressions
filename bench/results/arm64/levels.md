# Level by level, Apple M4 Pro (arm64), JIT

`bench/ManagedCompressions.Bench`: NativeCompressions 1.1.0 (zstd 1.5.7, lz4 1.10.0) and the managed codecs,
timed in alternating rounds on one thread, each figure the median of its rounds, in MB/s of uncompressed
data. × is the managed speed over the native speed.

```sh
dotnet run -c Release --project bench/ManagedCompressions.Bench -- --codecs zstd,lz4 --seconds 3
```

### zstd, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.896 | 2.896 | 778 | 778 | 1.00× | 1895 | 2446 | 1.29× |
| 3 | 3.205 | 3.205 | 466 | 470 | 1.01× | 1700 | 2499 | 1.47× |
| 5 | 3.378 | 3.378 | 243 | 234 | 0.96× | 1684 | 2495 | 1.48× |
| 7 | 3.518 | 3.518 | 155 | 149 | 0.96× | 1826 | 2675 | 1.46× |
| 9 | 3.588 | 3.588 | 114 | 112 | 0.98× | 1863 | 2675 | 1.44× |
| 13 | 3.674 | 3.674 | 24.7 | 24.0 | 0.97× | 1982 | 2825 | 1.43× |
| 16 | 3.834 | 3.834 | 11.1 | 11.2 | 1.01× | 1828 | 2541 | 1.39× |
| 19 | 4.011 | 4.011 | 4.95 | 5.33 | 1.08× | 1779 | 2609 | 1.47× |

### zstd, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.763 | 2.763 | 672 | 682 | 1.01× | 1468 | 1851 | 1.26× |
| 3 | 2.882 | 2.882 | 485 | 491 | 1.01× | 1533 | 2089 | 1.36× |
| 5 | 2.980 | 2.980 | 226 | 216 | 0.96× | 1351 | 1969 | 1.46× |
| 7 | 3.056 | 3.056 | 134 | 128 | 0.96× | 1496 | 2131 | 1.42× |
| 9 | 3.078 | 3.078 | 100 | 95.2 | 0.95× | 1530 | 2176 | 1.42× |
| 13 | 3.099 | 3.099 | 28.6 | 29.6 | 1.04× | 1470 | 2060 | 1.40× |
| 16 | 3.244 | 3.244 | 13.6 | 13.5 | 1.00× | 1297 | 1910 | 1.47× |
| 19 | 3.258 | 3.258 | 7.07 | 6.97 | 0.98× | 1344 | 1986 | 1.48× |

### zstd, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.866 | 2.866 | 325 | 421 | 1.29× | 743 | 1106 | 1.49× |
| 3 | 2.992 | 2.992 | 305 | 364 | 1.19× | 524 | 787 | 1.50× |
| 5 | 3.019 | 3.019 | 213 | 239 | 1.12× | 531 | 800 | 1.51× |
| 7 | 3.019 | 3.019 | 204 | 223 | 1.09× | 532 | 801 | 1.50× |
| 9 | 3.019 | 3.019 | 156 | 182 | 1.17× | 530 | 799 | 1.51× |
| 13 | 3.070 | 3.070 | 40.6 | 50.2 | 1.24× | 531 | 795 | 1.50× |
| 16 | 3.063 | 3.063 | 15.5 | 18.0 | 1.16× | 539 | 801 | 1.48× |
| 19 | 3.070 | 3.070 | 12.6 | 14.4 | 1.14× | 539 | 806 | 1.50× |

### zstd, github-dict

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 9.886 | 9.886 | 1209 | 1586 | 1.31× | 3423 | 4939 | 1.44× |
| 3 | 9.922 | 9.922 | 1155 | 1555 | 1.35× | 3523 | 5145 | 1.46× |
| 5 | 10.527 | 10.527 | 456 | 442 | 0.97× | 3662 | 5461 | 1.49× |
| 7 | 10.524 | 10.524 | 245 | 228 | 0.93× | 3065 | 4349 | 1.42× |
| 9 | 10.344 | 10.344 | 148 | 137 | 0.93× | 2965 | 4219 | 1.42× |
| 13 | 10.225 | 10.225 | 72.5 | 101 | 1.39× | 3109 | 4396 | 1.41× |
| 16 | 10.764 | 10.764 | 6.35 | 8.41 | 1.33× | 3639 | 5573 | 1.53× |
| 19 | 10.760 | 10.760 | 6.42 | 8.44 | 1.31× | 3685 | 5619 | 1.52× |

### lz4, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.101 | 2.101 | 996 | 955 | 0.96× | 6158 | 6373 | 1.03× |
| 2 | 2.378 | 2.378 | 450 | 412 | 0.92× | 5281 | 5509 | 1.04× |
| 3 | 2.606 | 2.606 | 162 | 162 | 1.00× | 5590 | 5756 | 1.03× |
| 6 | 2.704 | 2.704 | 90.3 | 83.6 | 0.93× | 5742 | 5961 | 1.04× |
| 9 | 2.721 | 2.721 | 54.3 | 48.0 | 0.88× | 5812 | 6060 | 1.04× |
| 12 | 2.743 | 2.743 | 16.1 | 16.1 | 1.00× | 5501 | 5679 | 1.03× |

### lz4, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.069 | 2.069 | 818 | 776 | 0.95× | 4609 | 4790 | 1.04× |
| 2 | 2.251 | 2.251 | 336 | 308 | 0.92× | 4870 | 5039 | 1.03× |
| 3 | 2.409 | 2.409 | 176 | 167 | 0.95× | 5212 | 5324 | 1.02× |
| 6 | 2.467 | 2.467 | 115 | 106 | 0.92× | 5471 | 5587 | 1.02× |
| 9 | 2.476 | 2.476 | 77.2 | 67.3 | 0.87× | 5394 | 5516 | 1.02× |
| 12 | 2.490 | 2.490 | 25.5 | 21.9 | 0.86× | 5506 | 5636 | 1.02× |

### lz4, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.289 | 2.289 | 1647 | 2050 | 1.24× | 9902 | 10908 | 1.10× |
| 2 | 2.345 | 2.345 | 218 | 1231 | 5.66× | 10098 | 11177 | 1.11× |
| 3 | 2.364 | 2.364 | 213 | 826 | 3.87× | 10276 | 11332 | 1.10× |
| 6 | 2.364 | 2.364 | 212 | 823 | 3.88× | 10199 | 11239 | 1.10× |
| 9 | 2.364 | 2.364 | 211 | 713 | 3.38× | 10123 | 11086 | 1.10× |
| 12 | 2.364 | 2.364 | 100 | 151 | 1.51× | 9357 | 10148 | 1.08× |

Throughputs in MB/s of uncompressed data (10^6 bytes a second), one thread, median of the rounds. Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.
