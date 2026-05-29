#!/usr/bin/env bash
set -uo pipefail
cd /home/stefan/RailReaderCore
H=tools/PerfHarness/bin/Release/net10.0/PerfHarness.dll
MIXED=/home/stefan/Downloads/energies-12-03569.pdf
OUT=tools/PerfHarness/full-result.txt

echo "== BUILD ==" > "$OUT"
dotnet build tools/PerfHarness/PerfHarness.csproj -c Release -nologo > /tmp/build_full.txt 2>&1
echo "build_exit=$?" >> "$OUT"
grep -E "Build succeeded|Build FAILED|error CS" /tmp/build_full.txt | head -12 >> "$OUT"
if grep -q "Build FAILED" /tmp/build_full.txt; then echo "ABORT: build failed" >> "$OUT"; exit 1; fi
[ -f "$H" ] || { echo "NO DLL" >> "$OUT"; exit 1; }

echo "" >> "$OUT"; echo "== THREAD SWEEP (V3, corrected) ==" >> "$OUT"
dotnet "$H" --analyzer sweep --pdf "$MIXED" --pages 6 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== HERON energies (mixed) ==" >> "$OUT"
dotnet "$H" --analyzer heron --pdf "$MIXED" --pages 8 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== DONE ==" >> "$OUT"
