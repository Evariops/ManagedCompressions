#!/usr/bin/env bash
# A/B of two commits with ManagedCompressions.Bench, in alternate processes (bench/ab.ps1's twin):
#
#   bench/ab.sh <base> <head|.> <pairs> <bench arguments...>
#
# e.g. bench/ab.sh 6290699~1 6290699 6 --codecs zstd --sets silesia-sample --levels 1 --directions compression --seconds 6
#
# Each commit's bench is built in a worktree of its own under artifacts/ab ("." is the working tree,
# uncommitted changes included); the two builds then run in alternate processes, the base first in
# even pairs and the head first in odd ones; the working tree's bench compares their results: in each
# process the managed throughput over the native one beside it, the head's over the base's pair by
# pair, and their geometric mean with its 95% interval.
set -euo pipefail
root="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
base="$1"; head="$2"; pairs="$3"; shift 3
framework="${FRAMEWORK:-net11.0}"
work="$root/artifacts/ab"
mkdir -p "$work"
rm -f "$work"/*.tsv
worktrees=()

build() {
    local side="$1" ref="$2" src
    if [ "$ref" = "." ]; then
        src="$root"
    else
        src="$work/src-$side"
        [ -d "$src" ] && git -C "$root" worktree remove --force "$src"
        git -C "$root" worktree add --detach "$src" "$(git -C "$root" rev-parse "$ref")" > /dev/null
        worktrees+=("$src")
    fi
    echo "building $side ($ref)"
    dotnet build -c Release -f "$framework" "$src/bench/ManagedCompressions.Bench" -o "$work/bin-$side" > /dev/null
}

build base "$base"
build head "$head"

base_files=(); head_files=()
for ((i = 0; i < pairs; i++)); do
    if ((i % 2 == 0)); then order=(base head); else order=(head base); fi
    for side in "${order[@]}"; do
        echo "pair $i, $side"
        dotnet "$work/bin-$side/ManagedCompressions.Bench.dll" "$@" --results "$work/$side-$i.tsv" > /dev/null
        if [ "$side" = base ]; then base_files+=("$work/$side-$i.tsv"); else head_files+=("$work/$side-$i.tsv"); fi
    done
done

status=0
dotnet run -c Release -f "$framework" --project "$root/bench/ManagedCompressions.Bench" -- \
    --compare-base "$(IFS=';'; echo "${base_files[*]}")" --compare-head "$(IFS=';'; echo "${head_files[*]}")" || status=$?

if [ "${KEEP:-0}" != 1 ]; then
    for src in ${worktrees[@]+"${worktrees[@]}"}; do git -C "$root" worktree remove --force "$src"; done
fi
exit "$status"
