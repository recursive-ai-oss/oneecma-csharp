using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.SourceMaps;

/// <summary>Size limits for decoding a source map from untrusted text.</summary>
public sealed class SourceMapLimits
{
    /// <summary>Longest accepted source map text, in UTF-16 code units. Default 64 million.</summary>
    public int MaxJsonLength { get; set; } = 64 * 1024 * 1024;

    /// <summary>Deepest accepted JSON nesting. An index map nests one source map per section, so the useful depth is small. Default 32.</summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>Most decoded mappings. Default 4 million.</summary>
    public int MaxMappings { get; set; } = 4_000_000;

    /// <summary>Most entries in <c>sources</c> (after merging the sections of an index map). Default 100 000.</summary>
    public int MaxSources { get; set; } = 100_000;

    /// <summary>Most sections in an index map. Default 10 000.</summary>
    public int MaxSections { get; set; } = 10_000;

    /// <summary>Most problems recorded before further ones are dropped. Default 100.</summary>
    public int MaxProblems { get; set; } = 100;
}

/// <summary>One original source file of a source map (a Decoded Source Record, ECMA-426 section 9.3).</summary>
/// <param name="Url">The source URL after <c>sourceRoot</c> and the map's own URL were applied; null when the entry was null or unusable.</param>
/// <param name="Content">The text from <c>sourcesContent</c>, or null.</param>
/// <param name="Ignored">True when the index is in <c>ignoreList</c> (library or generated code a debugger would skip).</param>
public sealed record SourceMapSource(string? Url, string? Content, bool Ignored);

/// <summary>A problem found while decoding a source map. Optional errors in ECMA-426 leave a usable map; fatal ones leave none.</summary>
/// <param name="Code">Stable id such as <c>source-map-invalid-vlq</c>.</param>
/// <param name="Message">What is wrong, in plain words.</param>
/// <param name="Path">Where in the map JSON, such as <c>sections[1].map.mappings</c>.</param>
/// <param name="Fatal">True when no map could be produced.</param>
public sealed record SourceMapProblem(string Code, string Message, string Path, bool Fatal);

/// <summary>A position in an original source found by <see cref="SourceMap.GetOriginalPositions"/>.</summary>
/// <param name="SourceIndex">Index into <see cref="SourceMap.Sources"/>.</param>
/// <param name="Source">The source record.</param>
/// <param name="Line">Zero-based line.</param>
/// <param name="Column">Zero-based column in UTF-16 code units.</param>
/// <param name="Name">The mapping's name, or null.</param>
/// <param name="GeneratedLine">Zero-based generated line of the mapping that matched (at or before the query).</param>
/// <param name="GeneratedColumn">Zero-based generated column of the mapping that matched.</param>
public sealed record OriginalPosition(int SourceIndex, SourceMapSource Source, int Line, int Column, string? Name, int GeneratedLine, int GeneratedColumn);

/// <summary>A Decoded Mapping Record in compact form.</summary>
/// <param name="GeneratedLine">Zero-based generated line.</param>
/// <param name="GeneratedColumn">Zero-based generated column in UTF-16 code units.</param>
/// <param name="SourceIndex">Index into <see cref="SourceMap.Sources"/>, or -1 when the generated code here has no original.</param>
/// <param name="OriginalLine">Zero-based original line; meaningful when <paramref name="SourceIndex"/> is not -1.</param>
/// <param name="OriginalColumn">Zero-based original column; meaningful when <paramref name="SourceIndex"/> is not -1.</param>
/// <param name="NameIndex">Index into <see cref="SourceMap.Names"/>, or -1.</param>
public readonly record struct DecodedMapping(int GeneratedLine, int GeneratedColumn, int SourceIndex, int OriginalLine, int OriginalColumn, int NameIndex);

/// <summary>The outcome of decoding a source map.</summary>
public sealed class SourceMapParseResult
{
    internal SourceMapParseResult(SourceMap? map, IReadOnlyList<SourceMapProblem> problems, string text, string name)
    {
        Map = map;
        Problems = problems;
        Text = text;
        Name = name;
    }

    /// <summary>The decoded map; null when a fatal problem prevented one.</summary>
    public SourceMap? Map { get; }

    /// <summary>Everything wrong with the input, fatal or not; empty when the map is valid.</summary>
    public IReadOnlyList<SourceMapProblem> Problems { get; }

    /// <summary>True when there are no problems at all.</summary>
    public bool IsValid => Problems.Count == 0;

    /// <summary>The map text that was decoded (the problems' paths refer into it).</summary>
    public string Text { get; }

    /// <summary>The URL or file name the map came from, used as the name of the <see cref="Text"/> in diagnostics.</summary>
    public string Name { get; }

    /// <summary>
    /// The problems as warning diagnostics (kind <c>SourceMap</c>) over the map text, so a report about a broken map
    /// looks like every other report. The span is the key of the offending field when it can be found, else the start of the text.
    /// </summary>
    public IReadOnlyList<Diagnostic> ToDiagnostics()
    {
        var source = new SourceText(Text, Name);
        var list = new List<Diagnostic>(Problems.Count);
        foreach (SourceMapProblem p in Problems)
        {
            var d = new Diagnostic("SourceMap", p.Code, p.Message, source, LocatePath(p.Path), Severity.Warning);
            d.Notes.Add(p.Fatal ? "the source map was not used" : "the source map is used as far as it decoded");
            if (p.Path.Length > 0)
            {
                d.Notes.Add("at " + p.Path);
            }

            list.Add(d);
        }

        return list;
    }

