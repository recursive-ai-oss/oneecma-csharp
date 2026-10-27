#!/bin/sh
# Records the expected output of testdata/date/probe.js from a reference Node under each zone.
# Usage: tools/date-diff/record.sh /path/to/node   (run from the repository root)
set -eu
NODE=${1:-node}
for zone in America/Chicago Europe/Berlin UTC; do
  name=$(echo "$zone" | tr '/' '_')
  TZ=$zone "$NODE" testdata/date/probe.js > "testdata/date/expected.$name.txt"
done
"$NODE" --version > testdata/date/node-version.txt
