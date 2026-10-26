const api = {
  getOrder: async (id) => ({ id, lineItems: [{ price: 5 }, { price: 7 }, { price: 9 }], total: 21 }),
};

async function checkout(id) {
  const order = await api.getOrder(id);
  const total = order.items.reduce((s, i) => s + i.price, 0);
  return total;
}

async function main() {
  const t = await checkout(7);
  console.log(t);
}

main();
