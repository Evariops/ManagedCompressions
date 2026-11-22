# Sourced by build-brotli.sh: brotli 1.1.1's sources, downloaded once into tools/native-ref/out/ and
# checked against their SHA-256, or BROTLI_SRC=<dir> for an extracted tree. Sets `out` and `brotli_src`.
# brotli publishes no release tarball of its own: this is GitHub's archive of the v1.1.1 tag.

out="$here/out"
mkdir -p "$out"

brotli_version=1.1.1
brotli_sha256=1a84dfaa740994d19254bf78aa7d74a4a8040d1638f3d5f2cdecb188fd520da8

# The SHA-256 of a file: sha256sum where it exists (Linux), shasum elsewhere (macOS).
sha256_of() {
  if command -v sha256sum > /dev/null; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

if [[ -n "${BROTLI_SRC:-}" ]]; then
  brotli_src="$BROTLI_SRC"
else
  brotli_src="$out/brotli-$brotli_version"
  if [[ ! -d "$brotli_src" ]]; then
    tarball="$out/brotli-$brotli_version.tar.gz"
    if [[ ! -f "$tarball" ]]; then
      curl -fsSL -o "$tarball" "https://github.com/google/brotli/archive/refs/tags/v$brotli_version.tar.gz"
    fi
    if [[ "$(sha256_of "$tarball")" != "$brotli_sha256" ]]; then
      echo "$tarball does not have the SHA-256 of brotli $brotli_version" >&2
      exit 1
    fi
    tar -xzf "$tarball" -C "$out"
  fi
fi
