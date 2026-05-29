#!/usr/bin/env bash
set -uo pipefail
cd /home/stefan/RailReaderCore
H=tools/PerfHarness/bin/Release/net10.0/PerfHarness.dll
MIXED=/home/stefan/Downloads/energies-12-03569.pdf
OUT=tools/PerfHarness/duel-result.txt

echo "== BUILD ==" > "$OUT"
dotnet build tools/PerfHarness/PerfHarness.csproj -c Release -nologo > /tmp/build_duel.txt 2>&1
echo "build_exit=$?" >> "$OUT"
grep -E "Build succeeded|Build FAILED|error CS" /tmp/build_duel.txt | head -12 >> "$OUT"
if grep -q "Build FAILED" /tmp/build_duel.txt; then echo "ABORT: build failed" >> "$OUT"; exit 1; fi

# Run the paired duel three separate times (fresh process each) to also see
# cross-process variance from VM CPU-steal.
for i in 1 2 3; do
  echo "" >> "$OUT"; echo "== DUEL run $i (loadavg: $(cut -d' ' -f1-3 /proc/loadavg)) ==" >> "$OUT"
  dotnet "$H" --analyzer duel --pdf "$MIXED" --pages 8 >> "$OUT" 2>&1
done

echo "" >> "$OUT"; echo "== DONE ==" >> "$OUT"
