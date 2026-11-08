# .NET 10 beside .NET 11, AMD Ryzen 9 7950X (x64)

The level-by-level benchmark at levels 1, 3 and 9, once on .NET 10.0.14 and once on .NET 11.0.0-rc.1,
in one session on Windows 11, with its timed thread kept on one logical processor:

```sh
dotnet run -c Release -f net10.0 --project bench/ManagedCompressions.Bench -- --levels 1,3,9 --seconds 2 --core 2 --results levels-net10.tsv
dotnet run -c Release -f net11.0 --project bench/ManagedCompressions.Bench -- --levels 1,3,9 --seconds 2 --core 2 --results levels-net11.tsv
```

The native side, NativeCompressions 1.1.0, is the same code on both runtimes: each managed speed is read
against it, from rounds that alternate with it, so the comparison survives the machine's drift between
the two runs. The last column is .NET 10's ratio over .NET 11's: below 1.000, .NET 10 is slower.

| codec | workload | level | direction | .NET 10, managed/native | .NET 11, managed/native | .NET 10 / .NET 11 |
|:--|:--|--:|:--|--:|--:|--:|
| zstd | silesia | 1 | compression | 0.97× | 0.98× | 0.991 |
| zstd | silesia | 1 | decompression | 1.00× | 1.01× | 0.988 |
| zstd | silesia | 3 | compression | 0.95× | 0.98× | 0.964 |
| zstd | silesia | 3 | decompression | 1.08× | 1.08× | 0.997 |
| zstd | silesia | 9 | compression | 0.96× | 0.98× | 0.979 |
| zstd | silesia | 9 | decompression | 1.08× | 1.08× | 1.008 |
| zstd | silesia-64k | 1 | compression | 0.97× | 0.98× | 0.990 |
| zstd | silesia-64k | 1 | decompression | 1.00× | 0.99× | 1.013 |
| zstd | silesia-64k | 3 | compression | 0.97× | 0.95× | 1.021 |
| zstd | silesia-64k | 3 | decompression | 1.04× | 1.04× | 0.995 |
| zstd | silesia-64k | 9 | compression | 1.01× | 1.01× | 1.006 |
| zstd | silesia-64k | 9 | decompression | 1.06× | 1.06× | 0.995 |
| zstd | github | 1 | compression | 1.26× | 1.28× | 0.986 |
| zstd | github | 1 | decompression | 1.17× | 1.18× | 0.993 |
| zstd | github | 3 | compression | 1.14× | 1.14× | 0.999 |
| zstd | github | 3 | decompression | 1.11× | 1.13× | 0.984 |
| zstd | github | 9 | compression | 1.15× | 1.10× | 1.045 |
| zstd | github | 9 | decompression | 1.11× | 1.13× | 0.987 |
| zstd | github-dict | 1 | compression | 1.45× | 1.57× | 0.924 |
| zstd | github-dict | 1 | decompression | 1.15× | 1.15× | 0.996 |
| zstd | github-dict | 3 | compression | 1.42× | 1.47× | 0.970 |
| zstd | github-dict | 3 | decompression | 1.15× | 1.18× | 0.967 |
| zstd | github-dict | 9 | compression | 1.02× | 1.01× | 1.006 |
| zstd | github-dict | 9 | decompression | 1.14× | 1.16× | 0.982 |
| lz4 | silesia | 1 | compression | 0.96× | 0.97× | 0.993 |
| lz4 | silesia | 1 | decompression | 1.02× | 1.02× | 1.003 |
| lz4 | silesia | 3 | compression | 0.96× | 0.96× | 0.993 |
| lz4 | silesia | 3 | decompression | 1.01× | 1.02× | 0.996 |
| lz4 | silesia | 9 | compression | 0.94× | 0.95× | 0.989 |
| lz4 | silesia | 9 | decompression | 1.01× | 1.02× | 0.998 |
| lz4 | silesia-64k | 1 | compression | 0.94× | 0.95× | 0.993 |
| lz4 | silesia-64k | 1 | decompression | 1.01× | 0.89× | 1.135 |
| lz4 | silesia-64k | 3 | compression | 0.97× | 0.97× | 1.002 |
| lz4 | silesia-64k | 3 | decompression | 1.00× | 0.88× | 1.141 |
| lz4 | silesia-64k | 9 | compression | 0.91× | 0.92× | 0.991 |
| lz4 | silesia-64k | 9 | decompression | 1.00× | 0.86× | 1.157 |
| lz4 | github | 1 | compression | 1.15× | 1.16× | 0.993 |
| lz4 | github | 1 | decompression | 1.05× | 1.06× | 0.989 |
| lz4 | github | 3 | compression | 2.21× | 2.17× | 1.019 |
| lz4 | github | 3 | decompression | 1.03× | 1.05× | 0.981 |
| lz4 | github | 9 | compression | 1.64× | 1.77× | 0.929 |
| lz4 | github | 9 | decompression | 1.03× | 1.03× | 0.992 |

Geometric means of the last column: 0.990 for Zstandard compression, 0.992 for its decompression, 0.989
for LZ4 compression and 1.041 for LZ4 decompression. No case runs more than 8 % slower on .NET 10. The
three LZ4 cases where .NET 10 is 14 to 16 % ahead, 64 KiB pages decompressed, are .NET 11's: the
repository's .NET 11 figures for them are 1.00× to 1.01×, so that run fell short there. Every output
matched the native one and read back, on both runtimes.
