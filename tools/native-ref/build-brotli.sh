#!/usr/bin/env bash
# Builds brotli 1.1.1 from its sources (brotli-source.sh) into tools/native-ref/out/libbrotli_ref.<ext>:
# the oracle of the Brotli tests and fuzzing, with the whole of brotli's API, built as .NET builds the
# brotli it ships in System.IO.Compression.Native, so that both write the same bytes.
#
# The tests fall back to .NET's library without this one, but on Linux and macOS it exports only the
# twelve functions .NET calls: not the decoder's error codes, nor the versions.
#
# Two settings decide the encoder's bytes beyond its sources, and follow .NET's build:
# - BROTLI_HAVE_LOG2. brotli's CMake build defines it from a check for log2, which fails under MSVC:
#   .NET's Windows brotli computes log(v) / ln 2 where its Linux and macOS brotli call log2(v), and the
#   two differ in the last bit for some v, enough to change a few streams at quality 10 and 11.
# - Floating-point contraction, off as .NET builds its native code (-ffp-contract=off), so that no
#   compiler fuses a multiply and an add in the encoder's costs.
#
# Windows, with no GNU-style compiler, builds it with MSVC from a Developer Command Prompt, in
# tools/native-ref/out after this script has downloaded the sources (BROTLI_DOWNLOAD_ONLY=1):
#   cl /O2 /LD /DBROTLI_HAVE_LOG2=0 /DBROTLI_SHARED_COMPILATION /DBROTLICOMMON_SHARED_COMPILATION
#      /DBROTLIDEC_SHARED_COMPILATION /DBROTLIENC_SHARED_COMPILATION /Ibrotli-1.1.1\c\include
#      brotli-1.1.1\c\common\*.c brotli-1.1.1\c\dec\*.c brotli-1.1.1\c\enc\*.c /Felibbrotli_ref.dll
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$here/brotli-source.sh"
if [[ -n "${BROTLI_DOWNLOAD_ONLY:-}" ]]; then
  echo "sources: $brotli_src"
  exit 0
fi

cc="${CC:-cc}"
echo "compiler: $($cc --version | head -1)"

pic=()
log2=1
case "$(uname -s)" in
  Darwin) shared=(-dynamiclib); ext=dylib ;;
  MINGW* | MSYS* | CYGWIN*) shared=(-shared); ext=dll; log2=0 ;;
  *) shared=(-shared); ext=so; pic=(-fPIC) ;;
esac

c="$brotli_src/c"
$cc -O2 -ffp-contract=off ${pic[@]+"${pic[@]}"} "${shared[@]}" -DBROTLI_HAVE_LOG2=$log2 \
  -DBROTLI_SHARED_COMPILATION -DBROTLICOMMON_SHARED_COMPILATION -DBROTLIDEC_SHARED_COMPILATION -DBROTLIENC_SHARED_COMPILATION \
  -I"$c/include" "$c"/common/*.c "$c"/dec/*.c "$c"/enc/*.c -lm -o "$out/libbrotli_ref.$ext"
echo "built: $out/libbrotli_ref.$ext"
