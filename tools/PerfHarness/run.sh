#!/usr/bin/env bash
set -uo pipefail
cd /home/stefan/RailReaderCore
H=tools/PerfHarness/bin/Release/net10.0/PerfHarness.dll
MIXED=/home/stefan/Downloads/energies-12-03569.pdf
MATH=/home/stefan/Downloads/generalized-kernel-regularized-least-squares.pdf
OUT=/tmp/perf_all.txt

echo "== BUILD ==" > "$OUT"
dotnet build tools/PerfHarness/PerfHarness.csproj -c Release -nologo > /tmp/build.txt 2>&1
echo "build_exit=$?" >> "$OUT"
grep -E "Build succeeded|Build FAILED|error CS" /tmp/build.txt | head -8 >> "$OUT"
[ -f "$H" ] || { echo "NO DLL" >> "$OUT"; exit 1; }

for f in "$MIXED" "$MATH"; do [ -f "$f" ] || echo "WARN missing $f" >> "$OUT"; done

echo "" >> "$OUT"; echo "== V3  energies (mixed) ==" >> "$OUT"
dotnet "$H" --analyzer v3  --pdf "$MIXED" --pages 8 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== PP-S energies (mixed) ==" >> "$OUT"
dotnet "$H" --analyzer pps --pdf "$MIXED" --pages 8 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== V3  gKRLS (math/stats) ==" >> "$OUT"
dotnet "$H" --analyzer v3  --pdf "$MATH"  --pages 8 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== DONE ==" >> "$OUT"
