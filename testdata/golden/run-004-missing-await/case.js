async function loadUsers() {
  return [{ name: "ada" }, { name: "grace" }];
}
const users = loadUsers();
console.log(users.map((u) => u.name));
