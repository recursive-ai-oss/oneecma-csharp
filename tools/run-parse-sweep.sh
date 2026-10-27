export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ONEECMA_ARTIFACTS=$HOME/oneecma/u3/artifacts
mkdir -p ~/oneecma/u3/out
DOC=""
[ -f tools/test262/documented-engine.txt ] && DOC="--documented tools/test262/documented-engine.txt"
nice -n 10 dotnet run -c Release --project tools/OneEcma.Test262 -- --backend engine --parse-only --jobs 8 --quiet --depth 3 $DOC --failures ~/oneecma/u3/out/fail.txt --json ~/oneecma/u3/out/sum.json --timeout 30 "$@" > ~/oneecma/u3/out/summary.txt 2>&1
grep -E '^TOTAL|^(annexB|built-ins|intl402|language) ' ~/oneecma/u3/out/summary.txt
wc -l < ~/oneecma/u3/out/fail.txt
grep -v 'regexp\|RegExp' ~/oneecma/u3/out/fail.txt | sed 's|\t\[|  [|; s|expected SyntaxError in parse phase, but it completed|EXPECTED-ERR|; s|language/||; s|expressions/|e/|; s|statements/|s/|' | cut -c1-170 | head -${NFAIL:-60}
