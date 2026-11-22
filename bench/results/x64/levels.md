# Level by level, AMD Ryzen 9 7950X (x64), JIT

`bench/ManagedCompressions.Bench`: NativeCompressions.Zstandard 1.3.0 (zstd 1.5.7) and NativeCompressions.LZ4
1.2.0 (lz4 1.10.0) against the managed codecs, timed in alternating rounds on one thread, in MB/s of
uncompressed data, each figure the median of its rounds. × is the managed speedup over the native one,
taken round by round, with its 95% interval; an interval covers its own process only (see below).

The machine is an AMD Ryzen 9 7950X (Zen 4, x86-64 with AVX-512) under Windows 11 (build 26300), .NET
11.0.0. The process runs at a high priority, its timed thread kept on logical processor 2.
NativeCompressions' win-x64 libraries are built with GCC 16.2 (MSYS2), the same files as in its 1.1.0: its
libzstd has zstd's assembly Huffman decoder and its BMI2 code, which the arm64 build has no counterpart of.

```sh
dotnet run -c Release -f net11.0 --project bench/ManagedCompressions.Bench -- --codecs zstd,lz4 --seconds 3 --core 2
```

BenchmarkDotNet ran at the same time on the processor's other eight cores, the other CCD with its own L3
cache, as for the previous results. The native speeds came out at 0.97 to 1.12 times those of the previous
run, 1.001 on average.

