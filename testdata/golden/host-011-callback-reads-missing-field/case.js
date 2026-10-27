// The callback reads a field the order does not have.
const found = api.find(order => order.customer.name === "Ada");
