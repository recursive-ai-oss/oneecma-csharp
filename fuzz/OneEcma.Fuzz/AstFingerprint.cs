using System.Collections;
using System.Reflection;
using System.Text;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// A text form of a syntax tree that ignores positions and empty statements, built by reflection (this project is not shipped). Two programs
/// with the same fingerprint parse to the same tree up to where their tokens sit.
/// </summary>
internal static class AstFingerprint
{
    private static readonly Dictionary<Type, PropertyInfo[]> Cache = [];

    internal static string Of(Node root)
    {
        var sb = new StringBuilder();
        Walk(root, sb, 0);
        return sb.ToString();
    }

    private static void Walk(object? o, StringBuilder sb, int depth)
    {
        if (depth > 150 || sb.Length > 2_000_000)
        {
            return;
        }

        switch (o)
        {
            case null:
                sb.Append('~');
                return;
            case Node n:
                sb.Append('(').Append(n.GetType().Name);
                foreach (PropertyInfo p in PropsOf(n.GetType()))
                {
                    sb.Append(' ').Append(p.Name).Append('=');
                    Walk(p.GetValue(n), sb, depth + 1);
                }

                sb.Append(')');
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case IEnumerable e:
                sb.Append('[');
                foreach (object? item in e)
                {
                    if (item is EmptyStatement)
                    {
                        continue;
                    }

                    Walk(item, sb, depth + 1);
                    sb.Append(',');
                }

                sb.Append(']');
                return;
            default:
                sb.Append(o);
                return;
        }
    }

    private static PropertyInfo[] PropsOf(Type t)
    {
        if (Cache.TryGetValue(t, out PropertyInfo[]? props))
        {
            return props;
        }

        var list = new List<PropertyInfo>();
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name is "Start" or "End" or "Span" or "Kind")
            {
                continue;
            }

            Type pt = p.PropertyType;
            bool simple = pt.IsPrimitive || pt.IsEnum || pt == typeof(string) || pt == typeof(decimal);
            bool node = typeof(Node).IsAssignableFrom(pt);
            bool nodes = pt != typeof(string) && typeof(IEnumerable).IsAssignableFrom(pt) && pt.IsGenericType && typeof(Node).IsAssignableFrom(pt.GetGenericArguments()[0]);
            if (simple || node || nodes)
            {
                list.Add(p);
            }
        }

        props = [.. list];
        Cache[t] = props;
        return props;
    }
}
