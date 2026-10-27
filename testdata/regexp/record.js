// Records the reference engine's answers for cases.js into expected.json.
// Usage: node record.js > expected.json   (run with the pinned Node; the version is written into the file)
console.log(require('./recorder.js').record(require('./cases.js')));
