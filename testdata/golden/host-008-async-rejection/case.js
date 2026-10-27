async function main() {
  const order = await api.fetchOrderAsync(99);
  print(order.id);
}

main();
