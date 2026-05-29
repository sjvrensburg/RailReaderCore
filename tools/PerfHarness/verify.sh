#!/usr/bin/env bash
set -uo pipefail
OUT=/home/stefan/RailReaderCore/tools/PerfHarness/verify-result.txt

{
echo "== CPU FREQ DRIVER STATE =="
echo "scaling_driver: $(cat /sys/devices/system/cpu/cpufreq/policy0/scaling_driver 2>/dev/null)"
echo "intel_pstate/status: $(cat /sys/devices/system/cpu/intel_pstate/status 2>/dev/null || echo n/a)"
echo "governor: $(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null)"
echo "min_freq kHz: $(cat /sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_min_freq 2>/dev/null)"
echo "max_freq kHz: $(cat /sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq 2>/dev/null)"
echo "no_turbo: $(cat /sys/devices/system/cpu/intel_pstate/no_turbo 2>/dev/null || echo n/a)"

echo ""
echo "== IDLE freq sample (cpu0-3, kHz) =="
for c in 0 1 2 3; do printf "cpu%s: %s  " "$c" "$(cat /sys/devices/system/cpu/cpu$c/cpufreq/scaling_cur_freq 2>/dev/null)"; done; echo

echo ""
echo "== UNDER-LOAD freq sample (16 busy loops, 2s, then read cpu0-3) =="
for i in $(seq 1 16); do timeout 3 bash -c 'while :; do :; done' & done
sleep 2
for c in 0 1 2 3; do printf "cpu%s: %s  " "$c" "$(cat /sys/devices/system/cpu/cpu$c/cpufreq/scaling_cur_freq 2>/dev/null)"; done; echo
wait 2>/dev/null

echo ""
echo "== current non-ours CPU hogs =="
ps -eo pcpu,comm --sort=-pcpu | grep -viE "claude|^ *0\.|VBCSCompiler|MSBuild|dotnet|ps$|grep" | head -6
echo "loadavg: $(cat /proc/loadavg)"
} > "$OUT" 2>&1