    private Span LocatePath(string path)
    {
        int dot = path.LastIndexOf('.');
        string key = dot >= 0 ? path[(dot + 1)..] : path;
        int br = key.IndexOf('[', StringComparison.Ordinal);
        if (br >= 0)
        {
            key = key[..br];
        }

        if (key.Length > 0)
        {
            int at = Text.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at >= 0)
            {
                return new Span(at, at + key.Length + 2);
            }
        }

        return new Span(0, Math.Min(1, Text.Length));
    }
}

/// <summary>
/// A decoded source map (ECMA-426): the original sources and the mappings from generated positions to original ones,
/// sorted by generated position. Immutable once built, so a map can be shared across reports and threads.
/// </summary>
public sealed class SourceMap
{
    private readonly long[] _keys;
    private readonly SourceMapSource[] _sources;
    private readonly DecodedMapping[] _mappings;

    internal SourceMap(string? file, SourceMapSource[] sources, string[] names, DecodedMapping[] sortedMappings)
    {
        File = file;
        _sources = sources;
        Sources = sources;
        Names = names;
        _mappings = sortedMappings;
        _keys = new long[sortedMappings.Length];
        for (int i = 0; i < sortedMappings.Length; i++)
        {
            _keys[i] = Key(sortedMappings[i].GeneratedLine, sortedMappings[i].GeneratedColumn);
        }
    }

    /// <summary>The <c>file</c> field: the name of the generated file, or null.</summary>
    public string? File { get; }

    /// <summary>The original sources. Entries of an index map's sections are merged, equal records once.</summary>
    public IReadOnlyList<SourceMapSource> Sources { get; }

    /// <summary>The <c>names</c> mappings refer to by <see cref="DecodedMapping.NameIndex"/>. For an index map, the names of its sections in section order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The number of decoded mappings.</summary>
    public int MappingCount => _mappings.Length;

    /// <summary>Returns the mapping at <paramref name="index"/> in generated-position order.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>0..MappingCount-1</c>.</exception>
    public DecodedMapping GetMapping(int index) => _mappings[index];

    internal static long Key(int line, int column) => ((long)line << 32) | (uint)column;

    /// <summary>
    /// Looks up a generated position (ECMA-426 section 12.1 GetOriginalPositions). The mapping that applies is the last one at or
    /// before the position in line-then-column order, even when it is on an earlier line; every mapping at exactly that generated
    /// position contributes, in map order. Entries without an original source are left out, so an unmapped position yields an empty list.
    /// </summary>
    /// <param name="line">Zero-based generated line.</param>
    /// <param name="column">Zero-based generated column in UTF-16 code units.</param>
    /// <returns>The original positions, possibly empty.</returns>
    public IReadOnlyList<OriginalPosition> GetOriginalPositions(int line, int column)
    {
        var result = new List<OriginalPosition>(1);
        int last = LastAtOrBefore(Key(Math.Max(line, 0), Math.Max(column, 0)));
        if (last < 0 || line < 0)
        {
            return result;
        }

        long key = _keys[last];
        int first = last;
        while (first > 0 && _keys[first - 1] == key)
        {
            first--;
        }

        for (int i = first; i < _mappings.Length && _keys[i] == key; i++)
        {
            DecodedMapping m = _mappings[i];
            if (m.SourceIndex >= 0)
            {
                result.Add(ToPosition(m));
            }
        }

        return result;
    }

    /// <summary>The first result of <see cref="GetOriginalPositions"/>.</summary>
    /// <param name="line">Zero-based generated line.</param>
    /// <param name="column">Zero-based generated column in UTF-16 code units.</param>
    /// <param name="position">The original position when found.</param>
    /// <returns>True when the generated position maps to an original one.</returns>
    public bool TryGetOriginalPosition(int line, int column, out OriginalPosition position)
    {
        IReadOnlyList<OriginalPosition> all = GetOriginalPositions(line, column);
        if (all.Count > 0)
        {
            position = all[0];
            return true;
        }

        position = null!;
        return false;
    }

    private OriginalPosition ToPosition(DecodedMapping m) =>
        new(m.SourceIndex, _sources[m.SourceIndex], m.OriginalLine, m.OriginalColumn,
            m.NameIndex >= 0 ? Names[m.NameIndex] : null, m.GeneratedLine, m.GeneratedColumn);

    /// <summary>Index of the last mapping whose key is at or before <paramref name="key"/>, or -1.</summary>
    private int LastAtOrBefore(long key)
    {
        int lo = 0;
        int hi = _keys.Length - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_keys[mid] <= key)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// Decodes a source map (ECMA-426 section 9): a standard map or an index map with <c>sections</c>. Never throws for bad
    /// input; problems are returned. A grammar error in <c>mappings</c> keeps the mappings decoded before it, which the
    /// specification leaves optional (it would return none).
    /// </summary>
    /// <param name="json">The map text.</param>
    /// <param name="baseUrl">The URL the map was loaded from, used to resolve relative source URLs; null keeps them as written (after <c>sourceRoot</c>).</param>
    /// <param name="limits">Size limits; null means the defaults.</param>
    /// <param name="name">The name to give the map text in diagnostics; defaults to <paramref name="baseUrl"/> or <c>source map</c>.</param>
    /// <returns>The map (null if fatally malformed) and every problem found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static SourceMapParseResult Parse(string json, string? baseUrl = null, SourceMapLimits? limits = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return SourceMapDecoder.Decode(json, baseUrl, limits ?? new SourceMapLimits(), name ?? baseUrl ?? "source map");
    }
}
