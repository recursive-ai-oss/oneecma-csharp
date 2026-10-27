// Orders in a status that does not exist.
for (const order of api.list('Pendng')) {
  print(order.id);
}
