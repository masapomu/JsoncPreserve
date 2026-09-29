using System.Text;
using System.Text.Json;

namespace JsoncPreserve;

/// <summary>Records byte spans and parentage without copying source trivia.</summary>
internal sealed class JsoncNode
{
    internal JsonTokenType Type;
    internal int Start;
    internal int End;
    internal string? Name;
    internal int NameStart;
    internal JsoncNode? Parent;
    internal List<JsoncNode> Children = [];
}

/// <summary>Builds a structural index from BCL reader tokens.</summary>
internal static class JsoncIndex
{
    /// <summary>Validates UTF-8 JSONC and indexes each semantic value.</summary>
    /// <param name="source">Complete UTF-8 source, with an optional BOM.</param>
    /// <returns>The root value node.</returns>
    internal static JsoncNode Parse(byte[] source)
    {
        // Strict decoding catches malformed UTF-8 before the reader can interpret it as JSON.
        _ = new UTF8Encoding(false, true).GetString(source);
        // Utf8JsonReader does not consume a BOM; add this offset back to every token span.
        int offset = source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF ? 3 : 0;
        var reader = new Utf8JsonReader(source.AsSpan(offset), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true
        });
        JsoncNode? root = null;
        var stack = new Stack<JsoncNode>();
        string? property = null;
        int propertyStart = -1;
        while (reader.Read())
        {
            var type = reader.TokenType;
            // Trivia remains in the source buffer and has no structural child node.
            if (type == JsonTokenType.Comment) continue;
            if (type == JsonTokenType.PropertyName)
            {
                property = reader.GetString();
                propertyStart = checked((int)reader.TokenStartIndex) + offset;
                continue;
            }
            if (type is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                // Container spans include both delimiters, unlike scalar token spans.
                if (stack.Count == 0) throw new JsonException("Unexpected container end.");
                var closed = stack.Pop();
                closed.End = checked((int)reader.BytesConsumed) + offset;
                continue;
            }
            var node = new JsoncNode
            {
                Type = type,
                Start = checked((int)reader.TokenStartIndex) + offset,
                End = checked((int)reader.BytesConsumed) + offset,
                Name = property,
                NameStart = propertyStart
            };
            if (stack.Count > 0)
            {
                var parent = stack.Peek();
                // A path cannot select a specific duplicate key, so reject that ambiguity.
                if (parent.Type == JsonTokenType.StartObject && property is null)
                    throw new JsonException("Object value without property name.");
                if (parent.Type == JsonTokenType.StartObject && parent.Children.Any(x => x.Name == property))
                    throw new JsonException($"Duplicate property '{property}' is ambiguous for editing.");
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else if (root is null) root = node;
            else throw new JsonException("Multiple root values.");
            property = null;
            propertyStart = -1;
            // The stack establishes parentage while the BCL reader validates grammar.
            if (type is JsonTokenType.StartObject or JsonTokenType.StartArray) stack.Push(node);
        }
        if (root is null || stack.Count != 0) throw new JsonException("Incomplete JSON document.");
        return root;
    }
}
