#!/usr/bin/env bash
# Samples the managed side of a benchmark at the instruction:
#
#   bench/jit-profile.sh <method substring> <benchmark arguments...>
#
# e.g. bench/jit-profile.sh CompressHashChain --codecs lz4 --sets silesia --levels 9 --directions compression
#
# The benchmark runs with only the managed candidate timed, the JIT writing its perf map and the
# disassembly of the methods whose name holds the substring; `sample` takes 8 seconds of it once the
# JIT has its final code, and bench/jit-pcprofile.py resolves and annotates the samples.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
method="$1"; shift
out="${TMPDIR:-/tmp}/jit-profile"
mkdir -p "$out"
rm -f "$out"/disasm.txt
dotnet build -c Release "$root/bench/ManagedCompressions.Bench" > /dev/null
DOTNET_PerfMapEnabled=1 DOTNET_JitDisasm="$method" DOTNET_JitStdOutFile="$out/disasm.txt" \
  dotnet "$root/bench/ManagedCompressions.Bench/bin/Release/net11.0/ManagedCompressions.Bench.dll" \
  --only managed --seconds "${PROFILE_RUN:-24}" "$@" > "$out/run.log" 2>&1 &
pid=$!
sleep "${PROFILE_DELAY:-12}"
sample "$pid" "${PROFILE_SECONDS:-8}" -mayDie -file "$out/sample.txt" > /dev/null 2>&1 || true
# The disassembly is buffered: the run ends on its own.
wait "$pid" 2> /dev/null || true
python3 "$root/bench/jit-pcprofile.py" "$out/sample.txt" "/tmp/perf-$pid.map" "$out/disasm.txt" "$method" --top "${PROFILE_TOP:-15}"
