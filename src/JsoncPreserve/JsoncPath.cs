namespace JsoncPreserve;

/// <summary>Identifies a JSON value using property names and zero-based array indices.</summary>
/// <remarks>String segments are literal property names, so they may contain periods.</remarks>
public readonly struct JsoncPath
{
    /// <summary>Gets the ordered path segments.</summary>
    /// <remarks>Each segment is a <see cref="string"/> property name or an <see cref="int"/> array index.</remarks>
    public IReadOnlyList<object> Segments { get; }

    /// <summary>Creates a path from property names and array indices.</summary>
    /// <param name="segments">Ordered segments from the root to the target value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is null.</exception>
    /// <exception cref="ArgumentException">A segment is neither a string nor an integer.</exception>
    public JsoncPath(params object[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        // Clone the caller's array so later changes cannot redirect a saved path.
        foreach (var segment in segments)
            if (segment is not string && segment is not int)
                throw new ArgumentException("Path segments must be strings or integer indices.", nameof(segments));
        Segments = Array.AsReadOnly((object[])segments.Clone());
    }

    /// <summary>Creates a property-only path from period-separated names.</summary>
    /// <param name="dottedPath">Property names separated by periods.</param>
    /// <returns>A path whose segments are all property names.</returns>
    /// <remarks>Use the constructor when a property name itself contains a period.</remarks>
    /// <exception cref="ArgumentException">The path or one of its segments is empty.</exception>
    public static JsoncPath Properties(string dottedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dottedPath);
        var parts = dottedPath.Split('.');
        if (parts.Any(string.IsNullOrEmpty)) throw new ArgumentException("Empty path segment.", nameof(dottedPath));
        return new JsoncPath(parts.Cast<object>().ToArray());
    }
}
