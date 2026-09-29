using System.Text;
using System.Text.Json;

namespace JsoncPreserve;

/// <summary>Edits UTF-8 JSONC while retaining source bytes outside the affected spans.</summary>
/// <remarks>Each successful edit is validated and indexed again before it replaces the current text.</remarks>
public sealed class JsoncDocument
{
    private byte[] _source;
    private JsoncNode _root;
    private readonly JsonSerializerOptions _writeOptions;

    /// <summary>Creates a validated document from an owned UTF-8 buffer.</summary>
    /// <param name="source">Source bytes owned by this instance.</param>
    /// <param name="options">Optional serializer options for newly written values.</param>
    private JsoncDocument(byte[] source, JsonSerializerOptions? options)
    {
        _source = source;
        _root = JsoncIndex.Parse(source);
        _writeOptions = options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
    }

    /// <summary>Parses JSONC text without changing its comments or formatting.</summary>
    /// <param name="text">JSONC source text.</param>
    /// <param name="options">Optional options used when values are later written.</param>
    /// <returns>A source-preserving document.</returns>
    /// <exception cref="JsonException">The source is invalid JSONC or has duplicate object keys.</exception>
    public static JsoncDocument Parse(string text, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new JsoncDocument(new UTF8Encoding(false, true).GetBytes(text), options);
    }

    /// <summary>Parses UTF-8 JSONC bytes, retaining an optional UTF-8 BOM.</summary>
    /// <param name="utf8">Complete UTF-8 source bytes.</param>
    /// <param name="options">Optional options used when values are later written.</param>
    /// <returns>A source-preserving document.</returns>
    /// <exception cref="DecoderFallbackException">The input contains invalid UTF-8.</exception>
    /// <exception cref="JsonException">The input is invalid JSONC or has duplicate object keys.</exception>
    public static JsoncDocument Parse(ReadOnlySpan<byte> utf8, JsonSerializerOptions? options = null) =>
        new(utf8.ToArray(), options);

    /// <summary>Reads and parses a UTF-8 JSONC file.</summary>
    /// <param name="path">Path to the source file.</param>
    /// <param name="options">Optional options used when values are later written.</param>
    /// <returns>A source-preserving document.</returns>
    public static JsoncDocument Load(string path, JsonSerializerOptions? options = null) =>
        Parse(File.ReadAllBytes(path), options);

    /// <summary>Returns a defensive copy of the current UTF-8 source bytes.</summary>
    /// <returns>Source bytes, including an original UTF-8 BOM if present.</returns>
    public byte[] ToUtf8Bytes() => (byte[])_source.Clone();

    /// <summary>Returns the current JSONC text with original comments and formatting.</summary>
    /// <returns>The UTF-8 source decoded as a string.</returns>
    public override string ToString() => new UTF8Encoding(false, true).GetString(_source);

    /// <summary>Deserializes the current source through <see cref="JsonSerializer"/>.</summary>
    /// <typeparam name="T">Requested POCO or value type.</typeparam>
    /// <param name="options">Optional serializer options; comment skipping and trailing commas are enabled on a copy.</param>
    /// <returns>The deserialized value, or null for a JSON null root.</returns>
    public T? Deserialize<T>(JsonSerializerOptions? options = null)
    {
        // Copy options because configuring JSONC reading must not mutate the caller's instance.
        var read = options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        read.ReadCommentHandling = JsonCommentHandling.Skip;
        read.AllowTrailingCommas = true;
        // JsonSerializer expects the JSON value rather than a leading UTF-8 BOM.
        int offset = _source.Length >= 3 && _source[0] == 0xEF && _source[1] == 0xBB && _source[2] == 0xBF ? 3 : 0;
        return JsonSerializer.Deserialize<T>(_source.AsSpan(offset), read);
    }

    /// <summary>Writes the current UTF-8 bytes to a file.</summary>
    /// <param name="path">Destination path.</param>
    public void Save(string path) => File.WriteAllBytes(path, _source);

