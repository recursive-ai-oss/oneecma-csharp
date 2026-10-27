function loadRows() {
  return { rows: [1, 2, 3] };
}
const all = [...loadRows()];
console.log(all.length);
