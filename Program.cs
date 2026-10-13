using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SqliteTableDiff;

/// <summary>
/// 2つの SQLite ファイルにそれぞれ1つだけ含まれるテーブルを比較し、
/// 差分があれば diff 風の出力を標準エラーに書き出す。
///
/// 終了コード: 0 = 同一, 1 = 差分あり, 2 = エラー(引数不正、テーブル数不正など)
/// </summary>
internal static class Program
{
    internal const int DefaultMaxLines = 100;

    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options is null)
            {
                PrintUsage();
                return 2;
            }

            using var a = new TableSource(options.FileA);
            using var b = new TableSource(options.FileB);

            var differ = new TableDiffer(a, b, options);
            int differences = differ.Run();

            return differences == 0 ? 0 : 1;
        }
        catch (DiffException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"sqlite error: {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            usage: SqliteTableDiff <fileA.db> <fileB.db> [options]

            options:
              --max-lines N      差分出力の行数上限 (default: 100)
              --key col1,col2    行を突き合わせるキー列 (default: PRIMARY KEY、無ければ全列)
              --quiet            差分の詳細を出さず、要約のみ出力する
            """);
    }
}

internal sealed class DiffException(string message) : Exception(message);

internal sealed record Options(string FileA, string FileB, int MaxLines, string[]? KeyColumns, bool Quiet)
{
    public static Options? Parse(string[] args)
    {
        var positional = new List<string>();
        int maxLines = Program.DefaultMaxLines;
        string[]? key = null;
        bool quiet = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--max-lines":
                    if (++i >= args.Length || !int.TryParse(args[i], out maxLines) || maxLines < 0)
                        throw new DiffException("--max-lines には 0 以上の整数を指定してください");
                    break;
                case "--key":
                    if (++i >= args.Length)
                        throw new DiffException("--key には列名をカンマ区切りで指定してください");
                    key = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "-h":
                case "--help":
                    return null;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new DiffException($"不明なオプション: {args[i]}");
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count != 2)
            return null;

        foreach (var path in positional)
        {
            if (!File.Exists(path))
                throw new DiffException($"ファイルが見つかりません: {path}");
        }

        return new Options(positional[0], positional[1], maxLines, key, quiet);
    }
}

internal sealed record ColumnInfo(string Name, string Type, int PkPosition);

/// <summary>SQLite ファイルを読み取り専用で開き、唯一のテーブルの情報を提供する。</summary>
internal sealed class TableSource : IDisposable
{
    public string Path { get; }
    public SqliteConnection Connection { get; }
    public string TableName { get; }
    public IReadOnlyList<ColumnInfo> Columns { get; }

    public TableSource(string path)
    {
        Path = path;
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        Connection.Open();

        TableName = FindSingleTable();
        Columns = LoadColumns();
    }

    private string FindSingleTable()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name";
        var names = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                names.Add(reader.GetString(0));
        }

        return names.Count switch
        {
            0 => throw new DiffException($"{Path} にテーブルがありません"),
            1 => names[0],
            _ => throw new DiffException($"{Path} にテーブルが複数あります: {string.Join(", ", names)}"),
        };
    }

    private List<ColumnInfo> LoadColumns()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Sql.Quote(TableName)})";
        var columns = new List<ColumnInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new ColumnInfo(
                Name: reader.GetString(reader.GetOrdinal("name")),
                Type: reader.GetString(reader.GetOrdinal("type")),
                PkPosition: reader.GetInt32(reader.GetOrdinal("pk"))));
        }
        return columns;
    }

    public long CountRows()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Sql.Quote(TableName)}";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// 指定列を、キー列→残りの列の順で BINARY 照合順にソートして読み出す。
    /// C# 側の比較器 (<see cref="SqliteValue"/>) と並び順を一致させるため、COLLATE BINARY を明示する。
    /// </summary>
    public SqliteDataReader OpenSortedReader(IReadOnlyList<string> selectColumns, IReadOnlyList<string> orderColumns)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {string.Join(", ", selectColumns.Select(Sql.Quote))} " +
            $"FROM {Sql.Quote(TableName)} " +
            $"ORDER BY {string.Join(", ", orderColumns.Select(c => Sql.Quote(c) + " COLLATE BINARY"))}";
        return cmd.ExecuteReader(System.Data.CommandBehavior.SequentialAccess);
    }

    public void Dispose() => Connection.Dispose();
}

internal static class Sql
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// SQLite の BINARY 照合順 (NULL &lt; 数値 &lt; TEXT &lt; BLOB) と同じ順序で値を比較する。
/// ソート済みの2本の読み出しをマージする際、SQLite 側の ORDER BY と矛盾しない必要がある。
/// </summary>
internal static class SqliteValue
{
    public static int Compare(object x, object y)
    {
        int rx = Rank(x), ry = Rank(y);
        if (rx != ry) return rx.CompareTo(ry);

        switch (rx)
        {
            case 0:
                return 0;
            case 1:
                return CompareNumeric(x, y);
            case 2:
                return Encoding.UTF8.GetBytes((string)x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes((string)y));
            default:
                return ((byte[])x).AsSpan().SequenceCompareTo((byte[])y);
        }
    }

    public static bool Equal(object x, object y) => Compare(x, y) == 0;

    private static int Rank(object v) => v switch
    {
        null or DBNull => 0,
        long or double => 1,
        string => 2,
        byte[] => 3,
        _ => throw new DiffException($"想定外の値の型です: {v.GetType()}"),
    };

    private static int CompareNumeric(object x, object y)
    {
        if (x is long lx && y is long ly) return lx.CompareTo(ly);
        if (x is double dx && y is double dy) return dx.CompareTo(dy);
        if (x is long l) return CompareLongToDouble(l, (double)y);
        return -CompareLongToDouble((long)y, (double)x);
    }

    // long と double を精度を落とさずに比較する (SQLite の sqlite3IntFloatCompare 相当)。
    private static int CompareLongToDouble(long l, double d)
    {
        if (d >= 9223372036854775808.0) return -1;
        if (d < -9223372036854775808.0) return 1;
        long floor = (long)Math.Floor(d);
        if (l < floor) return -1;
        if (l > floor) return 1;
        return d > floor ? -1 : 0;
    }

    public static string Format(object v, int maxLength = 200)
    {
        string s = v switch
        {
            null or DBNull => "NULL",
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            string str => "'" + EscapeText(str) + "'",
            byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
            _ => v.ToString() ?? "",
        };
        return s.Length > maxLength ? s[..maxLength] + "..." : s;
    }

    private static string EscapeText(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\'': sb.Append("''"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (char.IsControl(c)) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// 両テーブルをキー順にソートして読み出し、マージしながら差分を検出する。
/// 出力は標準エラーに diff 風 ("-" = A のみ, "+" = B のみ, "!" = スキーマ差) で書き出す。
/// </summary>
internal sealed class TableDiffer
{
    private readonly TableSource _a;
    private readonly TableSource _b;
    private readonly Options _options;
    private readonly TextWriter _err = Console.Error;

    private int _differences;
    private int _linesWritten;

    public TableDiffer(TableSource a, TableSource b, Options options)
    {
        _a = a;
        _b = b;
        _options = options;
    }

    /// <summary>差分の総数を返す (スキーマ差 1件 = 1、行差分 1行 = 1)。</summary>
    public int Run()
    {
        var common = DiffSchema();
        if (common.Count == 0)
        {
            WriteLine("! 共通の列がないため、行の比較は行いません");
            WriteSummary();
            return _differences;
        }

        var key = ResolveKeyColumns(common);
        DiffRows(common, key);
        WriteSummary();
        return _differences;
    }

    private List<string> DiffSchema()
    {
        if (_a.TableName != _b.TableName)
            WriteDiff($"! テーブル名が異なります: {_a.TableName} / {_b.TableName}");

        var byNameB = _b.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var byNameA = _a.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var common = new List<string>();

        foreach (var ca in _a.Columns)
        {
            if (!byNameB.TryGetValue(ca.Name, out var cb))
            {
                WriteDiff($"! 列 {ca.Name} {ca.Type} は {_a.Path} にのみ存在します");
                continue;
            }
            if (!string.Equals(ca.Type, cb.Type, StringComparison.OrdinalIgnoreCase))
                WriteDiff($"! 列 {ca.Name} の型が異なります: {ca.Type} / {cb.Type}");
            common.Add(ca.Name);
        }
        foreach (var cb in _b.Columns)
        {
            if (!byNameA.ContainsKey(cb.Name))
                WriteDiff($"! 列 {cb.Name} {cb.Type} は {_b.Path} にのみ存在します");
        }
        return common;
    }

    private List<string> ResolveKeyColumns(List<string> common)
    {
        if (_options.KeyColumns is { Length: > 0 } requested)
        {
            foreach (var k in requested)
            {
                if (!common.Contains(k, StringComparer.OrdinalIgnoreCase))
                    throw new DiffException($"--key で指定した列 {k} が両方のテーブルに存在しません");
            }
            return requested.ToList();
        }

        var pk = _a.Columns.Where(c => c.PkPosition > 0).OrderBy(c => c.PkPosition).Select(c => c.Name).ToList();
        if (pk.Count > 0 && pk.All(k => common.Contains(k, StringComparer.OrdinalIgnoreCase)))
            return pk;

        // PRIMARY KEY が無い場合は全列をキーにする (= 行全体の多重集合として比較)
        return common;
    }

    private void DiffRows(List<string> columns, List<string> key)
    {
        // ORDER BY はキー列が先、残りの列が後。これで同一キーの重複行も決定的な順序になる。
        var order = key.Concat(columns.Where(c => !key.Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
        int[] keyIndexes = key.Select(k => columns.FindIndex(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool keyIsWholeRow = key.Count == columns.Count;

        long countA = _a.CountRows(), countB = _b.CountRows();
        WriteLine($"--- {_a.Path} (table {_a.TableName}, {countA} rows)");
        WriteLine($"+++ {_b.Path} (table {_b.TableName}, {countB} rows)");
        WriteLine($"key: {string.Join(", ", key)}");

        using var ra = _a.OpenSortedReader(columns, order);
        using var rb = _b.OpenSortedReader(columns, order);

        object[]? rowA = Next(ra, columns.Count);
        object[]? rowB = Next(rb, columns.Count);

        while (rowA is not null || rowB is not null)
        {
            int c;
            if (rowA is null) c = 1;
            else if (rowB is null) c = -1;
            else c = CompareRows(rowA, rowB, keyIndexes);

            if (c < 0)
            {
                WriteDiff("- " + FormatRow(columns, rowA!));
                rowA = Next(ra, columns.Count);
            }
            else if (c > 0)
            {
                WriteDiff("+ " + FormatRow(columns, rowB!));
                rowB = Next(rb, columns.Count);
            }
            else
            {
                if (!keyIsWholeRow && !RowsEqual(rowA!, rowB!))
                {
                    WriteDiff("- " + FormatRow(columns, rowA!));
                    WriteDiff("+ " + FormatRow(columns, rowB!));
                }
                rowA = Next(ra, columns.Count);
                rowB = Next(rb, columns.Count);
            }
        }
    }

    private static object[]? Next(SqliteDataReader reader, int columnCount)
    {
        if (!reader.Read()) return null;
        var values = new object[columnCount];
        reader.GetValues(values);
        return values;
    }

    private static int CompareRows(object[] a, object[] b, int[] keyIndexes)
    {
        foreach (int i in keyIndexes)
        {
            int c = SqliteValue.Compare(a[i], b[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static bool RowsEqual(object[] a, object[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (!SqliteValue.Equal(a[i], b[i])) return false;
        }
        return true;
    }

    private static string FormatRow(List<string> columns, object[] row)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(columns[i]).Append('=').Append(SqliteValue.Format(row[i]));
        }
        return sb.ToString();
    }

    private void WriteDiff(string line)
    {
        _differences++;
        if (_options.Quiet || _linesWritten >= _options.MaxLines) return;
        _err.WriteLine(line);
        _linesWritten++;
    }

    private void WriteLine(string line)
    {
        if (_options.Quiet) return;
        _err.WriteLine(line);
    }

    private void WriteSummary()
    {
        if (_differences == 0)
        {
            if (!_options.Quiet) _err.WriteLine("identical");
            return;
        }

        int hidden = _differences - _linesWritten;
        if (_options.Quiet)
            _err.WriteLine($"{_differences} differences");
        else if (hidden > 0)
            _err.WriteLine($"... {hidden} more differences not shown ({_differences} total, limit {_options.MaxLines} lines)");
        else
            _err.WriteLine($"{_differences} differences");
    }
}
