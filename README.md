# JsoncPreserve

JsoncPreserve is a .NET library for editing JSON with comments (JSONC) while retaining the original text around each edit. It uses `System.Text.Json` for JSON parsing, serialization, and POCO conversion. It has no Node.js or Newtonsoft.Json dependency.

## Why?

`JsonSerializer` can read comments and trailing commas, but serializing a POCO again removes comments and rewrites formatting. JsoncPreserve keeps the original UTF-8 source and replaces only the affected spans.

```jsonc
{
  // Number of warm containers
  "warmPoolSize": 3,
  "logLevel": "Warning" // production default
}
```

Changing `warmPoolSize` to `5` changes only the byte for `3`.

## Installation

Version **0.1.0** is published on [NuGet.org](https://www.nuget.org/packages/JsoncPreserve/0.1.0). In Visual Studio:

1. Right-click the project that will use the library in Solution Explorer and select **Manage NuGet Packages**.
2. Select **nuget.org** as the package source, then open the **Browse** tab.
3. Search for **JsoncPreserve**, select version **0.1.0**, and click **Install**. Accept the license prompt if shown.
4. Confirm that **JsoncPreserve** appears under the project's **Dependencies > Packages**.

The library supports .NET 8 and .NET 10. Install it into each project that directly uses its types. From the command line, run this in the consuming project directory:

```sh
dotnet add package JsoncPreserve --version 0.1.0
```

Use **nuget.org** as the source for normal installation. GitHub Releases provides a downloadable package archive, and GitHub Packages is a separate feed that requires GitHub authentication.

## Basic usage

```csharp
using JsoncPreserve;

var document = JsoncDocument.Load("server.jsonc");
document.Set("warmPoolSize", 5);
document.Set(new JsoncPath("logging", "level"), "Information");
document.Add(new JsoncPath("servers", 2), "https://example.org"); // append at Count
document.Remove("obsoleteSetting");
document.Save("server.jsonc");
```

Property paths in strings are dot-separated. Use `JsoncPath` for array indices or property names containing dots. `Set` changes an existing value or adds a missing final property; for arrays, index `Count` appends. `Add` requires a new property or append index. `Remove` deletes a property or array element.

## Replace an existing `System.Text.Json` read/edit/write flow

Suppose `server.jsonc` contains the JSONC example above. Both programs below change `warmPoolSize` from `3` to `5` using the same POCO and serializer options.

**Before — `System.Text.Json` only (`Program.cs`):**

```csharp
using System.Text.Json;

var path = "server.jsonc";
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true
};

var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), options)!;
config.WarmPoolSize = 5;
File.WriteAllText(path, JsonSerializer.Serialize(config, options));

public sealed class ServerConfig
{
    public int WarmPoolSize { get; set; }
    public string LogLevel { get; set; } = "";
}
```

This reads the JSONC, but writing it again removes both comments and regenerates the entire file.

**After — `JsoncPreserve` (`Program.cs`):**

```csharp
using System.Text.Json;
using JsoncPreserve;

var path = "server.jsonc";
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true
};

var file = JsoncFile<ServerConfig>.Load(path, options);
file.Value.WarmPoolSize = 5;
file.Save(); // writes a temporary file, then replaces server.jsonc

public sealed class ServerConfig
{
    public int WarmPoolSize { get; set; }
    public string LogLevel { get; set; } = "";
}
```

The read/edit/write block changes from three lines to three lines; the POCO and serializer options stay the same. The output retains the original layout:

```jsonc
{
  // Number of warm containers
  "warmPoolSize": 5,
  "logLevel": "Warning" // production default
}
```

`JsoncFile<T>` serializes the POCO before and after editing, then applies changed paths to the original document. `JsonPropertyName`, `JsonIgnore`, converters, naming policies, enums, nullable values, collections, and nested POCOs are handled by `System.Text.Json`. Unknown source properties remain untouched. Use `JsoncDocument` when precise structural control matters.

### Preview, custom persistence, and atomic saving

```csharp
var file = JsoncFile<ServerConfig>.Load("server.jsonc", options);
file.Value.WarmPoolSize = 5;

string preview = file.ToJsoncString(); // updated JSONC, no file write
byte[] bytes = file.ToUtf8Bytes();      // UTF-8 output for a custom writer

file.SaveAtomic(); // explicit form of Save()
```

Both preview methods leave the file and the in-memory change baseline untouched. As an alternative to `SaveAtomic()`, use an existing persistence function:

```csharp
file.SaveWith(WriteAtomic); // application method: void WriteAtomic(string path, byte[] bytes)
```

The callback must write the supplied bytes unchanged and throw if saving fails. The library updates its baseline only after the callback returns successfully. If you write preview bytes separately, reload the wrapper before subsequent edits.

`Save()` and `SaveAtomic()` write a temporary file in the destination directory and replace the destination only after the write succeeds. `JsoncDocument.Save(path)` and `JsoncDocument.SaveAtomic(path)` use the same approach. On Unix, an existing file's mode is retained; the methods do not guarantee persistence across a machine crash.

## Round-trip preservation

- No edit: input UTF-8 bytes equal output bytes, including a UTF-8 BOM, comments, line endings, spaces, tabs, and trailing commas.
- Existing value edit: bytes outside that value span remain unchanged.
- Add or remove: only the local member/element and required separator are changed. Indentation, newline, and trailing-comma style are inferred locally.
- A comment-only run immediately above a member belongs to it unless separated by a blank line. Its inline trailing comment belongs to it. These comments are removed with the member.

## Limitations

UTF-8 is required. Duplicate object keys are rejected because paths would be ambiguous. Dotted paths are not JSONPath. Inserted values use `JsonSerializer` formatting and its default string escaping unless you supply options. Complex same-line trivia may not match hand alignment. Replacing a whole object or array also replaces comments inside that value. POCO diffing is intended for ordinary object graphs; converters that change shape or array reordering can cause larger edits. See [design](docs/DESIGN.md) and [round-trip rules](docs/ROUNDTRIP.md).

## Build and test

```sh
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack src/JsoncPreserve/JsoncPreserve.csproj -c Release
```

The library targets .NET 8 and .NET 10. The library has no external package dependencies; the test project uses xUnit.

## License

MIT. See [LICENSE](LICENSE). The implementation is original .NET code; no `node-jsonc-parser` code was copied.
