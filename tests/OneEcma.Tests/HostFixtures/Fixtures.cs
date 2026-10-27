using System.Numerics;

namespace RecursiveAI.OneEcma.Tests.Fixtures;

[JsExport]
public static class MathBox
{
    public const int Max = 10;

    public static int Calls { get; set; }

    public static int Add(int a, int b)
    {
        Calls++;
        return a + b;
    }

    public static string Greet(string name, string greeting = "Hello") => greeting + ", " + name;

    public static int Sum(params int[] xs) => xs.Sum();

    public static string Join(IReadOnlyList<string> parts, Dictionary<string, int>? weights = null) => string.Join("+", parts) + (weights is null ? string.Empty : "/" + weights.Count);

    public static string? Echo(string? s) => s;

    public static long Big() => long.MaxValue;

    public static BigInteger Huge(BigInteger b) => b * b;

    public static double? Half(double? x) => x / 2;

    public static char Letter(char c) => char.ToUpperInvariant(c);

    public static string Pick(int n) => "int " + n;

    public static string Pick(double d) => "double " + d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Pick(string s) => "string " + s;

    public static string Pick(string s, bool flag) => "string+bool " + s + flag;

    public static string Pick(int[] xs) => "array " + xs.Length;

    public static JsValue Pass(JsValue v) => v;
}

[JsExport]
public interface IShape
{
    double Area { get; }

    string Name { get; }
}

[JsExport]
public class Circle : IShape
{
    private readonly double _r;

    public Circle(double r) => _r = r;

    public double Area => Math.PI * _r * _r;

    public string Name => "circle";

    public static Circle Unit() => new(1);
}

[JsExport]
public class Counter
{
    public Counter(int start = 0) => Value = start;

    public static readonly string Tag = "counter";

    public int Value { get; set; }

    [JsExport(Name = "bump")]
    public virtual Counter Increment()
    {
        Value++;
        return this;
    }

    [JsIgnore]
    public virtual string Hidden => "hidden";

    public virtual void Fail() => throw new InvalidOperationException("counter exploded");
}

[JsExport]
public class Animal(string name)
{
    public string Name { get; } = name;

    public virtual string Speak() => Name + " makes a sound";
}

[JsExport]
public class Dog(string name) : Animal(name)
{
    public override string Speak() => Name + " barks";

    public string Fetch() => Name + " fetches";
}

[JsExport]
public record Point(double X, double Y);

[JsExport]
public struct Size
{
    public int W { get; set; }

    public int H { get; set; }
}

[JsExport]
public class Geometry
{
    public virtual Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    public virtual Size Grow(Size s, int by) => new() { W = s.W + by, H = s.H + by };

    public virtual int[] Squares(int[] xs) => xs.Select(x => x * x).ToArray();

    public virtual Dictionary<string, int> Count(string text) => text.Split(' ').GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());

    public virtual IShape Make(string kind) => kind == "circle" ? new Circle(2) : throw new ArgumentException("unknown shape '" + kind + "'", nameof(kind));

    public virtual int Apply(Func<int, int, int> f, int a, int b) => f(a, b);

    public virtual void Visit(Action<string> f)
    {
        f("a");
        f("b");
    }
}

[JsExport]
public class Streams
{
    public virtual IEnumerable<int> Range(int n)
    {
        for (int i = 0; i < n; i++)
        {
            yield return i;
        }
    }

    public virtual async IAsyncEnumerable<int> Ticks(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await Task.Delay(1);
            yield return i * 10;
        }
    }

    public virtual ValueTask<int> Quick(int x) => new(x + 1);

    public virtual async Task Fail()
    {
        await Task.Delay(1);
        throw new InvalidOperationException("async failure");
    }

    public virtual async Task<string> Later(int ms, string value)
    {
        await Task.Delay(ms);
        return value;
    }

    public virtual async Task<int> CallBackLater(Func<int, int> f)
    {
        await Task.Delay(1);
        return f(20);
    }
}
