const resp = { data: { items: [{ sku: "pen", qty: 2 }, { sku: "ink", qty: 1 }] }, status: 200 };

function loadLines(resp) {
  return resp.items;
}

function summarize(lines) {
  return lines.reduce((n, l) => n + l.qty, 0);
}

function report(resp) {
  const lines = loadLines(resp);
  return summarize(lines);
}

report(resp);
