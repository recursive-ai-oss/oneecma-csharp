using System.Diagnostics;
using System.Globalization;

namespace RecursiveAI.OneEcma.Benchmarks;

/// <summary>
/// The page-bundle rows: lodash utility code, the minified preact module and a React server render. Each is one plain script whose
/// completion value is a string; the expected string is whatever the reference Node prints for the same script, so a row is only
/// timed once both engines agree with Node byte for byte.
/// </summary>
internal static class BundleWorkloads
{
    private static readonly Dictionary<string, string> NodeCache = new();

    private static string InputsDir => Environment.GetEnvironmentVariable("ONEECMA_BENCH_INPUTS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "oneecma", "bench-inputs");

    /// <summary>The rows whose inputs have been fetched.</summary>
    internal static IEnumerable<(string Name, string Source)> All()
    {
        string lodash = Path.Combine(InputsDir, "lodash-4.18.1", "lodash.js");
        if (File.Exists(lodash))
        {
            yield return ("lodash", File.ReadAllText(lodash) + "\n" + LodashWorkload);
        }

        string preact = Path.Combine(InputsDir, "preact-11.0.2", "dist", "preact.mjs");
        if (File.Exists(preact))
        {
            string? script = PreactScript(File.ReadAllText(preact));
            if (script is not null)
            {
                yield return ("preact", script);
            }
        }

        string react = Path.Combine(InputsDir, "react-19.3.1", "cjs", "react.production.js");
        string dom = Path.Combine(InputsDir, "react-dom-19.3.1", "cjs", "react-dom.production.js");
        string server = Path.Combine(InputsDir, "react-dom-19.3.1", "cjs", "react-dom-server-legacy.browser.production.js");
        if (File.Exists(react) && File.Exists(dom) && File.Exists(server))
        {
            // The packages are CommonJS: a three-line loader stands in for `require`, so both engines run the shipped production files
            // untouched.
            string loader = "var __cache = {}, __defs = {};\n"
                + Define("react", File.ReadAllText(react))
                + Define("react-dom", File.ReadAllText(dom))
                + Define("react-dom/server", File.ReadAllText(server))
                + "function require(name) { var m = __cache[name]; if (m) return m.exports; m = __cache[name] = { exports: {} }; __defs[name].call(m.exports, m, m.exports, require); return m.exports; }\n"
                + "var React = require('react'); var ReactDOMServer = require('react-dom/server');\n";
            yield return ("react-ssr", loader + AppSource() + "\n" + ReactWorkload);
        }
    }

    private static string AppSource()
    {
        using var s = typeof(BundleWorkloads).Assembly.GetManifestResourceStream("react-ssr-app.js")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string Define(string name, string text) =>
        "__defs['" + name + "'] = function (module, exports, require) {\n" + text + "\n};\n";

    /// <summary>The module's trailing export list becomes an object so both engines can run it as a plain script.</summary>
    private static string? PreactScript(string text)
    {
        int e = text.LastIndexOf("export{", StringComparison.Ordinal);
        int close = e < 0 ? -1 : text.IndexOf('}', e);
        if (e < 0 || close < e)
        {
            return null;
        }

        var members = new List<string>();
        foreach (string item in text.Substring(e + 7, close - e - 7).Split(','))
        {
            string[] p = item.Split(" as ", StringSplitOptions.TrimEntries);
            members.Add(p.Length == 2 ? p[1] + ":" + p[0] : p[0] + ":" + p[0]);
        }

        return text[..e] + "var preact = {" + string.Join(",", members) + "};\n" + PreactWorkload;
    }

    /// <summary>What the reference Node prints for the script's completion value (cached; the reference is run as a black box).</summary>
    internal static string NodeResult(string source)
    {
        string key = source.Length.ToString(CultureInfo.InvariantCulture) + ":" + source.GetHashCode();
        if (NodeCache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        string node = Environment.GetEnvironmentVariable("ONEECMA_NODE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "oneecma", "ref", "node-v24.22.1", "bin", "node");
        string file = Path.Combine(Path.GetTempPath(), "oneecma-bench-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(file, source);
        try
        {
            var psi = new ProcessStartInfo(node) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            // A fresh context, like a bare engine: no require, process or timers leak into the script.
            psi.ArgumentList.Add("const vm=require('vm'),fs=require('fs');process.stdout.write(String(vm.runInNewContext(fs.readFileSync(process.argv[1],'utf8'))))");
            psi.ArgumentList.Add(file);
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException("reference Node failed: " + err.Result);
            }

            NodeCache[key] = output;
            return output;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private const string LodashWorkload = """
        (function () {
          var rows = [];
          for (var i = 0; i < 1000; i++) {
            rows.push({ id: i, name: 'user_name_' + (i * 7 % 101), dept: ['eng', 'ops', 'sales', 'hr'][i % 4], score: (i * 37) % 100, tags: ['t' + (i % 5), 'u' + (i % 3)], nested: { a: { b: i % 10 } } });
          }
          var byDept = _.mapValues(_.groupBy(rows, 'dept'), function (g) {
            return { n: g.length, avg: _.round(_.meanBy(g, 'score'), 2), top: _.map(_.take(_.orderBy(g, ['score', 'id'], ['desc', 'asc']), 3), 'id') };
          });
          var uniqTags = _.sortBy(_.uniq(_.flatMap(rows, 'tags')));
          var chunks = _.map(_.chunk(_.range(50), 7), _.sum);
          var cased = _.map(_.take(_.uniq(_.map(rows, 'name')), 8), function (s) { return [_.camelCase(s), _.kebabCase(s), _.startCase(s)]; });
          var clone = _.cloneDeep(rows.slice(0, 100));
          clone[0].nested.a.b = 99;
          var merged = _.merge({}, { a: { b: 1, c: [1, 2] } }, { a: { c: [3], d: 4 } });
          var tpl = _.template('<% _.forEach(items, function (it) { %><li><%- it.name %>:<%= it.score %></li><% }); %>');
          var html = tpl({ items: rows.slice(0, 50) });
          var eq = _.isEqual(rows, _.cloneDeep(rows));
          var squares = _.sum(_.map(_.filter(_.range(1, 2000), function (n) { return n % 3 === 0; }), function (n) { return n * n; }));
          return JSON.stringify({ byDept: byDept, uniqTags: uniqTags, chunks: chunks, cased: cased, orig: rows[0].nested.a.b, cl: clone[0].nested.a.b,
            merged: merged, html: html, eq: eq, squares: squares, pick: _.pick(rows[5], ['id', 'dept']), dif: _.difference(_.range(30), _.range(0, 30, 3)).length });
        })();
        """;

    // preact has no string renderer of its own (that is a separate package) and its DOM renderer needs a document, so the workload
    // builds a virtual tree with the real h/cloneElement/toChildArray, walks it with a small serializer of ours and returns the HTML.
    private const string PreactWorkload = """
        (function () {
          function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
          function ser(v) {
            if (v == null || typeof v === 'boolean') return '';
            if (typeof v === 'string' || typeof v === 'number') return esc(v);
            if (Array.isArray(v)) return v.map(ser).join('');
            var t = v.type, p = v.props;
            if (typeof t === 'function') {
              if (t.prototype && t.prototype.render) { var c = new t(p); c.props = p; c.state = c.state || {}; return ser(c.render(p, c.state)); }
              return ser(t(p));
            }
            var a = '';
            for (var k in p) {
              if (k === 'children') continue;
              var val = p[k];
              if (k === 'style' && typeof val === 'object') { var css = ''; for (var s in val) css += s.replace(/[A-Z]/g, function (m) { return '-' + m.toLowerCase(); }) + ':' + val[s] + ';'; val = css; }
              a += ' ' + (k === 'className' ? 'class' : k) + '="' + esc(val) + '"';
            }
            return '<' + t + a + '>' + ser(preact.toChildArray(p.children)) + '</' + t + '>';
          }
          var h = preact.h;
          class Badge extends preact.Component { render(props) { return h('span', { className: 'tag' }, props.label); } }
          function Row(props) {
            var item = props.item;
            return h('tr', { className: item.id % 2 ? 'odd' : 'even' },
              h('td', null, item.id), h('td', null, item.name),
              h('td', { style: { textAlign: 'right' } }, item.price.toFixed(2)),
              h('td', null, item.tags.map(function (t) { return h(Badge, { key: t, label: t }); })));
          }
          var items = [];
          for (var i = 0; i < 300; i++) items.push({ id: i, name: 'Product ' + i + ' <special & "quoted">', price: i * 1.25 + 0.99, tags: ['a' + (i % 3), 'b' + (i % 5)] });
          var rows = items.map(function (item) { return h(Row, { key: item.id, item: item }); });
          var emphasised = rows.map(function (r, n) { return n % 10 === 0 ? preact.cloneElement(r, { item: { id: r.props.item.id, name: 'Featured ' + n, price: 1, tags: [] } }) : r; });
          var tree = h('table', { id: 'products' }, h('tbody', null, emphasised, null, false));
          return ser(tree) + '|' + preact.isValidElement(tree) + '|' + preact.toChildArray([rows, [null, 'x']]).length;
        })();
        """;

    private const string ReactWorkload = """
        (function () {
          var page = renderPage();
          var fragment = ReactDOMServer.renderToStaticMarkup(React.createElement(Table, { items: makeItems(40) }));
          return page + '\n' + fragment;
        })();
        """;
}
