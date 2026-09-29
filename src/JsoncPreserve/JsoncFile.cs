using System.Text.Json;

namespace JsoncPreserve;

/// <summary>Loads a POCO from JSONC and applies its changes to the original source on save.</summary>
/// <typeparam name="T">The type deserialized and serialized by <see cref="JsonSerializer"/>.</typeparam>
/// <remarks>For precise structural edits, use <see cref="JsoncDocument"/> directly.</remarks>
public sealed class JsoncFile<T>
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options;
    private JsonElement _snapshot;

    /// <summary>Gets the current source-preserving document.</summary>
    /// <remarks>After a successful save, this property refers to the validated edited document.</remarks>
    public JsoncDocument Document { get; private set; }

    /// <summary>Gets or replaces the POCO whose changes will be saved.</summary>
    public T Value { get; set; }

    /// <summary>Initializes the file and records the POCO's initial serialized shape.</summary>
    /// <param name="path">Destination path used by <see cref="Save"/>.</param>
    /// <param name="document">Original source-preserving document.</param>
    /// <param name="options">Serializer options used for both snapshots and output.</param>
    private JsoncFile(string path, JsoncDocument document, JsonSerializerOptions options)
    {
        _path = path;
        _options = new JsonSerializerOptions(options);
        Document = document;
        Value = document.Deserialize<T>(_options) ?? throw new JsonException("The JSONC root deserialized to null.");
        // Compare serialized POCO shapes so STJ attributes and converters remain authoritative.
        _snapshot = JsonSerializer.SerializeToElement(Value, _options);
    }

    /// <summary>Loads a JSONC file and deserializes its root value.</summary>
    /// <param name="path">Path to the UTF-8 JSONC file.</param>
    /// <param name="options">Optional System.Text.Json options for POCO conversion.</param>
    /// <returns>A file wrapper containing the parsed document and POCO.</returns>
    /// <exception cref="JsonException">The source is invalid JSONC or deserializes to null.</exception>
    public static JsoncFile<T> Load(string path, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var effective = options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        return new JsoncFile<T>(path, JsoncDocument.Load(path, effective), effective);
    }

    /// <summary>Gets the JSONC bytes that would be saved for the current POCO.</summary>
    /// <returns>A defensive copy of the updated UTF-8 bytes.</returns>
    /// <remarks>This preview does not write a file or advance the in-memory change baseline.</remarks>
    public byte[] ToUtf8Bytes() => Prepare().Candidate.ToUtf8Bytes();

    /// <summary>Gets the JSONC text that would be saved for the current POCO.</summary>
    /// <returns>The updated JSONC text.</returns>
    /// <remarks>This preview does not write a file or advance the in-memory change baseline.</remarks>
    public string ToJsoncString() => Prepare().Candidate.ToString();

    /// <summary>Applies POCO changes and saves with a same-directory temporary file.</summary>
    /// <remarks>A failed diff or file replacement does not advance the in-memory change baseline.</remarks>
    public void Save() => SaveAtomic();

    /// <summary>Applies POCO changes and atomically replaces the original file.</summary>
    /// <remarks>The candidate is validated before writing; the in-memory baseline advances only after replacement succeeds.</remarks>
    public void SaveAtomic()
    {
        var (candidate, current) = Prepare();
        candidate.SaveAtomic(_path);
        Accept(candidate, current);
    }

    /// <summary>Applies POCO changes using an application-provided persistence function.</summary>
    /// <param name="write">
    /// Function that writes the supplied bytes to the supplied path and throws on failure.
    /// It must persist the bytes unchanged before returning.
    /// </param>
    /// <remarks>The in-memory document and diff baseline advance only after <paramref name="write"/> returns successfully.</remarks>
    public void SaveWith(Action<string, byte[]> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var (candidate, current) = Prepare();
        write(_path, candidate.ToUtf8Bytes());
        Accept(candidate, current);
    }

    /// <summary>Builds a validated candidate without changing the current document or snapshot.</summary>
    /// <returns>The candidate and its serialized POCO snapshot.</returns>
    private (JsoncDocument Candidate, JsonElement Current) Prepare()
    {
        var current = JsonSerializer.SerializeToElement(Value, _options);
        // Keep the original document untouched until every proposed edit validates.
        var candidate = JsoncDocument.Parse(Document.ToUtf8Bytes(), _options);
        Diff(_snapshot, current, [], candidate);
        return (candidate, current);
    }

    /// <summary>Accepts the validated candidate after its bytes have been persisted.</summary>
    /// <param name="candidate">Document that was written.</param>
    /// <param name="current">Serialized POCO state corresponding to the written bytes.</param>
    private void Accept(JsoncDocument candidate, JsonElement current)
    {
        Document = candidate;
        _snapshot = current.Clone();
    }

    /// <summary>Recursively translates serialized POCO differences into local document edits.</summary>
    /// <param name="oldValue">Value in the previous serialized snapshot.</param>
    /// <param name="newValue">Value in the current serialized snapshot.</param>
    /// <param name="path">Path to both values.</param>
    /// <param name="document">Candidate document receiving edits.</param>
    private void Diff(JsonElement oldValue, JsonElement newValue, List<object> path, JsoncDocument document)
    {
        if (path.Count > 0 && !document.Contains(new JsoncPath(path.ToArray())))
        {
            // A POCO default may have existed in the snapshot but not in the file.
            // Add its nearest missing parent only when the value actually changed.
            if (oldValue.GetRawText() != newValue.GetRawText()) document.Set(new JsoncPath(path.ToArray()), newValue);
            return;
        }
        if (oldValue.ValueKind == JsonValueKind.Object && newValue.ValueKind == JsonValueKind.Object)
        {
            // Diff serialized property names so naming policies and attributes remain authoritative.
            var oldProperties = oldValue.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
            var newProperties = newValue.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
            foreach (var oldProperty in oldProperties)
            {
                var childPath = Append(path, oldProperty.Key);
                if (!newProperties.TryGetValue(oldProperty.Key, out var next))
                {
                    // Preserve unrelated source properties that the POCO never represented.
                    var target = new JsoncPath(childPath.ToArray());
                    if (document.Contains(target)) document.Remove(target);
                }
                else Diff(oldProperty.Value, next, childPath, document);
            }
            foreach (var newProperty in newProperties)
                if (!oldProperties.ContainsKey(newProperty.Key))
                    document.Set(new JsoncPath(Append(path, newProperty.Key).ToArray()), newProperty.Value);
            return;
        }
        if (oldValue.ValueKind == JsonValueKind.Array && newValue.ValueKind == JsonValueKind.Array)
        {
            // Edit common indices first; remove from the end so indices do not shift early.
            int oldCount = oldValue.GetArrayLength(), newCount = newValue.GetArrayLength();
            for (int i = 0; i < Math.Min(oldCount, newCount); i++) Diff(oldValue[i], newValue[i], Append(path, i), document);
            for (int i = oldCount - 1; i >= newCount; i--) document.Remove(new JsoncPath(Append(path, i).ToArray()));
            for (int i = oldCount; i < newCount; i++) document.Add(new JsoncPath(Append(path, i).ToArray()), newValue[i]);
            return;
        }
        if (oldValue.GetRawText() == newValue.GetRawText()) return;
        if (path.Count == 0) document.ReplaceRoot(newValue);
        else document.Set(new JsoncPath(path.ToArray()), newValue);
    }

    /// <summary>Builds a child path without changing the parent's reusable segment list.</summary>
    /// <param name="path">Parent path.</param>
    /// <param name="segment">Property name or array index to append.</param>
    /// <returns>A new path segment list.</returns>
    private static List<object> Append(List<object> path, object segment)
    {
        var result = new List<object>(path) { segment };
        return result;
    }
}
