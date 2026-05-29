#!/usr/bin/env bash
set -uo pipefail
cd /home/stefan/RailReaderCore
H=tools/PerfHarness/bin/Release/net10.0/PerfHarness.dll
MIXED=/home/stefan/Downloads/energies-12-03569.pdf
OUT=tools/PerfHarness/sweep-result.txt

echo "== BUILD ==" > "$OUT"
dotnet build tools/PerfHarness/PerfHarness.csproj -c Release -nologo > /tmp/build_sweep.txt 2>&1
echo "build_exit=$?" >> "$OUT"
grep -E "Build succeeded|Build FAILED|error CS" /tmp/build_sweep.txt | head -12 >> "$OUT"
[ -f "$H" ] || { echo "NO DLL — aborting" >> "$OUT"; exit 1; }

echo "" >> "$OUT"
dotnet "$H" --analyzer sweep --pdf "$MIXED" --pages 6 >> "$OUT" 2>&1
echo "== DONE ==" >> "$OUT"
