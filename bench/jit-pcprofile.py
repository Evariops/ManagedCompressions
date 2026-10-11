#!/usr/bin/env python3
"""Resolves macOS `sample` output against the JIT's perf map, and annotates the JIT's disassembly.

  bench/jit-profile.sh runs the benchmark with DOTNET_PerfMapEnabled=1 and DOTNET_JitDisasm, samples it
  with `sample`, and calls this script:

  bench/jit-pcprofile.py <sample.txt> <perf-PID.map> [disasm.txt] [method substring] [--top N]

Prints the methods by samples (the leaf frames of `sample`'s "top of stack" section, JIT code resolved
through the perf map, native code by `sample` itself); with a disassembly and a method substring, the
listing of the version that was sampled (found by its size), each instruction with its samples.
"""
import bisect
import collections
import re
import sys


def read_samples(path):
    """(address or symbol, count) of the leaf frames."""
    lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
    out = []
    try:
        start = next(i for i, l in enumerate(lines) if l.startswith('Sort by top of stack'))
    except StopIteration:
        return out
    for line in lines[start + 1:]:
        if not line.strip():
            break
        m = re.match(r'\s+(.*?)\s+(\d+)$', line)
        if not m:
            continue
        name, count = m.group(1), int(m.group(2))
        a = re.search(r'\[(0x[0-9a-f]+)\]', name)
        out.append((int(a.group(1), 16) if a and name.startswith('???') else re.sub(r'\s+\(in .*', '', name), count))
    return out


def read_perfmap(path):
    entries = []
    for line in open(path, encoding='utf-8', errors='replace'):
        parts = line.rstrip('\n').split(' ', 2)
        if len(parts) == 3:
            entries.append((int(parts[0], 16), int(parts[1], 16), parts[2]))
    entries.sort()
    return entries


def short(name):
    name = re.sub(r'\[[A-Za-z.]+\] ', '', name)
    name = re.sub(r'valuetype |class |instance ', '', name)
    return name


def read_listings(path):
    """Every listing of the disassembly: (header, total bytes, [(offset, text)])."""
    listings = []
    current = None
    offset = 0
    for line in open(path, encoding='utf-8', errors='replace'):
        line = line.rstrip('\n')
        if line.startswith('; Assembly listing for method'):
            current = [line[len('; Assembly listing for method '):], 0, []]
            listings.append(current)
            offset = 0
            continue
        if current is None:
            continue
        m = re.match(r'(G_M\d+_IG\d+):\s+;; offset=0x([0-9A-F]+)', line)
        if m:
            offset = int(m.group(2), 16)
            current[2].append((None, m.group(1)))
            continue
        m = re.match(r'; Total bytes of code (\d+)', line)
        if m:
            current[1] = int(m.group(1))
            continue
        stripped = line.strip()
        if not stripped or stripped.startswith(';'):
            continue
        m = re.match(r'align\s+\[(\d+) bytes', stripped)
        if m:
            offset += int(m.group(1))
            continue
        current[2].append((offset, stripped))
        offset += 4
    return listings


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    top = 25
    if '--top' in sys.argv:
        top = int(sys.argv[sys.argv.index('--top') + 1])
        args = [a for a in args if a != str(top)]
    samples = read_samples(args[0])
    perfmap = read_perfmap(args[1])
    starts = [e[0] for e in perfmap]
    by_method = collections.Counter()
    by_address = collections.defaultdict(collections.Counter)
    sizes = {}
    total = 0
    for key, count in samples:
        if isinstance(key, str):
            if key.startswith('__') or key in ('mach_msg2_trap',):
                continue
            by_method['[native] ' + key] += count
            total += count
            continue
        i = bisect.bisect_right(starts, key) - 1
        if i >= 0 and key < perfmap[i][0] + perfmap[i][1]:
            name = short(perfmap[i][2])
            by_method[name] += count
            by_address[name][key - perfmap[i][0]] += count
            sizes[name] = perfmap[i][1]
        else:
            by_method['[unknown]'] += count
        total += count
    print(f'{total} samples')
    for name, count in by_method.most_common(top):
        print(f'{count:7d} {100.0 * count / total:5.1f}%  {name}')

    if len(args) >= 4:
        listings = read_listings(args[2])
        want = args[3]
        candidates = [n for n in by_address if want in n]
        if not candidates:
            print('no sampled method matches', want)
            return
        name = max(candidates, key=lambda n: by_method[n])
        counts = by_address[name]
        size = sizes[name]
        listing = next((l for l in reversed(listings) if l[1] == size and want.split('(')[0].split('::')[-1] in l[0]), None)
        print()
        print(f'{name}: {by_method[name]} samples, {size} bytes')
        if listing is None:
            print('no listing of that size; offsets:')
            for off, c in sorted(counts.items()):
                print(f'  0x{off:04X} {c}')
            return
        print(listing[0])
        method_total = sum(counts.values())
        for off, text in listing[2]:
            if off is None:
                print(f'{text}:')
                continue
            c = counts.get(off, 0)
            mark = f'{c:6d} {100.0 * c / method_total:5.1f}%' if c else ' ' * 13
            print(f'{mark}  0x{off:04X}  {text}')


main()
