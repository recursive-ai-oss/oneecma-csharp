function pick(list, id) {
  for (const x of list) {
    if (x.id === id) {
      return x;
    }
  }
}
const row = pick([{ id: 1, name: "a" }], 2);
console.log(row.name);
