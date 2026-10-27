#!/usr/bin/env bash
# Runs the whole benchmark suite (kernels plus the three bundle rows) and the peak-memory pass.
# Run it on a quiet machine; it samples the load average every 20 s into <output-dir>/load.log so a
# neighbouring job shows up in the record.
#   usage: run-suite.sh <benchmark-dll> <output-dir>
set -euo pipefail
dll="$1"; out="$2"; mkdir -p "$out"
node_ref="${ONEECMA_NODE:-$HOME/oneecma/ref/node-v24.22.1/bin/node}"
{ lscpu | grep -E 'Model name|^CPU\(s\)'; dotnet --version; "$node_ref" --version; echo "load before: $(cut -d' ' -f1-3 /proc/loadavg)"; } > "$out/machine.txt"
( while true; do
    echo "$(date +%T) $(cut -d' ' -f1-3 /proc/loadavg) top: $(ps -eo pcpu,comm --sort=-pcpu | sed -n 2,4p | tr -s ' ' | tr '\n' ';')" >> "$out/load.log"
    sleep 20
  done ) &
sampler=$!
trap 'kill $sampler 2>/dev/null || true' EXIT
dotnet "$dll" -f '*EngineBenchmarks*' --artifacts "$out/bdn" --exporters github json > "$out/bdn.log" 2>&1
for row in lodash preact react-ssr; do
  for engine in jint oneecma; do
    /usr/bin/time -f "$row $engine peakRSS_KB=%M" dotnet "$dll" once "$engine" "$row" 2>> "$out/rss.txt" > /dev/null
  done
done
echo "load after: $(cut -d' ' -f1-3 /proc/loadavg)" >> "$out/machine.txt"
