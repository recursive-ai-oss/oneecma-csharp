interface User { name: string; }
function greet(user: User): string {
  return `Hello, ${user.name}!`;
}
console.log(greet({ name: "Ada" } as User);