    /// <summary>Checks whether a property or array element exists at a path.</summary>
    /// <param name="path">Path to inspect; an empty path refers to the root.</param>
    /// <returns><see langword="true"/> if the value exists; otherwise, <see langword="false"/>.</returns>
    public bool Contains(JsoncPath path)
    {
        var segments = path.Segments ?? throw new ArgumentException("Uninitialized path.", nameof(path));
        var node = _root;
        foreach (var segment in segments)
        {
            var next = FindChild(node, segment);
            if (next is null) return false;
            node = next;
        }
        return true;
    }

    /// <summary>Sets a value at a period-separated property path.</summary>
    /// <typeparam name="T">Type serialized by <see cref="JsonSerializer"/>.</typeparam>
    /// <param name="dottedPath">Property-only path, such as <c>logging.level</c>.</param>
    /// <param name="value">New value.</param>
    /// <remarks>Use <see cref="JsoncPath"/> for array indices or names containing periods.</remarks>
    public void Set<T>(string dottedPath, T value) => Set(JsoncPath.Properties(dottedPath), value);

    /// <summary>Replaces an existing value or adds a missing final property or array element.</summary>
    /// <typeparam name="T">Type serialized by <see cref="JsonSerializer"/>.</typeparam>
    /// <param name="path">Target path. For arrays, index <c>Count</c> appends.</param>
    /// <param name="value">New value.</param>
    /// <remarks>Parent containers must already exist. Replacing a container replaces trivia inside its span.</remarks>
    public void Set<T>(JsoncPath path, T value)
    {
        var segments = path.Segments ?? throw new ArgumentException("Uninitialized path.", nameof(path));
        if (segments.Count == 0) throw new ArgumentException("Use ReplaceRoot to change the root.", nameof(path));
        var parent = ResolveParent(segments);
        // Let STJ apply converters and escaping; the editor only chooses the byte span.
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(value, _writeOptions);
        if (parent.Type == JsonTokenType.StartObject && segments[^1] is string name)
        {
            var child = parent.Children.Find(x => x.Name == name);
            if (child is null) Insert(parent, name, serialized);
            else Apply(child.Start, child.End, serialized);
        }
        else if (parent.Type == JsonTokenType.StartArray && segments[^1] is int index)
        {
            if (index < 0 || index > parent.Children.Count) throw new ArgumentOutOfRangeException(nameof(path));
            if (index == parent.Children.Count) Insert(parent, null, serialized);
            else { var child = parent.Children[index]; Apply(child.Start, child.End, serialized); }
        }
        else throw new InvalidOperationException("Path segment does not match its container.");
    }

