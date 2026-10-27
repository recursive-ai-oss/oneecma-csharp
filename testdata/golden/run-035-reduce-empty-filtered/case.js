const orders = [{ paid: false, total: 5 }];
const unpaid = orders.filter((o) => o.paid);
const sum = unpaid.reduce((acc, o) => acc + o.total);
