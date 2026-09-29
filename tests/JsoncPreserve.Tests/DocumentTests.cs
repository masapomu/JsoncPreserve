using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace JsoncPreserve.Tests;

public sealed class DocumentTests
{
    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("{\n  // 注釈\n  \"日本語\": \"こんにちは\",\n}")]
    [InlineData("{\r\n\t/* block */\r\n\t\"items\": [1, 2,], // inline\r\n}")]
    [InlineData("[true, false, null, {\"a\": [1]}]")]
    public void NoOpRoundTrip(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var doc = JsoncDocument.Parse(bytes);
        Assert.Equal(bytes, doc.ToUtf8Bytes());
        Assert.Equal(input, doc.ToString());
    }

    [Fact]
    public void BomRoundTripAndEdit()
    {
        byte[] input = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"日本語\": 1}")];
        var doc = JsoncDocument.Parse(input);
        Assert.Equal(input, doc.ToUtf8Bytes());
        doc.Set("日本語", 2);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"日本語\": 2}")], doc.ToUtf8Bytes());
        Assert.Equal(2, doc.Deserialize<JsonElement>().GetProperty("日本語").GetInt32());
    }

    [Fact]
    public void ReplacesOnlyOneValue()
    {
        const string source = "{\n  // foo\n  \"foo\": 1,\n\n  // bar\n  \"bar\": 2\n}";
        var doc = JsoncDocument.Parse(source);
        doc.Set("bar", 3);
        Assert.Equal(source.Replace("\"bar\": 2", "\"bar\": 3"), doc.ToString());
    }

    [Theory]
    [InlineData("{\n  \"a\": 1\n}", "{\n  \"a\": 1,\n  \"b\": 2\n}")]
    [InlineData("{\r\n    \"a\": 1,\r\n}", "{\r\n    \"a\": 1,\r\n    \"b\": 2,\r\n}")]
    [InlineData("{\n\t\"a\": 1\n}", "{\n\t\"a\": 1,\n\t\"b\": 2\n}")]
    [InlineData("{\"a\":1}", "{\"a\":1, \"b\": 2}")]
    public void AddsPropertyUsingLocalStyle(string source, string expected)
    {
        var doc = JsoncDocument.Parse(source);
        doc.Add(new JsoncPath("b"), 2);
        Assert.Equal(expected, doc.ToString());
    }

    [Fact]
    public void NestedAndArrayOperations()
    {
        var doc = JsoncDocument.Parse("{\n  \"nested\": {\"x\": true},\n  \"array\": [1, 2]\n}");
        doc.Set(new JsoncPath("nested", "x"), false);
        doc.Set(new JsoncPath("array", 0), 4);
        doc.Add(new JsoncPath("array", 2), 3);
        doc.Remove(new JsoncPath("array", 1));
        Assert.False(doc.Deserialize<JsonElement>().GetProperty("nested").GetProperty("x").GetBoolean());
        Assert.Equal(new[] { 4, 3 }, doc.Deserialize<JsonElement>().GetProperty("array").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Theory]
    [InlineData("{\n  // dedicated\n  \"a\": 1,\n  \"b\": 2\n}", "{\n  \"b\": 2\n}")]
    [InlineData("{\n  \"a\": 1, // inline\n  // dedicated\n  \"b\": 2\n}", "{\n  \"a\": 1 // inline\n}")]
    [InlineData("{\"a\":1,\"b\":2}", "{\"a\":1}")]
    public void DeletesMemberAndOwnedComments(string source, string expected)
    {
        var doc = JsoncDocument.Parse(source);
        doc.Remove(source.Contains("dedicated\n  \"a\"") ? "a" : "b");
        Assert.Equal(expected, doc.ToString());
    }

    [Fact]
    public void FailedEditDoesNotMutate()
    {
        var doc = JsoncDocument.Parse("{\"x\": 1}");
        Assert.Throws<KeyNotFoundException>(() => doc.Set("missing.child", 2));
        Assert.Equal("{\"x\": 1}", doc.ToString());
    }

    [Fact]
    public void EmptyContainersAndTrailingCommas()
    {
        var doc = JsoncDocument.Parse("{\n  \"empty\": {},\n  \"list\": [1,],\n}");
        doc.Add(new JsoncPath("empty", "child"), "日本語");
        doc.Add(new JsoncPath("list", 1), 2);
        Assert.Equal("{\n  \"empty\": {\"child\": \"\\u65E5\\u672C\\u8A9E\"},\n  \"list\": [1, 2,],\n}", doc.ToString());
        doc.Remove(new JsoncPath("empty", "child"));
        doc.Remove(new JsoncPath("list", 1));
        Assert.Equal("{\n  \"empty\": {},\n  \"list\": [1,],\n}", doc.ToString());
    }

    [Fact]
    public void InvalidJsonAndDuplicateNamesAreRejected()
    {
        Assert.ThrowsAny<JsonException>(() => JsoncDocument.Parse("{bad}"));
        Assert.Throws<JsonException>(() => JsoncDocument.Parse("{\"a\":1,\"a\":2}"));
    }

    [Fact]
    public void PreservesBlankLineSeparatedCommentOnDelete()
    {
        var doc = JsoncDocument.Parse("{\n  // general\n\n  \"a\": 1,\n  \"b\": 2\n}");
        doc.Remove("a");
        Assert.Equal("{\n  // general\n\n  \"b\": 2\n}", doc.ToString());
    }

    [Fact]
    public void DeletesMultipleLeadingCommentsIncludingMultilineBlock()
    {
        var doc = JsoncDocument.Parse("{\n  \"keep\": 1,\n  // one\n  /* two\n     more */\n  \"remove\": 2\n}");
        doc.Remove("remove");
        Assert.Equal("{\n  \"keep\": 1\n}", doc.ToString());
    }

    [Fact]
    public void ArrayFirstAndLastDeletionRetainTrailingComma()
    {
        var doc = JsoncDocument.Parse("[1, 2, 3,]");
        doc.Remove(new JsoncPath(0));
        doc.Remove(new JsoncPath(1));
        Assert.Equal("[2,]", doc.ToString());
    }

    [Fact]
    public void SerializerOptionsAndPocoSave()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonc");
        try
        {
            File.WriteAllText(path, "{\n  // level\n  \"level\": \"Warning\",\n  \"count\": 2\n}");
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            options.Converters.Add(new JsonStringEnumConverter());
            var file = JsoncFile<Config>.Load(path, options);
            file.Value.Count = 5;
            file.Save();
            Assert.Equal("{\n  // level\n  \"level\": \"Warning\",\n  \"count\": 5\n}", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PocoNoOpAndNestedChangesPreserveSource()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonc");
        try
        {
            const string source = "{\r\n  // stay\r\n  \"renamed\": 2,\r\n  \"nested\": {\"x\": 1},\r\n  \"items\": [1, 2,],\r\n  \"unknown\": true,\r\n}\r\n";
            File.WriteAllText(path, source);
            var file = JsoncFile<FullConfig>.Load(path, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            file.Save();
            Assert.Equal(source, File.ReadAllText(path));
            file.Value.Nested.X = 3;
            file.Value.Items.Add(4);
            file.Save();
            var text = File.ReadAllText(path);
            Assert.Contains("// stay\r\n", text);
            Assert.Contains("\"nested\": {\"x\": 3}", text);
            Assert.Contains("\"items\": [1, 2, 4,]", text);
            Assert.Contains("\"unknown\": true", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PocoCanAddOriginallyAbsentNestedObject()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonc");
        try
        {
            File.WriteAllText(path, "{\"renamed\":2,\"items\":[]}");
            var file = JsoncFile<FullConfig>.Load(path, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            file.Value.Nested.X = 7;
            file.Save();
            Assert.Equal(7, JsoncDocument.Load(path).Deserialize<JsonElement>().GetProperty("nested").GetProperty("x").GetInt32());
        }
        finally { File.Delete(path); }
    }

    private sealed class Config
    {
        public Level Level { get; set; }
        public int Count { get; set; }
    }
    private enum Level { Warning, Information }
    private sealed class FullConfig
    {
        [JsonPropertyName("renamed")]
        public int Number { get; set; }
        [JsonIgnore]
        public string IgnoreMe { get; set; } = "ignored";
        public NestedConfig Nested { get; set; } = new();
        public List<int> Items { get; set; } = [];
    }
    private sealed class NestedConfig { public int X { get; set; } }
}
