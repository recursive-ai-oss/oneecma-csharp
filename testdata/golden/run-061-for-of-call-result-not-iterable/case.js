const inventory = {
  items() {
    return { sku: "a-1", qty: 3 };
  },
};
for (const row of inventory.items()) {
  console.log(row);
}
