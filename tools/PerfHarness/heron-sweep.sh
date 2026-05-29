#!/usr/bin/env bash
set -uo pipefail
cd /home/stefan/RailReaderCore
H=tools/PerfHarness/bin/Release/net10.0/PerfHarness.dll
MIXED=/home/stefan/Downloads/energies-12-03569.pdf
OUT=tools/PerfHarness/heron-sweep-result.txt

echo "== BUILD ==" > "$OUT"
dotnet build tools/PerfHarness/PerfHarness.csproj -c Release -nologo > /tmp/build_hs.txt 2>&1
echo "build_exit=$?" >> "$OUT"
grep -E "Build succeeded|Build FAILED|error CS" /tmp/build_hs.txt | head -12 >> "$OUT"
if grep -q "Build FAILED" /tmp/build_hs.txt; then echo "ABORT: build failed" >> "$OUT"; exit 1; fi
[ -f "$H" ] || { echo "NO DLL" >> "$OUT"; exit 1; }

echo "" >> "$OUT"; echo "== V3 sweep (wall-clock RunAnalysis metric) ==" >> "$OUT"
dotnet "$H" --analyzer sweep --pdf "$MIXED" --pages 6 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== HERON sweep (wall-clock RunAnalysis metric) ==" >> "$OUT"
dotnet "$H" --analyzer sweep-heron --pdf "$MIXED" --pages 6 >> "$OUT" 2>&1

echo "" >> "$OUT"; echo "== DONE ==" >> "$OUT"
