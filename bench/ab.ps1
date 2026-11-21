<#
.SYNOPSIS
A/B of two commits with ManagedCompressions.Bench, in alternate processes.

.DESCRIPTION
Builds the bench of each commit in a worktree of its own under artifacts/ab, then runs the two builds
in alternate processes, -Pairs times (the base first in even pairs, the head first in odd ones), each
process writing its results. The bench of the working tree then compares them: in each process the
managed throughput over the native one measured beside it, which cancels what the machine did during
that process; the head's over the base's, pair by pair; their geometric mean with its 95% interval.

-Head . measures the working tree itself, uncommitted changes included. -Bench holds the bench's
arguments, as one string.

.EXAMPLE
bench/ab.ps1 -Base 6290699~1 -Head 6290699 -Pairs 6 -Bench '--codecs zstd --sets silesia-sample --levels 1 --directions compression --seconds 6 --core 2'
#>
param(
    [Parameter(Mandatory)] [string] $Base,
    [string] $Head = '.',
    [int] $Pairs = 6,
    [string] $Framework = 'net11.0',
    [switch] $Keep,
    [Parameter(Mandatory)] [string] $Bench
)

$ErrorActionPreference = 'Stop'
$benchArgs = $Bench -split '\s+' | Where-Object { $_ }
# The root from the script's own path: git's output would come back in the console's code page, which
# mangles a path that is not ASCII.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $root 'artifacts/ab'
New-Item -ItemType Directory -Force $work | Out-Null
Get-ChildItem $work -Filter '*.tsv' | Remove-Item

$refs = @{ base = $Base; head = $Head }
$worktrees = @()
foreach ($side in 'base', 'head') {
    if ($refs[$side] -eq '.') {
        $src = $root
    }
    else {
        $commit = (git -C $root rev-parse $refs[$side]).Trim()
        $src = Join-Path $work "src-$side"
        if (Test-Path $src) { git -C $root worktree remove --force $src }
        git -C $root worktree add --detach $src $commit | Out-Null
        $worktrees += $src
    }

    Write-Host "building $side ($($refs[$side]))"
    $log = dotnet build -c Release -f $Framework (Join-Path $src 'bench/ManagedCompressions.Bench') -o (Join-Path $work "bin-$side") 2>&1
    if ($LASTEXITCODE) {
        $log | Select-Object -Last 20 | Write-Host
        throw "the build of $side failed"
    }
}

$files = @{ base = @(); head = @() }
for ($i = 0; $i -lt $Pairs; $i++) {
    $order = if ($i % 2 -eq 0) { 'base', 'head' } else { 'head', 'base' }
    foreach ($side in $order) {
        $tsv = Join-Path $work "$side-$i.tsv"
        Write-Host "pair $i, $side"
        dotnet (Join-Path $work "bin-$side/ManagedCompressions.Bench.dll") @benchArgs --results $tsv | Out-Null
        if ($LASTEXITCODE) { throw "the $side process of pair $i failed" }
        $files[$side] += $tsv
    }
}

dotnet run -c Release -f $Framework --project (Join-Path $root 'bench/ManagedCompressions.Bench') -- `
    --compare-base ($files.base -join ';') --compare-head ($files.head -join ';')
$status = $LASTEXITCODE

if (-not $Keep) {
    foreach ($src in $worktrees) { git -C $root worktree remove --force $src }
}

exit $status
