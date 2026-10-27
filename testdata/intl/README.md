Recorded output of Node v24.22.1 (ICU 78.3, CLDR 48.0, Unicode 17.0) for a deterministic grid of values x options x locales per service,
produced by `tools/intl-diff/gen-corpus.js`. `deviations.txt` lists every row where this implementation intentionally differs, with the reason.

The three date-time and duration files are stored as `name.jsonl.gz` (`gzip -9 -n`, so the bytes are reproducible); the tests read either form. After recording with `gen-corpus.js`, compress them before committing.
