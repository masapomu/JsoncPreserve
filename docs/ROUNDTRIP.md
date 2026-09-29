# Round-trip rules

The source of truth is the original UTF-8 byte array. `JsoncDocument.Parse(bytes).ToUtf8Bytes()` is exactly `bytes` when no edit occurs. UTF-8 BOM is retained. Invalid UTF-8 and malformed JSONC are rejected.

For an existing value, the parser records the start and end byte positions reported by `Utf8JsonReader`. `Set` splices the `JsonSerializer` output into that exact span. Comments and whitespace before or after the span remain untouched. Replacing a container replaces its internal comments too; edit its children to preserve them.

For additions, the editor uses the nearest sibling's indentation, the first observed newline style, and whether the container has a trailing comma. It inserts text by the closing delimiter and adds a separator to the previous value when needed. New nested values follow `JsonSerializer` formatting. It does not reformat the rest of the file.

For deletion, a run of comment-only lines immediately preceding a member belongs to that member, stopping at a blank line. An inline comment on the member's line also belongs to it. The editor removes the member, owned comments, and one comma, preserving the previous member's inline comment. An existing trailing comma remains where possible.

After each proposed edit the complete candidate text is parsed again. An invalid candidate is rejected without changing the document. `JsoncFile<T>.ToUtf8Bytes()` and `ToJsoncString()` preview the fully edited source without writing it or changing the diff baseline. `Save()` and `SaveAtomic()` prepare the same candidate, write a same-directory temporary file, replace the destination, and only then advance the baseline. `SaveWith` lets a caller provide its own persistence function; the baseline advances only when it returns successfully. `JsoncDocument.Save(path)` and `SaveAtomic(path)` use temporary-file replacement as well. This does not guarantee durability across a machine crash.
