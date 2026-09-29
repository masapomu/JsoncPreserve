# JsoncPreserve design

## Problem
`System.Text.Json` can read JSON with comments and trailing commas, but its DOM and serializer discard source trivia. Configuration edits should retain handwritten text.

## Goals
Exact no-op UTF-8 round trips; small replacements of existing values; predictable object and array insertion and deletion; direct use of `System.Text.Json` for JSON validation, values, and POCO conversion. Support comments, trailing commas, LF/CRLF, tabs, and spaces.

## Non-goals
A new serializer, JSON5, schema validation, formatting whole files, and application-specific behavior.

## Architecture and responsibilities
`JsoncDocument` owns the original UTF-8 bytes. An internal index maps containers, properties, and array elements to byte spans. `Utf8JsonReader` validates syntax and supplies token positions, types, depths, and decoded property names. A small indexer pairs property names with values and records container membership; it does not reinterpret JSON values. `JsonSerializer` produces replacement value text and deserializes POCOs. After an edit, the index is rebuilt so subsequent edits use current positions.

`JsoncFile<T>` loads a document and POCO. On save it serializes the POCO to `JsonElement`, compares it with the original semantic snapshot, and applies changed leaf values. Additions and removals use the document editor. Preview methods build the same candidate without writing or advancing the snapshot. `SaveWith` delegates persistence to an application callback and advances the snapshot only if it succeeds. This API is intentionally limited to ordinary object graphs; converters that change object shape or duplicate keys can make structural matching ambiguous. The low-level API is the definitive v1 editing surface.

## CST and trivia model
Each indexed value has a start and end byte offset; containers hold ordered members. Properties also carry name token offsets. Comments are reader tokens with positions, retained in the original byte buffer. Whitespace, punctuation, and line endings remain in that buffer. No-op output is the same buffer, byte for byte, including a UTF-8 BOM. Offsets come from `TokenStartIndex` and `BytesConsumed`; the reader's `GetString` decodes property names. `ValueSpan` and `CurrentDepth` are available but are not needed because the reader supplies decoded names and parentage is established with a stack.

## Parsing strategy
Read UTF-8 with `JsonReaderOptions { CommentHandling = Allow, AllowTrailingCommas = true }`. Reject invalid UTF-8 before reading. Ignore comment tokens for structural indexing while preserving their spans in source. The BCL reader enforces JSON syntax. The indexer tracks pending property names and matching end tokens. Duplicate object keys are rejected for editing because paths would be ambiguous.

## Edit strategy
Existing scalar or container values are replaced at their exact value span with `JsonSerializer.SerializeToUtf8Bytes`; sibling text is untouched. Property and array additions insert the smallest separator and new member/element near the closing delimiter. Deletion removes a member's syntactic span and one separator. Every candidate edit is parsed and validated before committing. A failed edit leaves the document unchanged. Paths are typed segments (`string` property or `int` array index); dotted strings are convenience syntax for property-only paths.

## Comment ownership
An immediately preceding run of comment-only lines at the member's indentation belongs to that member, stopping at a blank line. The ownership scan uses reader comment token spans, so a multi-line block comment is treated as one comment. An inline comment after a value belongs to that member. On deletion, owned comments are removed with it. Comments separated by a blank line, comments on a container's closing line, and ambiguous same-line block comments remain. Insertion does not move comments. This rule favors predictable edits; complex mixed-line trivia should be edited as a whole value or by hand.

## Formatting preservation
Unchanged bytes are never serialized. New members infer newline style from the file and indentation from sibling lines, falling back to two spaces. Existing trailing comma style is retained. Single-line containers receive compact insertions. Newly serialized values use `System.Text.Json` formatting; nested inserted values may therefore be compact.

## POCO integration
`JsoncFile<T>` clones supplied `JsonSerializerOptions`, enabling comment skipping and trailing commas for reading. It deserializes with `JsonSerializer`. Saving compares serialized JSON trees and patches differences. Existing source fields omitted by the POCO are preserved unless they correspond to a field in the initial serialized snapshot; this avoids deleting unknown settings. `JsonPropertyName`, naming policy, converters, enum converters, nullable values, and collections remain `System.Text.Json` concerns. Callers can use `JsoncDocument` for precise control.

## Error handling
Malformed JSONC throws `JsonException` (including reader subclasses); invalid UTF-8 throws `DecoderFallbackException`; invalid paths or unsupported edits throw argument or invalid-operation exceptions. A candidate is validated before replacing the in-memory buffer. `JsoncFile<T>` applies a diff to a copy, writes it to a same-directory temporary file, and replaces the destination before advancing the snapshot. `JsoncDocument.Save` uses the same strategy. Replacement does not imply a machine-crash durability guarantee.

## Performance
One reader pass builds the index. Each edit rebuilds it and copies the UTF-8 buffer, suitable for configuration files of a few KB to a few hundred KB. Bulk edits are O(edits × file size); future versions may batch non-overlapping spans.

## Compatibility
Target `net8.0` and `net10.0`. The reader position, comment, and trailing-comma APIs exist in both. `JsonDocument` and `JsonNode` are useful semantic DOMs but do not retain trivia, so they cannot own the source. The library uses only BCL references. `JsonSerializerOptions.ReadCommentHandling` uses `Skip`, since `Allow` is a reader feature and not a serializer setting.

## Known limitations
Paths do not support query expressions. Formatting inference is local and may not mimic unusual hand alignment. Duplicate keys are rejected. High-level diffing cannot preserve comments inside a value when a converter replaces that whole value. UTF-8 is the file encoding; other encodings are unsupported.

## Future work
Batch edits, configurable comment ownership, source-preserving nested insertion formatting, optional streaming for large files, and stronger POCO diff conflict detection.

## References
- [Utf8JsonReader API](https://learn.microsoft.com/dotnet/api/system.text.json.utf8jsonreader)
- [JSON with comments and trailing commas](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/invalid-json)
