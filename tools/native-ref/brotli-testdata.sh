#!/usr/bin/env bash
# Writes brotli's test data into tests/ManagedCompressions.Brotli.Tests/testdata/: the files of
# tests/testdata at brotli's v1.1.1 tag, each original beside the streams that encode it (*.compressed,
# and *.compressed.NN from other encoders and settings). The release tarball leaves them out, so they
# come from the repository, at the tag's commit, which git checks by its hash. The repository does
# not keep them; they remain under brotli's MIT license.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
target="$here/../../tests/ManagedCompressions.Brotli.Tests/testdata"
commit=db0631dfea829194206d269bc7b25d0908cdb97d # v1.1.1

if [[ -f "$target/.commit" && "$(cat "$target/.commit")" == "$commit" ]]; then
  echo "brotli test data already at $commit"
  exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
git -C "$work" init -q
git -C "$work" remote add origin https://github.com/google/brotli.git
git -C "$work" fetch -q --depth 1 --filter=blob:none origin "$commit"
git -C "$work" sparse-checkout set tests/testdata
git -C "$work" checkout -q FETCH_HEAD
if [[ "$(git -C "$work" rev-parse HEAD)" != "$commit" ]]; then
  echo "fetched $(git -C "$work" rev-parse HEAD), not brotli v1.1.1's $commit" >&2
  exit 1
fi

rm -rf "$target"
mkdir -p "$target"
cp "$work"/tests/testdata/* "$target"/
echo "$commit" > "$target/.commit"
echo "wrote $(ls "$target" | wc -l) files into $target"
