// Prices copied from a form arrive as strings; the .NET side wants numbers.
const lines = [
  { sku: 'pen', quantity: 3, price: 1.5 },
  { sku: 'ink', quantity: 1, price: '7.25' },
];
api.addOrder({ id: 3, lineItems: lines });
