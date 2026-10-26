// Component tree for the server-render benchmark. Written for this repository; it builds a
// product table with nested elements using only React.createElement, so no JSX transform is
// needed. The host supplies `React` and `ReactDOMServer` from the fetched react 19.3.1 packages.
function Row(props) {
  var item = props.item;
  return React.createElement('tr', { className: item.id % 2 ? 'odd' : 'even' },
    React.createElement('td', null, item.id),
    React.createElement('td', null, item.name),
    React.createElement('td', { style: { textAlign: 'right' } }, item.price.toFixed(2)),
    React.createElement('td', null, item.tags.map(function (t) {
      return React.createElement('span', { key: t, className: 'tag' }, t);
    })));
}

function Table(props) {
  return React.createElement('table', { id: 'products' },
    React.createElement('thead', null,
      React.createElement('tr', null, ['Id', 'Name', 'Price', 'Tags'].map(function (h) {
        return React.createElement('th', { key: h }, h);
      }))),
    React.createElement('tbody', null, props.items.map(function (item) {
      return React.createElement(Row, { key: item.id, item: item });
    })));
}

function makeItems(n) {
  var items = [];
  for (var i = 0; i < n; i++) {
    items.push({ id: i, name: 'Product ' + i + ' <special & "quoted">', price: i * 1.25 + 0.99, tags: ['a' + (i % 3), 'b' + (i % 5)] });
  }
  return items;
}

function renderPage() {
  return ReactDOMServer.renderToString(
    React.createElement('main', null,
      React.createElement('h1', null, 'Products'),
      React.createElement(Table, { items: makeItems(300) })));
}
