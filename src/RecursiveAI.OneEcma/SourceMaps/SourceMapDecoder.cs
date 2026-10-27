using System.Globalization;
using System.Text.Json;

namespace RecursiveAI.OneEcma.SourceMaps;

/// <summary>Decoding of source map JSON, ECMA-426 sections 6, 9 and 10.</summary>
internal static class SourceMapDecoder
{
    private sealed class Context(SourceMapLimits limits)
    {
        public SourceMapLimits Limits { get; } = limits;

        public List<SourceMapProblem> Problems { get; } = [];

        public int MappingCount { get; set; }

        public void Add(string code, string message, string path, bool fatal = false)
        {
            if (Problems.Count < Limits.MaxProblems || fatal)
            {
                Problems.Add(new SourceMapProblem(code, message, path, fatal));
            }
        }
    }

    private sealed class Decoded
    {
        public string? File { get; set; }

        public List<SourceMapSource> Sources { get; } = [];

        public List<string> Names { get; } = [];

        public List<DecodedMapping> Mappings { get; } = [];
    }

    public static SourceMapParseResult Decode(string json, string? baseUrl, SourceMapLimits limits, string name)
    {
        var ctx = new Context(limits);
        SourceMap? map = null;
        if (json.Length > limits.MaxJsonLength)
        {
            ctx.Add("source-map-too-large", "the source map is " + json.Length.ToString(NumberFormatInfo.InvariantInfo) + " characters, over the limit of " + limits.MaxJsonLength.ToString(NumberFormatInfo.InvariantInfo), "", true);
            return new SourceMapParseResult(null, ctx.Problems, string.Empty, name);
        }

        string text = json.Length > 0 && json[0] == '\uFEFF' ? json[1..] : json;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = Math.Max(1, limits.MaxJsonDepth) });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                ctx.Add("source-map-not-object", "a source map must be a JSON object", "", true);
            }
            else
            {
                // ECMA-426 9.1.1 ParseSourceMap: "sections" present means an index map.
                Decoded? d = root.TryGetProperty("sections", out _)
                    ? DecodeIndex(root, baseUrl, ctx)
                    : DecodeMap(root, baseUrl, "", ctx, true);
                if (d is not null)
                {
                    map = Build(d);
                }
            }
        }
        catch (JsonException e)
        {
            ctx.Add("source-map-invalid-json", "the source map is not valid JSON: " + FirstLine(e.Message), "", true);
        }
        catch (InvalidOperationException)
        {
            // Looking a property up by name fails when some property name holds half a surrogate pair as an escape.
            ctx.Add("source-map-invalid-json", "the source map has a property name with an unpaired surrogate escape", "", true);
        }

        return new SourceMapParseResult(map, ctx.Problems, text, name);
    }

    /// <summary>
    /// The string value of a JSON string element. JSON allows an escape for half of a surrogate pair on its own (JavaScript strings
    /// can hold one), which <see cref="JsonElement.GetString"/> refuses, so that case is unescaped by hand.
    /// </summary>
    private static string Str(JsonElement e)
    {
        try
        {
            return e.GetString()!;
        }
        catch (InvalidOperationException)
        {
            string raw = e.GetRawText();
            var sb = new System.Text.StringBuilder(raw.Length);
            for (int i = 1; i < raw.Length - 1; i++)
            {
                char c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length - 1)
                {
                    sb.Append(c);
                    continue;
                }

                char n = raw[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u' when i + 4 < raw.Length - 1 && ushort.TryParse(raw.AsSpan(i + 1, 4), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.NumberFormatInfo.InvariantInfo, out ushort u):
                        sb.Append((char)u);
                        i += 4;
                        break;
                    default: sb.Append(n); break;
                }
            }

            return sb.ToString();
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n', StringComparison.Ordinal);
        return nl >= 0 ? s[..nl] : s;
    }

    private static SourceMap Build(Decoded d)
    {
        DecodedMapping[] all = [.. d.Mappings];
        // ECMA-426 9.1.2: mappings are sorted by generated position; equal positions keep map order.
        bool sorted = true;
        for (int i = 1; i < all.Length && sorted; i++)
        {
            sorted = SourceMap.Key(all[i - 1].GeneratedLine, all[i - 1].GeneratedColumn) <= SourceMap.Key(all[i].GeneratedLine, all[i].GeneratedColumn);
        }

        if (!sorted)
        {
            var keys = new (long Key, int Index)[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                keys[i] = (SourceMap.Key(all[i].GeneratedLine, all[i].GeneratedColumn), i);
            }

            Array.Sort(keys);
            var copy = new DecodedMapping[all.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                copy[i] = all[keys[i].Index];
            }

            all = copy;
        }

        return new SourceMap(d.File, [.. d.Sources], [.. d.Names], all);
    }

    // ECMA-426 10.1 DecodeIndexSourceMap
    private static Decoded? DecodeIndex(JsonElement json, string? baseUrl, Context ctx)
    {
        JsonElement sections = json.GetProperty("sections");
        if (sections.ValueKind != JsonValueKind.Array)
        {
            ctx.Add("source-map-sections-type", "\"sections\" must be an array", "sections", true);
            return null;
        }

        CheckVersion(json, "", ctx);
        foreach (string key in new[] { "mappings", "sources" })
        {
            if (json.TryGetProperty(key, out _))
            {
                ctx.Add("source-map-index-extra-field", "an index map has no top-level \"" + key + "\"; its sections carry the mappings", key);
            }
        }

        var result = new Decoded { File = OptionalString(json, "file", "", ctx) };
        var sourceIndex = new Dictionary<SourceMapSource, int>();
        long prevLine = -1;
        long prevColumn = 0;
        long lastLine = -1;
        long lastColumn = 0;
        int count = 0;
        foreach (JsonElement section in sections.EnumerateArray())
        {
            string path = "sections[" + count.ToString(NumberFormatInfo.InvariantInfo) + "]";
            if (++count > ctx.Limits.MaxSections)
            {
                ctx.Add("source-map-too-many-sections", "more than " + ctx.Limits.MaxSections.ToString(NumberFormatInfo.InvariantInfo) + " sections", "sections", true);
                return null;
            }

            if (section.ValueKind != JsonValueKind.Object)
            {
                ctx.Add("source-map-section-type", "a section must be an object", path);
                continue;
            }

            if (!section.TryGetProperty("offset", out JsonElement offset) || offset.ValueKind != JsonValueKind.Object)
            {
                ctx.Add("source-map-section-offset", "a section needs an \"offset\" object", path + ".offset", true);
                return null;
            }

            long offLine = OffsetNumber(offset, "line", path + ".offset", ctx);
            long offColumn = OffsetNumber(offset, "column", path + ".offset", ctx);
            if (prevLine >= 0 && (offLine < prevLine || (offLine == prevLine && offColumn < prevColumn)))
            {
                ctx.Add("source-map-sections-order", "section offsets are not in ascending order", path + ".offset");
            }

            if (lastLine >= 0 && (offLine < lastLine || (offLine == lastLine && offColumn <= lastColumn)))
            {
                ctx.Add("source-map-sections-overlap", "a section starts before the previous section's last mapping", path + ".offset");
            }

            if (!section.TryGetProperty("map", out JsonElement mapField) || mapField.ValueKind != JsonValueKind.Object)
            {
                ctx.Add("source-map-section-map", "a section needs a \"map\" object", path + ".map", true);
                return null;
            }

            Decoded? sub = DecodeMap(mapField, baseUrl, path + ".map", ctx, false);
            if (sub is null)
            {
                prevLine = offLine;
                prevColumn = offColumn;
                continue;
            }

            int nameShift = result.Names.Count;
            result.Names.AddRange(sub.Names);
            var remap = new int[sub.Sources.Count];
            for (int i = 0; i < remap.Length; i++)
            {
                if (!sourceIndex.TryGetValue(sub.Sources[i], out int idx))
                {
                    if (result.Sources.Count >= ctx.Limits.MaxSources)
                    {
                        ctx.Add("source-map-too-many-sources", "more than " + ctx.Limits.MaxSources.ToString(NumberFormatInfo.InvariantInfo) + " sources", path, true);
                        return null;
                    }

                    idx = result.Sources.Count;
                    result.Sources.Add(sub.Sources[i]);
                    sourceIndex[sub.Sources[i]] = idx;
                }

                remap[i] = idx;
            }

            bool any = false;
            foreach (DecodedMapping m in sub.Mappings)
            {
                // The section offset moves the first line's columns and every line.
                long line = m.GeneratedLine + offLine;
                long col = m.GeneratedLine == 0 ? m.GeneratedColumn + offColumn : m.GeneratedColumn;
                if (line > int.MaxValue || col > int.MaxValue)
                {
                    ctx.Add("source-map-position-range", "a section offset moves a mapping out of range", path + ".offset");
                    continue;
                }

                result.Mappings.Add(m with
                {
                    GeneratedLine = (int)line,
                    GeneratedColumn = (int)col,
                    SourceIndex = m.SourceIndex >= 0 ? remap[m.SourceIndex] : -1,
                    NameIndex = m.NameIndex >= 0 ? m.NameIndex + nameShift : -1,
                });
                any = true;
            }

            if (any)
            {
                DecodedMapping lastMapping = result.Mappings[^1];
                lastLine = lastMapping.GeneratedLine;
                lastColumn = lastMapping.GeneratedColumn;
            }

            prevLine = offLine;
            prevColumn = offColumn;
        }

        return result;
    }

    private static long OffsetNumber(JsonElement offset, string key, string path, Context ctx)
    {
        if (offset.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number && IsIntegral(v, out double d) && d >= 0 && d <= int.MaxValue)
        {
            return (long)d;
        }

        ctx.Add("source-map-section-offset", "\"" + key + "\" of an offset must be a non-negative integer", path + "." + key);
        return 0;
    }

    private static bool IsIntegral(JsonElement v, out double d)
    {
        d = 0;
        return v.TryGetDouble(out d) && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d;
    }

    private static void CheckVersion(JsonElement json, string path, Context ctx)
    {
        // ECMA-426 9.1.2 step 1: a version other than 3 is an optional error.
        if (!json.TryGetProperty("version", out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double n) || n != 3)
        {
            ctx.Add("source-map-version", "\"version\" must be the number 3", Join(path, "version"));
        }
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

    private static string? OptionalString(JsonElement json, string key, string path, Context ctx)
    {
        if (!json.TryGetProperty(key, out JsonElement v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            return Str(v);
        }

        ctx.Add("source-map-field-type", "\"" + key + "\" must be a string", Join(path, key));
        return null;
    }

    // ECMA-426 9.1.2 DecodeSourceMap
    private static Decoded? DecodeMap(JsonElement json, string? baseUrl, string path, Context ctx, bool top)
    {
        CheckVersion(json, path, ctx);
        if (!json.TryGetProperty("mappings", out JsonElement mappingsField) || mappingsField.ValueKind != JsonValueKind.String)
        {
            ctx.Add("source-map-mappings-type", "\"mappings\" must be a string", Join(path, "mappings"), top);
            return null;
        }

        if (!json.TryGetProperty("sources", out JsonElement sourcesField) || sourcesField.ValueKind != JsonValueKind.Array)
        {
            ctx.Add("source-map-sources-type", "\"sources\" must be an array", Join(path, "sources"), top);
            return null;
        }

        var result = new Decoded
        {
            File = OptionalString(json, "file", path, ctx),
        };
        string? sourceRoot = OptionalString(json, "sourceRoot", path, ctx);
        List<string?> sources = OptionalList(sourcesField, "sources", path, ctx, allowNull: true);
        List<string?> contents = json.TryGetProperty("sourcesContent", out JsonElement sc) ? OptionalList(sc, "sourcesContent", path, ctx, allowNull: true) : [];
        if (sources.Count > ctx.Limits.MaxSources)
        {
            ctx.Add("source-map-too-many-sources", "more than " + ctx.Limits.MaxSources.ToString(NumberFormatInfo.InvariantInfo) + " sources", Join(path, "sources"), top);
            return null;
        }

        var ignored = new HashSet<int>();
        if (json.TryGetProperty("ignoreList", out JsonElement il))
        {
            if (il.ValueKind != JsonValueKind.Array)
            {
                ctx.Add("source-map-field-type", "\"ignoreList\" must be an array", Join(path, "ignoreList"));
            }
            else
            {
                foreach (JsonElement item in il.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && IsIntegral(item, out double d) && d >= 0 && d <= int.MaxValue)
                    {
                        if (d >= sources.Count)
                        {
                            ctx.Add("source-map-ignore-list-range", "\"ignoreList\" has an index with no matching source", Join(path, "ignoreList"));
                        }
                        else
                        {
                            ignored.Add((int)d);
                        }
                    }
                    else
                    {
                        ctx.Add("source-map-field-type", "\"ignoreList\" entries must be non-negative integers", Join(path, "ignoreList"));
                    }
                }
            }
        }

        // ECMA-426 9.3.1 DecodeSourceMapSources. An empty sourceRoot counts as absent: the literal rule would turn "a.js" into "/a.js".
        string prefix = string.IsNullOrEmpty(sourceRoot) ? string.Empty : sourceRoot.EndsWith('/') ? sourceRoot : sourceRoot + "/";
        for (int i = 0; i < sources.Count; i++)
        {
            string? url = sources[i] is { } s ? SourceMapUrl.Resolve(baseUrl, prefix + s) : null;
            result.Sources.Add(new SourceMapSource(url, i < contents.Count ? contents[i] : null, ignored.Contains(i)));
        }

        if (json.TryGetProperty("names", out JsonElement namesField))
        {
            foreach (string? n in OptionalList(namesField, "names", path, ctx, allowNull: false))
            {
                result.Names.Add(n ?? string.Empty);
            }
        }

        DecodeMappings(Str(mappingsField), result, Join(path, "mappings"), ctx);
        return result;
    }

    /// <summary>GetOptionalListOfStrings / GetOptionalListOfOptionalStrings (9.1.2.2 and 9.1.2.3).</summary>
    private static List<string?> OptionalList(JsonElement values, string key, string path, Context ctx, bool allowNull)
    {
        var list = new List<string?>();
        if (values.ValueKind != JsonValueKind.Array)
        {
            ctx.Add("source-map-field-type", "\"" + key + "\" must be an array", Join(path, key));
            return list;
        }

        foreach (JsonElement item in values.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(Str(item));
            }
            else
            {
                if (!(allowNull && item.ValueKind == JsonValueKind.Null))
                {
                    ctx.Add("source-map-field-type", "\"" + key + "\" entries must be " + (allowNull ? "strings or null" : "strings"), Join(path, key));
                }

                list.Add(allowNull ? null : string.Empty);
            }
        }

        return list;
    }

    private static int Digit(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };

    private enum VlqStatus
    {
        Ok,
        BadCharacter,
        Unterminated,
        TooLarge,
    }

    /// <summary>ECMA-426 section 6: reads one base64 VLQ at <paramref name="pos"/> and returns its signed value.</summary>
    private static VlqStatus ReadVlq(string s, ref int pos, out long value)
    {
        value = 0;
        ulong unsigned = 0;
        int shift = 0;
        while (true)
        {
            if (pos >= s.Length)
            {
                return VlqStatus.Unterminated;
            }

            if (s[pos] is ',' or ';')
            {
                return VlqStatus.Unterminated;
            }

            int d = Digit(s[pos]);
            if (d < 0)
            {
                return VlqStatus.BadCharacter;
            }

            pos++;
            // Every digit carries five value bits (VLQUnsignedValue); the value must stay below 2^32. Continuation
            // digits that add nothing are allowed however many there are.
            int bits = d & 31;
            if (bits != 0)
            {
                if (shift >= 32)
                {
                    return VlqStatus.TooLarge;
                }

                unsigned += (ulong)bits << shift;
                if (unsigned >= 1UL << 32)
                {
                    return VlqStatus.TooLarge;
                }
            }

            if (d < 32)
            {
                break;
            }

            shift = Math.Min(shift + 5, 64);
        }

        // VLQSignedValue: the low bit is the sign; "negative zero" is -2^31.
        long v = (long)(unsigned >> 1);
        if ((unsigned & 1) == 1)
        {
            value = v == 0 ? int.MinValue : -v;
        }
        else
        {
            value = v;
        }

        return VlqStatus.Ok;
    }

    // ECMA-426 9.2.1.1 DecodeMappingsField, applied while reading the grammar of 9.2.1.
    private static void DecodeMappings(string raw, Decoded into, string path, Context ctx)
    {
        long line = 0;
        long genCol = 0;
        long src = 0;
        long origLine = 0;
        long origCol = 0;
        long name = 0;
        int pos = 0;
        Span<long> f = stackalloc long[5];
        while (true)
        {
            // One generated line: a possibly empty list of comma-separated segments.
            bool first = true;
            while (pos < raw.Length && raw[pos] != ';')
            {
                if (!first)
                {
                    if (raw[pos] != ',')
                    {
                        Fail(ctx, path, "unexpected character '" + raw[pos] + "' between segments", pos);
                        return;
                    }

                    pos++;
                    if (pos >= raw.Length || raw[pos] is ',' or ';')
                    {
                        Fail(ctx, path, "empty segment", pos);
                        return;
                    }
                }

                first = false;
                int fields = 0;
                while (pos < raw.Length && raw[pos] is not (',' or ';'))
                {
                    if (fields == 5)
                    {
                        Fail(ctx, path, "a segment has more than 5 fields", pos);
                        return;
                    }

                    int at = pos;
                    VlqStatus st = ReadVlq(raw, ref pos, out f[fields]);
                    if (st != VlqStatus.Ok)
                    {
                        Fail(ctx, path, st switch
                        {
                            VlqStatus.BadCharacter => "'" + raw[Math.Min(pos, raw.Length - 1)] + "' is not a base64 digit",
                            VlqStatus.Unterminated => "a VLQ ends with a continuation digit",
                            _ => "a VLQ value does not fit in 32 bits",
                        }, st == VlqStatus.Unterminated ? raw.Length : at);
                        return;
                    }

                    fields++;
                }

                if (fields is not (1 or 4 or 5))
                {
                    Fail(ctx, path, "a segment has " + fields.ToString(NumberFormatInfo.InvariantInfo) + " fields, expected 1, 4 or 5", pos);
                    return;
                }

                genCol += f[0];
                if (fields >= 4)
                {
                    src += f[1];
                    origLine += f[2];
                    origCol += f[3];
                }

                if (fields == 5)
                {
                    name += f[4];
                }

                if (genCol < 0 || genCol > int.MaxValue)
                {
                    ctx.Add("source-map-position-range", "a generated column is out of range", path);
                    continue;
                }

                if (ctx.MappingCount >= ctx.Limits.MaxMappings)
                {
                    ctx.Add("source-map-too-many-mappings", "more than " + ctx.Limits.MaxMappings.ToString(NumberFormatInfo.InvariantInfo) + " mappings; the rest are ignored", path);
                    return;
                }

                int sourceIndex = -1;
                int oLine = 0;
                int oCol = 0;
                int nameIndex = -1;
                if (fields >= 4)
                {
                    if (src < 0 || src >= into.Sources.Count || origLine < 0 || origLine > int.MaxValue || origCol < 0 || origCol > int.MaxValue)
                    {
                        ctx.Add("source-map-position-range", "a mapping points outside the sources or at a negative position", path);
                    }
                    else
                    {
                        sourceIndex = (int)src;
                        oLine = (int)origLine;
                        oCol = (int)origCol;
                    }
                }

                if (fields == 5)
                {
                    if (name < 0 || name >= into.Names.Count)
                    {
                        ctx.Add("source-map-name-range", "a mapping's name index is outside \"names\"", path);
                    }
                    else
                    {
                        nameIndex = (int)name;
                    }
                }

                into.Mappings.Add(new DecodedMapping((int)Math.Min(line, int.MaxValue), (int)genCol, sourceIndex, oLine, oCol, nameIndex));
                ctx.MappingCount++;
            }

            if (pos >= raw.Length)
            {
                return;
            }

            // raw[pos] == ';': next generated line; the column delta restarts at zero.
            pos++;
            line++;
            genCol = 0;
        }
    }

    private static void Fail(Context ctx, string path, string what, int pos)
    {
        ctx.Add("source-map-invalid-mappings", "malformed \"mappings\" at offset " + pos.ToString(NumberFormatInfo.InvariantInfo) + ": " + what + "; later mappings are ignored", path);
    }
}
