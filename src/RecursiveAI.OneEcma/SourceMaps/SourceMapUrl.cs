using System.Text;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.SourceMaps;

/// <summary>Reads text the engine itself may not fetch: a source map or an original source, by URL. Returns null when the host has nothing for the URL.</summary>
/// <param name="url">The URL as written in the generated code or the source map, resolved against the generated file's name.</param>
/// <returns>The text, or null.</returns>
public delegate string? SourceMapFetcher(string url);

/// <summary>URL handling for source maps: finding the <c>sourceMappingURL</c> comment, <c>data:</c> URLs and resolving relative references.</summary>
public static class SourceMapUrl
{
    /// <summary>
    /// Finds the <c>//# sourceMappingURL=</c> comment of a JavaScript source (ECMA-426 section 11.1.2.1, the extraction
    /// that does not parse the source). Both <c>//#</c> and <c>//@</c> are accepted.
    /// </summary>
    /// <param name="source">The generated source text.</param>
    /// <returns>The URL, or null when the last non-blank, non-comment-free line check finds none, or the comment is ambiguous (it contains a quote, backtick or <c>*/</c>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static string? Extract(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Walk the lines from the end without splitting the whole text: minified bundles can be megabytes on one line.
        int end = source.Length;
        while (true)
        {
            int start = LineStart(source, end);
            string? verdict = null;
            bool decided = ScanLine(source, start, end, out verdict);
            if (decided)
            {
                return verdict;
            }

            if (start == 0)
            {
                return null;
            }

            // Step over the terminator that precedes this line.
            end = start - 1;
            if (source[end] == '\n' && end > 0 && source[end - 1] == '\r')
            {
                end--;
            }
        }
    }

    private static int LineStart(string s, int end)
    {
        for (int i = end - 1; i >= 0; i--)
        {
            if (SourceText.IsLineTerminator(s[i]))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>One iteration of the spec's loop. Returns true when the answer is final (a URL or null); false to look at the line above.</summary>
    private static bool ScanLine(string s, int start, int end, out string? result)
    {
        result = null;
        int pos = start;
        while (pos < end)
        {
            char first = s[pos];
            if (first == '/' && pos + 1 < end)
            {
                pos++;
                if (s[pos] != '/')
                {
                    return true;
                }

                pos++;
                ReadOnlySpan<char> comment = s.AsSpan(pos, end - pos);
                if (comment.IndexOfAny('"', '\'', '`') >= 0 || comment.IndexOf("*/", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }

                string? url = MatchComment(comment);
                if (url is not null)
                {
                    result = url;
                    return true;
                }

                return false;
            }

            if (IsWhiteSpace(first))
            {
                pos++;
                continue;
            }

            return true;
        }

        return false;
    }

    // ECMA-426 11.1.2.1.1 MatchSourceMapURL: ^[@#]\s*sourceMappingURL=(\S*?)\s*$
    private static string? MatchComment(ReadOnlySpan<char> comment)
    {
        if (comment.Length == 0 || comment[0] is not ('@' or '#'))
        {
            return null;
        }

        int i = 1;
        while (i < comment.Length && IsWhiteSpace(comment[i]))
        {
            i++;
        }

        const string Key = "sourceMappingURL=";
        if (!comment[i..].StartsWith(Key, StringComparison.Ordinal))
        {
            return null;
        }

        i += Key.Length;
        int j = comment.Length;
        while (j > i && IsWhiteSpace(comment[j - 1]))
        {
            j--;
        }

        ReadOnlySpan<char> url = comment[i..j];
        foreach (char c in url)
        {
            if (IsWhiteSpace(c))
            {
                return null;
            }
        }

        return url.ToString();
    }

    private static bool IsWhiteSpace(char c) => c is ' ' or '\t' or '\v' or '\f' or '\u00a0' or '\ufeff' or '\u1680' or (>= '\u2000' and <= '\u200a') or '\u202f' or '\u205f' or '\u3000';

    /// <summary>
    /// Decodes a <c>data:</c> URL (<c>data:[type][;base64],data</c>), the self-contained way to embed a source map.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="text">The decoded text (UTF-8, a leading byte order mark removed) when the URL is a well-formed data URL.</param>
    /// <param name="maxLength">Longest accepted result in code units.</param>
    /// <returns>True when decoded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is null.</exception>
    public static bool TryDecodeDataUrl(string url, out string text, int maxLength = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(url);
        text = string.Empty;
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || url.Length - comma > maxLength + (maxLength / 2) + 16)
        {
            return false;
        }

        string meta = url[5..comma];
        bool base64 = meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        byte[] bytes;
        try
        {
            string payload = url[(comma + 1)..];
            if (base64)
            {
                bytes = Convert.FromBase64String(PercentDecodeToString(payload).Trim());
            }
            else
            {
                bytes = PercentDecodeBytes(payload);
            }
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length > maxLength)
        {
            return false;
        }

        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        text = new UTF8Encoding(false, false).GetString(bytes, skip, bytes.Length - skip);
        return true;
    }

    private static string PercentDecodeToString(string s) => s.Contains('%', StringComparison.Ordinal) ? Encoding.Latin1.GetString(PercentDecodeBytes(s)) : s;

    private static byte[] PercentDecodeBytes(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '%' && i + 2 < s.Length && Hex(s[i + 1]) >= 0 && Hex(s[i + 2]) >= 0)
            {
                bytes.Add((byte)((Hex(s[i + 1]) << 4) | Hex(s[i + 2])));
                i += 2;
            }
            else if (c < 0x80)
            {
                bytes.Add((byte)c);
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(char.IsHighSurrogate(c) && i + 1 < s.Length ? s.Substring(i++, 2) : c.ToString()));
            }
        }

        return [.. bytes];
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// Resolves <paramref name="reference"/> against <paramref name="baseUrl"/> the way a browser resolves a relative URL, for
    /// URLs and plain file paths alike: an absolute reference (it has a scheme, or starts with <c>/</c> and there is no scheme
    /// in the base) is kept; otherwise the reference replaces the last segment of the base, and <c>.</c> and <c>..</c> segments are folded.
    /// </summary>
    /// <param name="baseUrl">The base, or null to keep the reference as written.</param>
    /// <param name="reference">The reference.</param>
    /// <returns>The resolved URL or path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
    public static string Resolve(string? baseUrl, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (baseUrl is null || baseUrl.Length == 0 || HasScheme(reference) || baseUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        // Split the base into a prefix that is never touched (scheme and authority) and a path.
        string prefix = string.Empty;
        string basePath = baseUrl;
        int q = basePath.IndexOfAny(['?', '#']);
        if (q >= 0)
        {
            basePath = basePath[..q];
        }

        if (HasScheme(baseUrl))
        {
            int schemeEnd = baseUrl.IndexOf(':', StringComparison.Ordinal) + 1;
            int pathStart = schemeEnd;
            if (string.CompareOrdinal(baseUrl, schemeEnd, "//", 0, 2) == 0)
            {
                int slash = baseUrl.IndexOf('/', schemeEnd + 2);
                pathStart = slash < 0 ? baseUrl.Length : slash;
            }

            prefix = baseUrl[..pathStart];
            basePath = q >= 0 && q < pathStart ? string.Empty : basePath[pathStart..];
        }

        if (reference.StartsWith("//", StringComparison.Ordinal) && prefix.Length > 0)
        {
            return prefix[..(prefix.IndexOf(':', StringComparison.Ordinal) + 1)] + reference;
        }

        string path;
        if (reference.StartsWith('/'))
        {
            path = reference;
        }
        else
        {
            int lastSlash = basePath.LastIndexOf('/');
            path = (lastSlash >= 0 ? basePath[..(lastSlash + 1)] : prefix.Length > 0 ? "/" : string.Empty) + reference;
        }

        return prefix + FoldDots(path);
    }

    private static bool HasScheme(string s)
    {
        int colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon < 2)
        {
            // One letter before the colon is a Windows drive, not a scheme.
            return false;
        }

        if (!char.IsAsciiLetter(s[0]))
        {
            return false;
        }

        for (int i = 1; i < colon; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(s[i]) || s[i] is '+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static string FoldDots(string path)
    {
        bool absolute = path.StartsWith('/');
        var stack = new List<string>();
        string[] parts = path.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            bool last = i == parts.Length - 1;
            if (p == ".")
            {
                if (last)
                {
                    stack.Add(string.Empty);
                }
            }
            else if (p == "..")
            {
                if (stack.Count > 0 && stack[^1] != ".." && !(absolute && stack.Count == 1 && stack[0].Length == 0))
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (!absolute)
                {
                    stack.Add("..");
                }

                if (last)
                {
                    stack.Add(string.Empty);
                }
            }
            else
            {
                stack.Add(p);
            }
        }

        string joined = string.Join('/', stack);
        return absolute && !joined.StartsWith('/') ? "/" + joined : joined;
    }
}

