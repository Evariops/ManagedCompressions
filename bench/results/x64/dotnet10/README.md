# .NET 10 beside .NET 11, AMD Ryzen 9 7950X (x64)

The level-by-level benchmark at levels 1, 3 and 9, once on .NET 10.0.15 and once on .NET 11.0.0, in one
session on Windows 11, with its timed thread kept on one logical processor:

```sh
dotnet run -c Release -f net10.0 --project bench/ManagedCompressions.Bench -- --levels 1,3,9 --seconds 2 --core 2 --results levels-net10.tsv
dotnet run -c Release -f net11.0 --project bench/ManagedCompressions.Bench -- --levels 1,3,9 --seconds 2 --core 2 --results levels-net11.tsv
```

The native side, NativeCompressions.Zstandard 1.3.0 and NativeCompressions.LZ4 1.2.0, is the same code on
both runtimes: each managed speed is read against it, from rounds that alternate with it, so the comparison
survives the machine's drift between the two runs. The last column is .NET 10's ratio over .NET 11's: below
1.000, .NET 10 is slower.

| codec | workload | level | direction | .NET 10, managed/native | .NET 11, managed/native | .NET 10 / .NET 11 |
|:--|:--|--:|:--|--:|--:|--:|
| zstd | silesia | 1 | compression | 1.02× | 1.02× | 0.999 |
| zstd | silesia | 1 | decompression | 1.00× | 1.01× | 0.990 |
| zstd | silesia | 3 | compression | 0.96× | 0.97× | 0.989 |
| zstd | silesia | 3 | decompression | 1.08× | 1.08× | 1.000 |
| zstd | silesia | 9 | compression | 1.00× | 0.99× | 1.013 |
| zstd | silesia | 9 | decompression | 1.08× | 1.08× | 1.006 |
| zstd | silesia-64k | 1 | compression | 1.01× | 1.02× | 0.993 |
| zstd | silesia-64k | 1 | decompression | 1.01× | 1.01× | 0.996 |
| zstd | silesia-64k | 3 | compression | 0.97× | 0.98× | 0.990 |
| zstd | silesia-64k | 3 | decompression | 1.05× | 1.05× | 0.999 |
| zstd | silesia-64k | 9 | compression | 1.00× | 1.01× | 0.998 |
| zstd | silesia-64k | 9 | decompression | 1.07× | 1.06× | 1.001 |
| zstd | github | 1 | compression | 1.28× | 1.27× | 1.002 |
| zstd | github | 1 | decompression | 1.16× | 1.19× | 0.973 |
| zstd | github | 3 | compression | 1.14× | 1.16× | 0.982 |
| zstd | github | 3 | decompression | 1.10× | 1.13× | 0.975 |
| zstd | github | 9 | compression | 1.16× | 1.17× | 0.993 |
| zstd | github | 9 | decompression | 1.10× | 1.13× | 0.974 |
| zstd | github-dict | 1 | compression | 1.40× | 1.41× | 0.992 |
| zstd | github-dict | 1 | decompression | 1.11× | 1.15× | 0.961 |
| zstd | github-dict | 3 | compression | 1.43× | 1.40× | 1.017 |
| zstd | github-dict | 3 | decompression | 1.14× | 1.18× | 0.963 |
| zstd | github-dict | 9 | compression | 1.00× | 0.97× | 1.031 |
| zstd | github-dict | 9 | decompression | 1.12× | 1.15× | 0.979 |
| lz4 | silesia | 1 | compression | 0.96× | 0.97× | 0.997 |
| lz4 | silesia | 1 | decompression | 1.02× | 1.03× | 0.990 |
| lz4 | silesia | 3 | compression | 0.95× | 0.95× | 1.007 |
| lz4 | silesia | 3 | decompression | 1.01× | 1.02× | 0.995 |
| lz4 | silesia | 9 | compression | 0.95× | 0.95× | 1.004 |
| lz4 | silesia | 9 | decompression | 1.01× | 1.02× | 0.992 |
| lz4 | silesia-64k | 1 | compression | 0.94× | 0.94× | 1.001 |
| lz4 | silesia-64k | 1 | decompression | 0.98× | 1.01× | 0.975 |
| lz4 | silesia-64k | 3 | compression | 0.96× | 0.95× | 1.008 |
| lz4 | silesia-64k | 3 | decompression | 0.97× | 1.00× | 0.967 |
| lz4 | silesia-64k | 9 | compression | 0.91× | 0.91× | 1.005 |
| lz4 | silesia-64k | 9 | decompression | 0.97× | 1.01× | 0.958 |
| lz4 | github | 1 | compression | 1.15× | 1.16× | 0.996 |
| lz4 | github | 1 | decompression | 1.06× | 1.05× | 1.005 |
| lz4 | github | 3 | compression | 2.04× | 2.26× | 0.902 |
| lz4 | github | 3 | decompression | 1.03× | 1.04× | 0.992 |
| lz4 | github | 9 | compression | 1.73× | 1.70× | 1.018 |
| lz4 | github | 9 | decompression | 1.03× | 1.04× | 0.997 |

Geometric means of the last column: 1.000 for Zstandard compression, 0.985 for its decompression, 0.993 for
LZ4 compression and 0.985 for LZ4 decompression; 0.991 over every case. One case runs more than 5 % slower
on .NET 10, by 10 %: LZ4's level 3 on the JSON records (0.902), where the managed compressor is still 2.04×
as fast as native lz4, against 2.26× on .NET 11. Every output matched the
native one and read back, on both runtimes.