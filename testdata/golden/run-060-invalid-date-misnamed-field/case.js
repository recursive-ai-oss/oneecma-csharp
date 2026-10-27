const events = [{ created_at: "2024-01-05T10:00:00Z" }, { created_at: "2024-02-01T00:00:00Z" }];
const dates = events.map((e) => new Date(e.createdAt));
const latest = dates[1];
console.log(latest.toISOString());
