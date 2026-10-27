const cart = { items: [1, 2] };
const saved = undefined;
cart.items = saved;
console.log(cart.items.length);
