namespace RecursiveAI.OneEcma.Benchmarks;

/// <summary>Small kernel sources. Each is plain ES5-style code, so every engine runs the same text.</summary>
public static class BenchScripts
{
    /// <summary>Name, source and the expected completion value of each benchmark.</summary>
    public static IReadOnlyList<(string Name, string Source, object Expected)> All { get; } =
    [
        ("fib25", Fib, 75025.0),
        ("loops", Loops, LoopsExpected),
        ("props", Props, PropsExpected),
        ("closures", Closures, ClosuresExpected),
        ("strings", Strings, StringsExpected),
        ("vectors", Vectors, VectorsExpected),
        ("calc", Calc, CalcExpected),
        ("qsort", QSort, QSortExpected),
        ("throws", Throws, ThrowsExpected),
    ];

    public const string Fib = """
        function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
        fib(25);
        """;

    public const string Loops = """
        function main() {
          var s = 0;
          for (var i = 0; i < 1000; i++) {
            for (var j = 0; j < 1000; j++) {
              s = (s + i * j) % 1000003;
            }
          }
          return s;
        }
        main();
        """;

    // Computed in C# below so the expectation does not depend on any engine.
    private static double LoopsExpected
    {
        get
        {
            double s = 0;
            for (int i = 0; i < 1000; i++)
            {
                for (int j = 0; j < 1000; j++)
                {
                    s = (s + (double)i * j) % 1000003;
                }
            }

            return s;
        }
    }

    public const string Props = """
        function main() {
          var o = { a: 1, b: 2, c: 3 };
          var p = { x: 0, y: 0 };
          for (var i = 0; i < 300000; i++) {
            o.a = (o.a + o.b) & 65535;
            o.c = o.a ^ i;
            p.x = p.x + 1;
            p.y = o.c & 255;
          }
          return o.a + o.c + p.x + p.y;
        }
        main();
        """;

    private static double PropsExpected
    {
        get
        {
            int a = 1, b = 2, c, x = 0, y = 0;
            c = 3;
            for (int i = 0; i < 300000; i++)
            {
                a = (a + b) & 65535;
                c = a ^ i;
                x++;
                y = c & 255;
            }

            return (double)a + c + x + y;
        }
    }

    public const string Closures = """
        function makeCounter() {
          var c = 0;
          return function () { c = c + 1; return c; };
        }
        function map(a, f) {
          var r = [];
          for (var i = 0; i < a.length; i++) r.push(f(a[i]));
          return r;
        }
        function filter(a, f) {
          var r = [];
          for (var i = 0; i < a.length; i++) { if (f(a[i])) r.push(a[i]); }
          return r;
        }
        function main() {
          var a = [];
          for (var i = 0; i < 1000; i++) a.push(i);
          var total = 0;
          for (var k = 0; k < 100; k++) {
            var m = map(a, function (x) { return x * 2; });
            var f = filter(m, function (x) { return x % 3 === 0; });
            total += f.length;
          }
          var counter = makeCounter();
          for (var j = 0; j < 100000; j++) counter();
          return total + counter();
        }
        main();
        """;

    private static double ClosuresExpected
    {
        get
        {
            int total = 0;
            for (int k = 0; k < 100; k++)
            {
                for (int i = 0; i < 1000; i++)
                {
                    if ((i * 2) % 3 == 0)
                    {
                        total++;
                    }
                }
            }

            return total + 100001;
        }
    }

    public const string Strings = """
        function main() {
          var s = "";
          for (var i = 0; i < 20000; i++) { s = s + "ab" + i; }
          var n = 0;
          for (var j = 0; j < s.length; j += 7) { n += s.charCodeAt(j); }
          return n + s.length;
        }
        main();
        """;

