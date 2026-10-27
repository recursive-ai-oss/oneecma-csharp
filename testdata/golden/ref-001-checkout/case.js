// Totals one order from the order service. api.getOrder is a host function
// that answers with { id, lineItems, total }; this script reads `items` by mistake.

async function checkout(id) {
  // fetch the order, then add up its line prices
  //
  //

  const order = await api.getOrder(id);
  //

  const total = order.items.reduce((s, i) => s + i.price, 0);
  return total;
}

async function main() {
  // report the total
  // (the checkout total is printed by the host)

  const t = await checkout(7);
  print(t);
}

main();
