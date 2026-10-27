const items = [{ price: 5 }];
const fee = 100n;
function cost(i) { return i.price; }
const t = fee * BigInt(2) + cost(items[0]);