The code is compiled anew in each process, and where it lands moves a few cases by several percent from
process to process. Two cases of that run came out unlike every other process that measured them: zstd's
compression of the JSON records at levels 1 and 3 (1.22× and 1.02×, against 1.28× to 1.32× and 1.15× to
1.23× in three more processes of the JSON records, and no change at all in an A/B of six pairs of processes
against the previous results' commit). The rows of zstd's JSON records, with and without the dictionary,
are the median of those three processes.

LZ4's rows are those of its safe code, which indexes spans and has no unsafe context, from a process of
their own (`--codecs lz4 --sets silesia,silesia-64k,github --levels 1,2,3,6,9,12 --seconds 2 --core 2`),
while BenchmarkDotNet ran on the other CCD. Against the unsafe code's results, decompression lost a quarter
of its speed on silesia.tar and a third on the JSON records to the bounds checks and slices; compression
lost 6% to 28%.

### zstd, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.896 | 2.896 | 710 | 721 | 1.02× (1.01–1.02) | 2166 | 2196 | 1.01× (1.01–1.02) |
| 3 | 3.205 | 3.205 | 445 | 431 | 0.97× (0.97–0.97) | 1883 | 2042 | 1.08× (1.08–1.09) |
| 5 | 3.378 | 3.378 | 222 | 219 | 0.99× (0.99–1.00) | 1851 | 2037 | 1.10× (1.10–1.10) |
| 7 | 3.518 | 3.518 | 145 | 146 | 1.01× (1.00–1.02) | 1997 | 2189 | 1.10× (1.10–1.10) |
| 9 | 3.588 | 3.588 | 113 | 113 | 1.00× (0.99–1.01) | 2041 | 2213 | 1.09× (1.08–1.09) |
| 13 | 3.674 | 3.674 | 27.1 | 27.4 | 1.01× (1.00–1.02) | 2108 | 2284 | 1.08× (1.08–1.09) |
| 16 | 3.834 | 3.834 | 11.8 | 12.0 | 1.02× (1.02–1.02) | 2033 | 2199 | 1.08× (1.08–1.08) |
| 19 | 4.011 | 4.011 | 5.21 | 5.61 | 1.07× (1.04–1.08) | 1879 | 2047 | 1.09× (1.09–1.09) |

### zstd, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.763 | 2.763 | 686 | 695 | 1.01× (1.01–1.01) | 1948 | 1970 | 1.01× (1.01–1.01) |
| 3 | 2.882 | 2.882 | 519 | 500 | 0.96× (0.96–0.96) | 1832 | 1919 | 1.05× (1.05–1.05) |
| 5 | 2.980 | 2.980 | 210 | 203 | 0.97× (0.97–0.97) | 1626 | 1758 | 1.08× (1.08–1.08) |
| 7 | 3.056 | 3.056 | 131 | 129 | 0.98× (0.98–0.98) | 1777 | 1874 | 1.05× (1.05–1.06) |
| 9 | 3.078 | 3.078 | 101 | 101 | 1.00× (1.00–1.00) | 1818 | 1935 | 1.06× (1.06–1.07) |
| 13 | 3.099 | 3.099 | 31.1 | 31.3 | 1.01× (1.01–1.01) | 1742 | 1822 | 1.05× (1.05–1.05) |
| 16 | 3.244 | 3.244 | 12.4 | 13.6 | 1.10× (1.10–1.10) | 1537 | 1649 | 1.07× (1.07–1.07) |
| 19 | 3.258 | 3.258 | 6.02 | 6.62 | 1.10× (1.10–1.10) | 1525 | 1619 | 1.06× (1.06–1.06) |

### zstd, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.866 | 2.866 | 335 | 442 | 1.32× (1.32–1.32) | 928 | 1103 | 1.19× (1.19–1.19) |
| 3 | 2.992 | 2.992 | 306 | 358 | 1.17× (1.17–1.17) | 666 | 750 | 1.13× (1.13–1.13) |
| 5 | 3.019 | 3.019 | 187 | 200 | 1.07× (1.07–1.08) | 674 | 760 | 1.13× (1.13–1.13) |
| 7 | 3.019 | 3.019 | 175 | 188 | 1.07× (1.07–1.08) | 670 | 757 | 1.13× (1.13–1.13) |
| 9 | 3.019 | 3.019 | 130 | 154 | 1.17× (1.17–1.18) | 672 | 756 | 1.13× (1.12–1.13) |
| 13 | 3.070 | 3.070 | 38.8 | 42.9 | 1.11× (1.10–1.11) | 682 | 781 | 1.15× (1.15–1.15) |
| 16 | 3.063 | 3.063 | 11.7 | 13.1 | 1.12× (1.12–1.12) | 682 | 778 | 1.14× (1.14–1.14) |
| 19 | 3.070 | 3.070 | 9.09 | 10.2 | 1.12× (1.12–1.12) | 683 | 784 | 1.15× (1.15–1.15) |

### zstd, github-dict

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 9.886 | 9.886 | 1006 | 1445 | 1.44× (1.43–1.44) | 3084 | 3563 | 1.15× (1.15–1.15) |
| 3 | 9.922 | 9.922 | 943 | 1362 | 1.45× (1.44–1.45) | 3202 | 3607 | 1.12× (1.12–1.12) |
| 5 | 10.527 | 10.527 | 367 | 363 | 0.99× (0.99–0.99) | 3397 | 4067 | 1.20× (1.20–1.20) |
| 7 | 10.524 | 10.524 | 192 | 191 | 0.99× (0.98–1.01) | 2941 | 3355 | 1.14× (1.14–1.15) |
| 9 | 10.344 | 10.344 | 119 | 121 | 1.02× (1.01–1.02) | 2841 | 3293 | 1.16× (1.16–1.16) |
| 13 | 10.225 | 10.225 | 69.0 | 84.3 | 1.22× (1.22–1.23) | 2980 | 3405 | 1.14× (1.14–1.14) |
| 16 | 10.764 | 10.764 | 6.65 | 7.59 | 1.15× (1.14–1.15) | 3502 | 4142 | 1.18× (1.17–1.18) |
| 19 | 10.760 | 10.760 | 6.64 | 7.61 | 1.15× (1.14–1.15) | 3487 | 4180 | 1.20× (1.20–1.20) |

### lz4, silesia

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.101 | 2.101 | 1026 | 787 | 0.77× (0.76–0.77) | 6384 | 4755 | 0.74× (0.74–0.75) |
| 2 | 2.378 | 2.378 | 462 | 380 | 0.82× (0.82–0.83) | 5563 | 4131 | 0.74× (0.74–0.74) |
| 3 | 2.606 | 2.606 | 167 | 140 | 0.84× (0.84–0.84) | 5827 | 4337 | 0.74× (0.74–0.74) |
| 6 | 2.704 | 2.704 | 94.1 | 79.6 | 0.85× (0.84–0.85) | 6050 | 4490 | 0.74× (0.74–0.74) |
| 9 | 2.721 | 2.721 | 56.8 | 49.6 | 0.87× (0.87–0.87) | 6108 | 4530 | 0.74× (0.74–0.74) |
| 12 | 2.743 | 2.743 | 18.9 | 16.3 | 0.87× (0.87–0.87) | 6273 | 4635 | 0.74× (0.74–0.74) |

### lz4, silesia-64k

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.069 | 2.069 | 957 | 749 | 0.78× (0.78–0.78) | 5186 | 3803 | 0.73× (0.73–0.73) |
| 2 | 2.251 | 2.251 | 377 | 326 | 0.86× (0.86–0.86) | 5633 | 4149 | 0.74× (0.73–0.74) |
| 3 | 2.409 | 2.409 | 184 | 154 | 0.84× (0.83–0.84) | 5897 | 4307 | 0.73× (0.73–0.73) |
| 6 | 2.467 | 2.467 | 119 | 98.5 | 0.83× (0.83–0.83) | 6092 | 4453 | 0.73× (0.73–0.73) |
| 9 | 2.476 | 2.476 | 81.4 | 66.6 | 0.82× (0.81–0.82) | 6156 | 4481 | 0.73× (0.73–0.73) |
| 12 | 2.490 | 2.490 | 26.7 | 22.9 | 0.86× (0.86–0.86) | 6253 | 4537 | 0.73× (0.72–0.73) |

### lz4, github

| level | ratio native | ratio managed | compression native | compression managed | × | decompression native | decompression managed | × |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| 1 | 2.289 | 2.289 | 1849 | 1558 | 0.84× (0.84–0.84) | 10381 | 7058 | 0.68× (0.68–0.68) |
| 2 | 2.345 | 2.345 | 368 | 1010 | 2.74× (2.74–2.75) | 10793 | 7120 | 0.66× (0.66–0.66) |
| 3 | 2.364 | 2.364 | 294 | 499 | 1.70× (1.70–1.70) | 10850 | 7195 | 0.66× (0.66–0.66) |
| 6 | 2.364 | 2.364 | 293 | 495 | 1.69× (1.69–1.70) | 10764 | 7157 | 0.67× (0.67–0.67) |
| 9 | 2.364 | 2.364 | 293 | 442 | 1.51× (1.51–1.51) | 10821 | 7182 | 0.66× (0.66–0.66) |
| 12 | 2.364 | 2.364 | 109 | 103 | 0.94× (0.94–0.94) | 9999 | 6548 | 0.66× (0.66–0.66) |

Throughputs in MB/s of uncompressed data (10^6 bytes a second), median of the rounds; a crowd's (a set named `x N`) are N threads' together. × is the managed speedup over the native one, paired round by round, with its 95% interval where measured. Decompression decodes the native side's output at each level. ≠ marks an output that differs from the native one where it should not, or that does not read back.