/// <summary>Finds and loads the source map a generated file points to.</summary>
public static class SourceMapLoader
{
    /// <summary>
    /// Follows the <c>sourceMappingURL</c> comment of <paramref name="generated"/>. A <c>data:</c> URL is decoded here; any
    /// other URL is resolved against the generated file's name and handed to <paramref name="fetch"/>, because the engine reads no files
    /// and opens no connections itself. A fetched text that starts with <c>)]}'</c> has that first line dropped (ECMA-426 section 11.2.1).
    /// </summary>
    /// <param name="generated">The generated source; its <see cref="SourceText.Name"/> is the base for relative URLs.</param>
    /// <param name="fetch">How to read a URL; null means only <c>data:</c> URLs are followed.</param>
    /// <param name="limits">Decoding limits; null means the defaults.</param>
    /// <returns>
    /// Null when the source has no <c>sourceMappingURL</c> comment, or its map could not be obtained (not a data URL and
    /// <paramref name="fetch"/> is null or returned null); otherwise the decoding result, whose <see cref="SourceMapParseResult.Map"/> is null when the map is
    /// malformed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="generated"/> is null.</exception>
    public static SourceMapParseResult? Load(SourceText generated, SourceMapFetcher? fetch = null, SourceMapLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(generated);
        limits ??= new SourceMapLimits();
        string? url = SourceMapUrl.Extract(generated.Text);
        if (url is null)
        {
            return null;
        }

        string text;
        string baseUrl;
        string name;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!SourceMapUrl.TryDecodeDataUrl(url, out text, limits.MaxJsonLength))
            {
                return new SourceMapParseResult(null, [new SourceMapProblem("source-map-data-url", "the sourceMappingURL data: URL cannot be decoded (expected base64 or percent-encoded UTF-8 JSON, within the size limit)", "", true)], string.Empty, generated.Name);
            }

            baseUrl = generated.Name;
            name = generated.Name + " (inline source map)";
        }
        else
        {
            if (fetch is null)
            {
                return null;
            }

            baseUrl = SourceMapUrl.Resolve(generated.Name, url);
            string? fetched = fetch(baseUrl);
            if (fetched is null)
            {
                return null;
            }

            text = StripXssiPrefix(fetched);
            name = baseUrl;
        }

        return SourceMap.Parse(text, baseUrl, limits, name);
    }

    /// <summary>Drops a leading <c>)]}'</c> line, which servers may put before a source map (ECMA-426 section 11.2.1).</summary>
    /// <param name="text">The fetched map text.</param>
    /// <returns>The text without that line.</returns>
    public static string StripXssiPrefix(string text)
    {
        if (!text.StartsWith(")]}'", StringComparison.Ordinal))
        {
            return text;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '\n' or '\r')
            {
                return text[i..];
            }
        }

        return string.Empty;
    }
}
