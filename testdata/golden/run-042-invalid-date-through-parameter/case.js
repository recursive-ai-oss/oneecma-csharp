const raw = "yesterday";
const when = new Date(raw);
function label(d) {
  return d.toISOString().slice(0, 10);
}
function render(row) {
  return label(when) + row;
}
render("x");
