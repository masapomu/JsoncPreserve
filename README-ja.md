# JsoncPreserve

JsoncPreserve は、コメント付き JSON（JSONC）のコメントや書式を保ちながら編集する .NET ライブラリです。JSON の構文検証、値のシリアライズ、POCO 変換には `System.Text.Json` を使います。Glasswalk、Node.js、Newtonsoft.Json には依存しません。

## 必要な理由

`JsonSerializer` はコメントと末尾カンマを読み込めますが、POCO を再シリアライズするとコメントと書式が失われます。本ライブラリは元の UTF-8 バイト列を保持し、変更した箇所だけを書き換えます。

```jsonc
{
  // 起動済みコンテナ数
  "warmPoolSize": 3,
  "logLevel": "Warning" // 本番環境の既定値
}
```

`warmPoolSize` を `5` に変更した場合、元の `3` の部分だけが変わります。

## インストール

**NuGet.org にはまだ公開していません。** 公開後のコマンドは次の予定です。

```sh
dotnet add package JsoncPreserve
```

現時点では `src/JsoncPreserve/JsoncPreserve.csproj` をプロジェクト参照するか、`dotnet pack -c Release` でローカルパッケージを作成してください。

## 基本的な使い方

```csharp
using JsoncPreserve;

var document = JsoncDocument.Load("server.jsonc");
document.Set("warmPoolSize", 5);
document.Set(new JsoncPath("logging", "level"), "Information");
document.Add(new JsoncPath("servers", 2), "https://example.org"); // Count 位置へ追加
document.Remove("obsoleteSetting");
document.Save("server.jsonc");
```

文字列パスは `.` 区切りのプロパティ名です。配列添字や `.` を含むプロパティ名には `JsoncPath` を使用します。`Set` は既存値を変更し、末尾のプロパティがなければ追加します。配列では `Count` 位置に追加します。`Add` は新規プロパティまたは配列末尾への追加、`Remove` は削除です。

## 既存の `System.Text.Json` コードからの置き換え

上の JSONC を `server.jsonc` に保存したとします。次の二つのプログラムは、同じ POCO とシリアライザー設定を使って `warmPoolSize` を `3` から `5` に変更します。

**変更前 — `System.Text.Json` のみ（`Program.cs`）:**

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

読み込みはできますが、保存時にコメントが消え、ファイル全体の書式が作り直されます。

**変更後 — `JsoncPreserve`（`Program.cs`）:**

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
file.Save();

public sealed class ServerConfig
{
    public int WarmPoolSize { get; set; }
    public string LogLevel { get; set; } = "";
}
```

読み込み・値の変更・保存の3行を置き換えるだけで、POCO とシリアライザー設定はそのまま使えます。保存後も元の書式が残ります。

```jsonc
{
  // 起動済みコンテナ数
  "warmPoolSize": 5,
  "logLevel": "Warning" // 本番環境の既定値
}
```

`JsoncFile<T>` は編集前後の POCO をシリアライズして差分を元文書へ適用します。`JsonPropertyName`、`JsonIgnore`、コンバーター、命名規則、enum、nullable、コレクション、入れ子オブジェクトは `System.Text.Json` に委ねます。元ファイルにだけ存在する未知の項目は保持します。構造を厳密に制御する場合は `JsoncDocument` を使ってください。

## ラウンドトリップ保証

- 無変更時は UTF-8 バイト列が完全一致します。BOM、コメント、改行、空白、タブ、末尾カンマを含みます。
- 既存値の変更では、その値の範囲以外のバイトは変わりません。
- 追加・削除では対象項目と必要な区切りだけを変更します。近くのインデント、改行、末尾カンマから書式を推定します。
- 項目直前のコメント行は空行で区切られていなければ、その項目に属します。行末コメントもその項目に属し、項目削除時に削除されます。

## 制限

UTF-8 専用です。重複するオブジェクトキーはパスが曖昧になるため拒否します。文字列パスは JSONPath ではありません。追加値の書式と文字列エスケープには `JsonSerializer` の設定が適用されます。複雑な同行コメントの整列は再現しない場合があります。オブジェクトや配列全体を置換すると、その内部のコメントも置換されます。POCO 差分は通常のオブジェクトグラフ向けで、形を変えるコンバーターや配列の並べ替えでは編集範囲が大きくなる場合があります。詳しくは [設計](docs/DESIGN.md) と [保持規則](docs/ROUNDTRIP.md) を参照してください。

## ビルドとテスト

```sh
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack src/JsoncPreserve/JsoncPreserve.csproj -c Release
```

ライブラリは .NET 8 / .NET 10 対応で、外部パッケージ依存はありません。テストプロジェクトは xUnit を使用します。

## ライセンス

MIT。[LICENSE](LICENSE) を参照してください。実装は独自の .NET コードであり、`node-jsonc-parser` のコードは複製していません。
