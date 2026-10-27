const event = { id: 1, timestamp: 1700000000 };
const when = new Date(event.time * 1000);
console.log(when.toISOString());