    private static double StringsExpected
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 20000; i++)
            {
                sb.Append("ab").Append(i);
            }

            string s = sb.ToString();
            double n = 0;
            for (int j = 0; j < s.Length; j += 7)
            {
                n += s[j];
            }

            return n + s.Length;
        }
    }

    public const string Vectors = """
        function Vec(x, y) { this.x = x; this.y = y; }
        Vec.prototype.add = function (o) { return new Vec(this.x + o.x, this.y + o.y); };
        Vec.prototype.len2 = function () { return this.x * this.x + this.y * this.y; };
        function main() {
          var v = new Vec(0, 0);
          var d = new Vec(1, 2);
          for (var i = 0; i < 200000; i++) { v = v.add(d); }
          return v.len2();
        }
        main();
        """;

    private const double VectorsExpected = 200000.0 * 200000 + 400000.0 * 400000;

    // A recursive-descent calculator over a string: charCodeAt, objects as parser state, mutual recursion.
    public const string Calc = """
        function Parser(s) { this.s = s; this.pos = 0; }
        function peek(p) { return p.s.charCodeAt(p.pos); }
        function number(p) {
          var v = 0;
          while (peek(p) >= 48 && peek(p) <= 57) { v = v * 10 + (peek(p) - 48); p.pos++; }
          return v;
        }
        function factor(p) {
          if (peek(p) === 40) { p.pos++; var v = expr(p); p.pos++; return v; }
          return number(p);
        }
        function term(p) {
          var v = factor(p);
          while (peek(p) === 42 || peek(p) === 47) {
            var op = peek(p); p.pos++;
            var r = factor(p);
            v = op === 42 ? v * r : Math.floor(v / r);
          }
          return v;
        }
        function expr(p) {
          var v = term(p);
          while (peek(p) === 43 || peek(p) === 45) {
            var op = peek(p); p.pos++;
            var r = term(p);
            v = op === 43 ? v + r : v - r;
          }
          return v;
        }
        function main() {
          var src = "(1+2)*3-4/2+(10*(2+3))-7*(8-6)+123*4";
          var total = 0;
          for (var i = 0; i < 3000; i++) { total += expr(new Parser(src)); }
          return total;
        }
        main();
        """;

    private const double CalcExpected = 3000.0 * (9 - 2 + 50 - 14 + 492);

    public const string QSort = """
        function sort(a, lo, hi) {
          while (lo < hi) {
            var p = a[(lo + hi) >> 1], i = lo, j = hi;
            while (i <= j) {
              while (a[i] < p) i++;
              while (a[j] > p) j--;
              if (i <= j) { var t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
            }
            if (j - lo < hi - i) { sort(a, lo, j); lo = i; } else { sort(a, i, hi); hi = j; }
          }
        }
        function main() {
          var seed = 12345, a = [];
          for (var i = 0; i < 40000; i++) { seed = (seed * 1103515245 + 12345) & 2147483647; a.push(seed % 100000); }
          sort(a, 0, a.length - 1);
          var ok = 1;
          for (var k = 1; k < a.length; k++) { if (a[k - 1] > a[k]) ok = 0; }
          return ok * 1000000 + a[0] + a[a.length - 1];
        }
        main();
        """;

    private static double QSortExpected
    {
        get
        {
            // Mirrors the script's double arithmetic: the product exceeds 2^31, so it goes through ToInt32.
            double seed = 12345;
            var a = new List<double>();
            for (int i = 0; i < 40000; i++)
            {
                seed = ToInt32((seed * 1103515245) + 12345) & 2147483647;
                a.Add(seed % 100000);
            }

            a.Sort();
            return 1000000 + a[0] + a[^1];
        }
    }

    private static int ToInt32(double d)
    {
        double t = Math.Truncate(d);
        double m = t % 4294967296.0;
        if (m < 0)
        {
            m += 4294967296.0;
        }

        return unchecked((int)(uint)m);
    }

    public const string Throws = """
        function risky(i) { if (i % 2 === 0) { throw new Error("even"); } return i; }
        function main() {
          var caught = 0, sum = 0;
          for (var i = 0; i < 20000; i++) {
            try { sum += risky(i); } catch (e) { caught++; }
          }
          return caught * 1000000 + sum;
        }
        main();
        """;

    private static double ThrowsExpected
    {
        get
        {
            double sum = 0;
            for (int i = 1; i < 20000; i += 2)
            {
                sum += i;
            }

            return 10000.0 * 1000000 + sum;
        }
    }
}