    /// <summary>Adds a new object property or appends an array element.</summary>
    /// <typeparam name="T">Type serialized by <see cref="JsonSerializer"/>.</typeparam>
    /// <param name="path">New property path or array path with index equal to <c>Count</c>.</param>
    /// <param name="value">Value to add.</param>
    /// <exception cref="ArgumentException">The property exists or the index is not the append position.</exception>
    public void Add<T>(JsoncPath path, T value)
    {
        var segments = path.Segments ?? throw new ArgumentException("Uninitialized path.", nameof(path));
        if (segments.Count == 0) throw new ArgumentException("Empty path.", nameof(path));
        var parent = ResolveParent(segments);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _writeOptions);
        if (parent.Type == JsonTokenType.StartObject && segments[^1] is string name)
        {
            if (parent.Children.Any(x => x.Name == name)) throw new ArgumentException("Property already exists.", nameof(path));
            Insert(parent, name, bytes);
        }
        else if (parent.Type == JsonTokenType.StartArray && segments[^1] is int index)
        {
            if (index != parent.Children.Count) throw new ArgumentException("Array additions append at Count.", nameof(path));
            Insert(parent, null, bytes);
        }
        else throw new InvalidOperationException("Path segment does not match its container.");
    }

    /// <summary>Removes an object property at a period-separated path.</summary>
    /// <param name="dottedPath">Property-only path to remove.</param>
    public void Remove(string dottedPath) => Remove(JsoncPath.Properties(dottedPath));

    /// <summary>Removes an object property or array element and its owned comments.</summary>
    /// <param name="path">Path to an existing member or element.</param>
    /// <remarks>A preceding comment-only run belongs to the member unless a blank line separates it.</remarks>
    /// <exception cref="KeyNotFoundException">The target path does not exist.</exception>
    public void Remove(JsoncPath path)
    {
        var segments = path.Segments ?? throw new ArgumentException("Uninitialized path.", nameof(path));
        if (segments.Count == 0) throw new ArgumentException("Cannot remove root.", nameof(path));
        var parent = ResolveParent(segments);
        var child = FindChild(parent, segments[^1]) ?? throw new KeyNotFoundException("Path not found.");
        var siblings = parent.Children;
        int ordinal = siblings.IndexOf(child);
        // Distinguish the name token from the value token so object removal includes the name.
        int memberStart = MemberStart(child);
        int ownedStart = OwnedStart(child, parent);
        int after = MemberLineEnd(child, parent);
        if (siblings.Count == 1)
        {
            // No separator remains to repair when the container becomes empty.
            Apply(ownedStart, after, []);
            return;
        }
        if (ordinal < siblings.Count - 1)
        {
            var next = siblings[ordinal + 1];
            int nextStart = OwnedStart(next, parent);
            // Removing a non-last entry consumes its following separator, but not the next entry's comments.
            Apply(ownedStart, nextStart, []);
        }
        else
        {
            var previous = siblings[ordinal - 1];
            // Last-member removal consumes the preceding comma rather than a following one.
            int comma = FindComma(previous.End, memberStart);
            if (comma < 0) throw new JsonException("Missing separator.");
            bool keepTrailingComma = FindComma(child.End, parent.End - 1) >= 0;
            if (ownedStart > comma && _source.AsSpan(comma + 1, ownedStart - comma - 1).IndexOf((byte)'\n') >= 0)
            {
                // Preserve an inline comment attached to the previous member.
                var candidate = Splice(_source, ownedStart, after, []);
                candidate = Splice(candidate, comma, comma + 1, []);
                if (keepTrailingComma) candidate = Splice(candidate, previous.End, previous.End, ","u8.ToArray());
                Commit(candidate);
            }
            else
            {
                var candidate = Splice(_source, comma, after, []);
                if (keepTrailingComma) candidate = Splice(candidate, previous.End, previous.End, ","u8.ToArray());
                Commit(candidate);
            }
        }
    }

    /// <summary>Replaces the root JSON value while retaining text outside its span.</summary>
    /// <typeparam name="T">Type serialized by <see cref="JsonSerializer"/>.</typeparam>
    /// <param name="value">New root value.</param>
    /// <remarks>Comments inside the old root value are replaced with that value.</remarks>
    public void ReplaceRoot<T>(T value) => Apply(_root.Start, _root.End, JsonSerializer.SerializeToUtf8Bytes(value, _writeOptions));

    /// <summary>Resolves every path segment except the final edit target.</summary>
    /// <param name="segments">Full target path.</param>
    /// <returns>The existing parent container.</returns>
    private JsoncNode ResolveParent(IReadOnlyList<object> segments)
    {
        var node = _root;
        for (int i = 0; i < segments.Count - 1; i++)
            node = FindChild(node, segments[i]) ?? throw new KeyNotFoundException("Path not found.");
        return node;
    }

    /// <summary>Finds a direct child using the segment type appropriate for its container.</summary>
    /// <param name="parent">Container to inspect.</param>
    /// <param name="segment">Property name or zero-based array index.</param>
    /// <returns>The child, or null when it does not exist.</returns>
    private static JsoncNode? FindChild(JsoncNode parent, object segment) =>
        parent.Type switch
        {
            JsonTokenType.StartObject when segment is string name => parent.Children.Find(x => x.Name == name),
            JsonTokenType.StartArray when segment is int index && index >= 0 && index < parent.Children.Count => parent.Children[index],
            _ => null
        };

    /// <summary>Inserts a serialized value near a container's closing delimiter.</summary>
    /// <param name="parent">Destination object or array.</param>
    /// <param name="name">Property name for an object; null for an array.</param>
    /// <param name="value">Already serialized UTF-8 value.</param>
    private void Insert(JsoncNode parent, string? name, byte[] value)
    {
        bool isObject = parent.Type == JsonTokenType.StartObject;
        if (!isObject && parent.Type != JsonTokenType.StartArray) throw new InvalidOperationException("Target is not a container.");
        string item = isObject ? JsonSerializer.Serialize(name) + ": " + Encoding.UTF8.GetString(value) : Encoding.UTF8.GetString(value);
        // A closing delimiter on its own line is the reliable signal for line-oriented insertion.
        bool multi = ContainsNewline(parent.Start, parent.End);
        int close = parent.End - 1;
        if (!Whitespace(LineStart(close), close)) multi = false;
        var last = parent.Children.LastOrDefault();
        // Preserve the container's existing trailing-comma convention.
        bool trailing = last is not null && FindComma(last.End, close) >= 0;
        if (!multi)
        {
            string prefix = last is null ? "" : trailing ? " " : ", ";
            // A line comment could hide an inserted compact member; reject that layout.
            if (last is not null && !trailing && FindComma(last.End, close) < 0 && ContainsLineComment(last.End, close))
                throw new InvalidOperationException("Cannot insert into a compact container after a line comment.");
            Apply(close, close, Encoding.UTF8.GetBytes(prefix + item + (trailing ? "," : "")));
            return;
        }
        string newline = DetectNewline();
        string indent = ChildIndent(parent);
        int lineStart = LineStart(close);
        // Insert before the closing line's indentation to leave that line untouched.
        int atPosition = lineStart;
        string insertion = indent + item + (trailing ? "," : "") + newline;
        if (last is null)
        {
            // The closing delimiter already has its own indentation line.
            Apply(atPosition, atPosition, Encoding.UTF8.GetBytes(insertion));
        }
        else if (trailing)
        {
            Apply(atPosition, atPosition, Encoding.UTF8.GetBytes(insertion));
        }
        else
        {
            // Add the separator to the previous value, before an inline comment.
            var updated = Splice(_source, last.End, last.End, ","u8.ToArray());
            updated = Splice(updated, atPosition + 1, atPosition + 1, Encoding.UTF8.GetBytes(insertion));
            Commit(updated);
        }
    }

    /// <summary>Builds and validates a candidate containing one span replacement.</summary>
    /// <param name="start">Inclusive byte offset.</param>
    /// <param name="end">Exclusive byte offset.</param>
    /// <param name="replacement">Bytes replacing the selected span.</param>
    private void Apply(int start, int end, byte[] replacement) => Commit(Splice(_source, start, end, replacement));

    /// <summary>Publishes an edit only after the BCL reader accepts its complete result.</summary>
    /// <param name="candidate">Candidate UTF-8 source bytes.</param>
    private void Commit(byte[] candidate)
    {
        // Reindex after validation because every later path must use updated byte offsets.
        var index = JsoncIndex.Parse(candidate);
        _source = candidate;
        _root = index;
    }

    /// <summary>Copies unchanged prefix and suffix bytes around one replacement.</summary>
    /// <param name="source">Original UTF-8 bytes.</param>
    /// <param name="start">Inclusive replacement start.</param>
    /// <param name="end">Exclusive replacement end.</param>
    /// <param name="replacement">Inserted bytes.</param>
    /// <returns>A new byte array; <paramref name="source"/> is unchanged.</returns>
    private static byte[] Splice(byte[] source, int start, int end, byte[] replacement)
    {
        if (start < 0 || end < start || end > source.Length) throw new ArgumentOutOfRangeException(nameof(start));
        var result = new byte[source.Length - (end - start) + replacement.Length];
        Buffer.BlockCopy(source, 0, result, 0, start);
        Buffer.BlockCopy(replacement, 0, result, start, replacement.Length);
        Buffer.BlockCopy(source, end, result, start + replacement.Length, source.Length - end);
        return result;
    }

    /// <summary>Returns the name token start for a property or value start for an array item.</summary>
    /// <param name="child">Indexed member.</param>
    /// <returns>Member's first structural byte offset.</returns>
    private int MemberStart(JsoncNode child) => child.Name is null ? child.Start : child.NameStart;

    /// <summary>Finds the first byte after the previous newline.</summary>
    /// <param name="position">Byte offset within the current source.</param>
    /// <returns>Start offset of the containing line.</returns>
    private int LineStart(int position)
    {
        while (position > 0 && _source[position - 1] != (byte)'\n') position--;
        return position;
    }
    /// <summary>Finds the byte after the next newline, or the end of the source.</summary>
    /// <param name="position">Byte offset within the current source.</param>
    /// <returns>Exclusive end offset of the containing line.</returns>
    private int LineEnd(int position)
    {
        while (position < _source.Length && _source[position] != (byte)'\n') position++;
        return position < _source.Length ? position + 1 : position;
    }
    /// <summary>Checks whether a byte interval contains only JSON whitespace.</summary>
    /// <param name="start">Inclusive start offset.</param>
    /// <param name="end">Exclusive end offset.</param>
    /// <returns>True if every byte is whitespace.</returns>
    private bool Whitespace(int start, int end)
    {
        for (int i = start; i < end; i++) if (_source[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        return true;
    }
    /// <summary>Finds the first line of comments owned by a member.</summary>
    /// <param name="child">Member being edited.</param>
    /// <param name="parent">Its containing object or array.</param>
    /// <returns>Start of owned comments, or the member start when none qualify.</returns>
    private int OwnedStart(JsoncNode child, JsoncNode parent)
    {
        int member = MemberStart(child);
        int line = LineStart(member);
        if (line <= parent.Start || !Whitespace(line, member)) return member;
        int candidate = line;
        int floor = parent.Children.IndexOf(child) is var n && n > 0 ? parent.Children[n - 1].End : parent.Start + 1;
        // Walk backward only across consecutive dedicated comment lines; a blank line stops ownership.
        while (candidate > floor)
        {
            var comment = PreviousDedicatedComment(floor, candidate);
            if (comment is null) break;
            candidate = LineStart(comment.Value.Start);
        }
        return candidate;
    }
    /// <summary>Finds a comment occupying the line immediately before a member/comment line.</summary>
    /// <param name="floor">Earliest byte that may belong to the member.</param>
    /// <param name="candidate">Start of the current member/comment line.</param>
    /// <returns>The preceding comment span, or null.</returns>
    private (int Start, int End)? PreviousDedicatedComment(int floor, int candidate)
    {
        int offset = _source.Length >= 3 && _source[0] == 0xEF && _source[1] == 0xBB && _source[2] == 0xBF ? 3 : 0;
        var reader = new Utf8JsonReader(_source.AsSpan(offset), new JsonReaderOptions
        { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        (int Start, int End)? result = null;
        // Reader tokens handle multi-line block comments without a second handwritten lexer.
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.Comment) continue;
            int start = checked((int)reader.TokenStartIndex) + offset;
            int end = checked((int)reader.BytesConsumed) + offset;
            if (start < floor || end > candidate) continue;
            if (!Whitespace(LineStart(start), start) || !Whitespace(end, candidate)) continue;
            // The reader may consume the newline after //; account for both token endings.
            int nextLine = end > start && _source[end - 1] == (byte)'\n' ? end : LineEnd(end);
            if (nextLine == candidate && (result is null || end > result.Value.End)) result = (start, end);
        }
        return result;
    }
    /// <summary>Extends a member span over its inline trivia when removing it.</summary>
    /// <param name="child">Member being removed.</param>
    /// <param name="parent">Containing node, whose delimiter bounds the scan.</param>
    /// <returns>Exclusive removal end offset.</returns>
    private int MemberLineEnd(JsoncNode child, JsoncNode parent)
    {
        int end = child.End;
        int lineEnd = LineEnd(end);
        int limit = parent.End - 1;
        if (lineEnd <= limit && IsOnlyTrivia(end, lineEnd)) return lineEnd;
        int comma = FindComma(end, Math.Min(lineEnd, limit));
        return comma >= 0 ? comma + 1 : end;
    }
    /// <summary>Checks whether a line suffix has only separators, whitespace, and comments.</summary>
    /// <param name="start">Inclusive suffix start.</param>
    /// <param name="end">Exclusive suffix end.</param>
    /// <returns>True when deleting through the line ending is safe.</returns>
    private bool IsOnlyTrivia(int start, int end)
    {
        for (int i = start; i < end;)
        {
            if (_source[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') { i++; continue; }
            if (i + 1 < end && _source[i] == (byte)'/' && _source[i + 1] == (byte)'/') return true;
            if (i + 1 < end && _source[i] == (byte)'/' && _source[i + 1] == (byte)'*')
            {
                i += 2;
                while (i + 1 < end && !(_source[i] == (byte)'*' && _source[i + 1] == (byte)'/')) i++;
                i += 2;
                continue;
            }
            if (_source[i] == (byte)',') { i++; continue; }
            return false;
        }
        return true;
    }
    /// <summary>Finds a separator comma while skipping comment contents.</summary>
    /// <param name="start">Inclusive scan start.</param>
    /// <param name="end">Exclusive scan end.</param>
    /// <returns>The comma offset, or -1 if absent.</returns>
    private int FindComma(int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (i + 1 < end && _source[i] == (byte)'/' && _source[i + 1] == (byte)'/')
            { while (i < end && _source[i] != (byte)'\n') i++; continue; }
            if (i + 1 < end && _source[i] == (byte)'/' && _source[i + 1] == (byte)'*')
            { i += 2; while (i + 1 < end && !(_source[i] == (byte)'*' && _source[i + 1] == (byte)'/')) i++; i++; continue; }
            if (_source[i] == (byte)',') return i;
        }
        return -1;
    }
    /// <summary>Checks whether a byte span crosses a line boundary.</summary>
    /// <param name="start">Inclusive scan start.</param>
    /// <param name="end">Exclusive scan end.</param>
    /// <returns>True when the interval contains LF.</returns>
    private bool ContainsNewline(int start, int end)
    { for (int i = start; i < end; i++) if (_source[i] == (byte)'\n') return true; return false; }
    /// <summary>Detects a line comment in compact insertion trivia.</summary>
    /// <param name="start">Inclusive scan start.</param>
    /// <param name="end">Exclusive scan end.</param>
    /// <returns>True when a line-comment delimiter appears.</returns>
    private bool ContainsLineComment(int start, int end)
    { for (int i = start; i + 1 < end; i++) if (_source[i] == (byte)'/' && _source[i + 1] == (byte)'/') return true; return false; }
    /// <summary>Infers the first newline convention present in the source.</summary>
    /// <returns>CRLF, LF, or the environment newline when none exists.</returns>
    private string DetectNewline()
    {
        for (int i = 0; i < _source.Length; i++)
            if (_source[i] == (byte)'\n') return i > 0 && _source[i - 1] == (byte)'\r' ? "\r\n" : "\n";
        return Environment.NewLine;
    }
    /// <summary>Infers indentation for a new direct child of a container.</summary>
    /// <param name="parent">Container receiving the child.</param>
    /// <returns>Sibling indentation or closing-line indentation plus one unit.</returns>
    private string ChildIndent(JsoncNode parent)
    {
        if (parent.Children.Count > 0)
        {
            // Existing sibling indentation takes precedence over global guesses.
            var child = parent.Children[^1];
            int line = LineStart(MemberStart(child));
            if (Whitespace(line, MemberStart(child))) return Encoding.UTF8.GetString(_source, line, MemberStart(child) - line);
        }
        int closeLine = LineStart(parent.End - 1);
        int close = parent.End - 1;
        string baseIndent = Whitespace(closeLine, close) ? Encoding.UTF8.GetString(_source, closeLine, close - closeLine) : "";
        // Empty containers use a small default; an observed tab switches that unit to a tab.
        string unit = "  ";
        for (int i = 0; i < _source.Length; i++) if (_source[i] == (byte)'\t') { unit = "\t"; break; }
        return baseIndent + unit;
    }
}
