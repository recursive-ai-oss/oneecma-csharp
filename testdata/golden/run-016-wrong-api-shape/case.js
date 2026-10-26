const api = { list: async () => ({ items: [1, 2], next: null }) };
async function run() {
  const res = await api.list();
  res.forEach((x) => x);
}
run();
