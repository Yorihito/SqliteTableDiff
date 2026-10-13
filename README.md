# SqliteTableDiff

テーブルを1つだけ持つ SQLite ファイル2つを比較し、差分があれば diff 風の出力を標準エラーに書き出すツールです。

## ビルド / 実行

```bash
dotnet build -c Release
dotnet run -c Release -- a.db b.db
```

## 使い方

```
SqliteTableDiff <fileA.db> <fileB.db> [options]

  --max-lines N      差分出力の行数上限 (default: 100)
  --key col1,col2    行を突き合わせるキー列 (default: PRIMARY KEY、無ければ全列)
  --quiet            差分の詳細を出さず、要約のみ出力する
```

終了コード: `0` = 同一, `1` = 差分あり, `2` = エラー (テーブルが0個/複数、ファイルなし等)

## 出力例

```
--- a.db (table users, 4 rows)
+++ b.db (table users, 4 rows)
key: id
- id=2, name='Bob', score=2, data=X'0102'
+ id=2, name='Bob', score=2, data=X'0103'
- id=3, name='Carol', score=3, data=NULL
+ id=4, name='Dave', score=4, data=NULL
4 differences
```

- `-` : A にだけある行 / A 側の値、`+` : B にだけある行 / B 側の値、`!` : スキーマ(列・型・テーブル名)の差
- PRIMARY KEY があればキーで行を突き合わせ、同じキーで値が違う行は `-`/`+` のペアで出します
- PRIMARY KEY が無ければ全列をキーにし、行全体の多重集合として比較します(重複行の個数差も検出)
- 値の比較は SQLite の意味に従います (`3` と `3.0` は同じ、NULL 同士は同じ)
- 両テーブルをキー順にソートして読み出しながらマージするため、メモリは行数に依存しません

## 配布用バイナリ (Releases)

[Releases](https://github.com/Yorihito/SqliteTableDiff/releases) に .NET ランタイム不要の実行ファイルを置いています。
ZIP を展開し、同梱の SQLite ライブラリと同じフォルダに置いたまま実行してください。

| ZIP | 対象 | 中身 |
|---|---|---|
| `SqliteTableDiff-win-x64.zip` | Windows 64bit | `SqliteTableDiff.exe`, `e_sqlite3.dll` |
| `SqliteTableDiff-osx-arm64.zip` | macOS (Apple Silicon) | `SqliteTableDiff`, `libe_sqlite3.dylib` |

```
SqliteTableDiff.exe a.db b.db
```

自分でビルドする場合:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o dist/win-x64
```

`-r` を `osx-arm64` / `linux-x64` などに変えると他の OS 向けになります。
