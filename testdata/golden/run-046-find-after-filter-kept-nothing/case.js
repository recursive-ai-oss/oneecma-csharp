const tasks = [
  { id: 1, status: "OPEN" },
  { id: 2, status: "OPEN" },
  { id: 3, status: "CLOSED" },
  { id: 4, status: "OPEN" },
  { id: 5, status: "CLOSED" },
];
const open = tasks.filter((t) => t.status === "open");
const first = open.find((t) => t.id > 0);
console.log(first.id);
