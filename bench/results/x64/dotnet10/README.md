# .NET 10 beside .NET 11, AMD Ryzen 9 7950X (x64)

The level-by-level benchmark at levels 1, 3 and 9, once on .NET 10.0.15 and once on .NET 11.0.0, in one
session on Windows 11, with its timed thread kept on one logical processor:

```sh
dotnet run -c Release -f net10.0 --project bench/ManagedCompressions.Bench -- --levels 1,3,9 --seconds 2 --core 2 --results levels-net10.tsv
dotnet run -c Release -f net11.0 --project bench/ManagedCompressions.Bench -- --levels 1,3,9 --seconds 2 --core 2 --results levels-net11.tsv
```

The native side, NativeCompressions.Zstandard 1.3.0 and NativeCompressions.LZ4 1.2.0, is the same code on
both runtimes: each managed speed is read against it round by round, in rounds that alternate with it, so
the comparison survives the machine's drift between the two runs. The last column is .NET 10's ratio over
.NET 11's: below 1.000, .NET 10 is slower.

| codec | workload | level | direction | .NET 10, managed/native | .NET 11, managed/native | .NET 10 / .NET 11 |
|:--|:--|--:|:--|--:|--:|--:|
| zstd | silesia | 1 | compression | 1.02× | 1.02× | 0.997 |
| zstd | silesia | 1 | decompression | 1.00× | 1.01× | 0.993 |
| zstd | silesia | 3 | compression | 0.96× | 0.97× | 0.990 |
| zstd | silesia | 3 | decompression | 1.08× | 1.08× | 0.994 |
| zstd | silesia | 9 | compression | 0.95× | 0.98× | 0.966 |
| zstd | silesia | 9 | decompression | 1.08× | 1.09× | 0.996 |
| zstd | silesia-64k | 1 | compression | 1.00× | 1.01× | 0.990 |
| zstd | silesia-64k | 1 | decompression | 1.00× | 1.01× | 0.991 |
| zstd | silesia-64k | 3 | compression | 0.97× | 0.98× | 0.996 |
| zstd | silesia-64k | 3 | decompression | 1.04× | 1.04× | 0.995 |
| zstd | silesia-64k | 9 | compression | 1.01× | 1.01× | 1.002 |
| zstd | silesia-64k | 9 | decompression | 1.06× | 1.06× | 0.995 |
| zstd | github | 1 | compression | 1.28× | 1.28× | 0.999 |
| zstd | github | 1 | decompression | 1.17× | 1.17× | 1.000 |
| zstd | github | 3 | compression | 1.16× | 1.16× | 1.004 |
| zstd | github | 3 | decompression | 1.11× | 1.12× | 0.992 |
| zstd | github | 9 | compression | 1.17× | 1.17× | 1.006 |
| zstd | github | 9 | decompression | 1.12× | 1.13× | 0.994 |
| zstd | github-dict | 1 | compression | 1.40× | 1.50× | 0.934 |
| zstd | github-dict | 1 | decompression | 1.13× | 1.16× | 0.974 |
| zstd | github-dict | 3 | compression | 1.34× | 1.44× | 0.929 |
| zstd | github-dict | 3 | decompression | 1.13× | 1.18× | 0.959 |
| zstd | github-dict | 9 | compression | 1.02× | 0.96× | 1.055 |
| zstd | github-dict | 9 | decompression | 1.15× | 1.18× | 0.974 |
| lz4 | silesia | 1 | compression | 0.95× | 0.96× | 0.990 |
| lz4 | silesia | 1 | decompression | 1.02× | 1.03× | 0.995 |
| lz4 | silesia | 3 | compression | 0.94× | 0.94× | 1.007 |
| lz4 | silesia | 3 | decompression | 1.02× | 1.02× | 0.995 |
| lz4 | silesia | 9 | compression | 0.96× | 0.95× | 1.011 |
| lz4 | silesia | 9 | decompression | 1.01× | 1.02× | 0.995 |
| lz4 | silesia-64k | 1 | compression | 0.94× | 0.95× | 0.990 |
| lz4 | silesia-64k | 1 | decompression | 1.01× | 1.01× | 1.001 |
| lz4 | silesia-64k | 3 | compression | 0.96× | 0.96× | 1.002 |
| lz4 | silesia-64k | 3 | decompression | 1.00× | 1.00× | 0.997 |
| lz4 | silesia-64k | 9 | compression | 0.92× | 0.92× | 1.008 |
| lz4 | silesia-64k | 9 | decompression | 1.00× | 1.00× | 0.998 |
| lz4 | github | 1 | compression | 1.15× | 1.15× | 1.006 |
| lz4 | github | 1 | decompression | 1.04× | 1.07× | 0.976 |
| lz4 | github | 3 | compression | 2.25× | 2.19× | 1.023 |
| lz4 | github | 3 | decompression | 1.02× | 1.04× | 0.987 |
| lz4 | github | 9 | compression | 1.74× | 1.68× | 1.037 |
| lz4 | github | 9 | decompression | 1.02× | 1.04× | 0.984 |

Geometric means of the last column: 0.988 for Zstandard compression, 0.988 for its decompression, 1.008 for
LZ4 compression and 0.992 for LZ4 decompression; 0.993 over every case. Two cases run more than 5 % slower
on .NET 10, by 7 %: Zstandard's compression of the JSON records with their dictionary at levels 1 and 3
(0.934 and 0.929). Those two vary as much from one process to the next on one runtime: on .NET 11 they came
out at 1.41× and 1.40× in the previous comparison, 1.44× and 1.45× in the level-by-level results, and 1.50×
and 1.44× here. The case the previous comparison found 10 % slower, LZ4's level 3 on the JSON records, came
out 2 % faster. Every output matched the native one and read back, on both runtimes.
