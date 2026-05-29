#!/usr/bin/env bash
set -uo pipefail
cd /home/stefan/RailReaderCore
H=tools/PerfHarness/bin/Release/net10.0/PerfHarness.dll
MIXED=/home/stefan/Downloads/energies-12-03569.pdf
OUT=tools/PerfHarness/rebaseline-result.txt

echo "== BUILD ==" > "$OUT"
dotnet build tools/PerfHarness/PerfHarness.csproj -c Release -nologo > /tmp/build_rb.txt 2>&1
echo "build_exit=$?" >> "$OUT"
grep -E "Build succeeded|Build FAILED|error CS" /tmp/build_rb.txt | head -12 >> "$OUT"
if grep -q "Build FAILED" /tmp/build_rb.txt; then echo "ABORT: build failed" >> "$OUT"; exit 1; fi

echo "" >> "$OUT"; echo "== loadavg at start: $(cat /proc/loadavg) ==" >> "$OUT"

echo "" >> "$OUT"; echo "== FULL PIPELINE V3 (clean absolutes) ==" >> "$OUT"
dotnet "$H" --analyzer v3 --pdf "$MIXED" --pages 8 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== DUEL V3 vs Heron (ratio re-confirm) ==" >> "$OUT"
dotnet "$H" --analyzer duel --pdf "$MIXED" --pages 8 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== PIPELINE OVERLAP A/B ==" >> "$OUT"
dotnet "$H" --analyzer pipeline --pdf "$MIXED" --pages 8 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== loadavg at end: $(cat /proc/loadavg) ==" >> "$OUT"
echo "== DONE ==" >> "$OUT"
