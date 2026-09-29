# JsoncPreserve

JsoncPreserve は、コメント付き JSON（JSONC）のコメントや書式を保ちながら編集する .NET ライブラリです。JSON の構文検証、値のシリアライズ、POCO 変換には `System.Text.Json` を使います。Node.js、Newtonsoft.Json には依存しません。

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

**0.1.0** は [NuGet.org](https://www.nuget.org/packages/JsoncPreserve/0.1.0) で公開しています。Visual Studio の NuGet マネージャから導入する場合:

1. ソリューション エクスプローラーで利用先のプロジェクトを右クリックし、**NuGet パッケージの管理**を開きます。
2. パッケージ ソースに **nuget.org** を選び、**参照**タブを開きます。
3. **JsoncPreserve** を検索し、バージョン **0.1.0** を選んで **インストール**を押します。ライセンスの確認が表示された場合は内容を確認してください。
4. プロジェクトの **依存関係 > パッケージ** に **JsoncPreserve** が表示されることを確認します。

対応する対象フレームワークは .NET 8 と .NET 10 です。ライブラリの型を直接使うプロジェクトごとに追加してください。コマンドラインでは、利用先プロジェクトのディレクトリで次を実行します。

```sh
dotnet add package JsoncPreserve --version 0.1.0
```

通常の導入ではパッケージ ソースに **nuget.org** を選んでください。GitHub Releases からはパッケージをダウンロードできます。GitHub Packages は別のフィードで、利用には GitHub の認証が必要です。

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
file.Save(); // 一時ファイルを書いてから server.jsonc を置き換える

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

### 保存前の確認・独自保存処理・一時ファイル経由の保存

```csharp
var file = JsoncFile<ServerConfig>.Load("server.jsonc", options);
file.Value.WarmPoolSize = 5;

string preview = file.ToJsoncString(); // 保存せず変更後の JSONC を取得
byte[] bytes = file.ToUtf8Bytes();      // 独自の保存処理に渡せる UTF-8

file.SaveAtomic(); // Save() と同じ保存方式を明示
```

二つのプレビュー API はファイルにも内部の差分基準にも変更を加えません。`SaveAtomic()` の代わりに既存の保存関数を使う場合は次のように書けます。

```csharp
file.SaveWith(WriteAtomic); // アプリ側の関数: void WriteAtomic(string path, byte[] bytes)
```

この関数は渡されたバイト列をそのまま保存し、失敗時には例外を投げる必要があります。ライブラリは正常終了後だけ差分基準を更新します。プレビューのバイト列を別途保存した場合は、次の編集前にファイルを読み直してください。

`Save()` と `SaveAtomic()` は保存先と同じディレクトリに一時ファイルを書き、書き込みが終わってから置き換えます。`JsoncDocument.Save(path)` と `JsoncDocument.SaveAtomic(path)` も同じ方式です。Unix では既存ファイルのモードを引き継ぎますが、マシン停止時の永続性は保証しません。

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
