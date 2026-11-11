using System.Text;
using System.Globalization;

namespace Drilling.File.Parser;
internal static class CCsvParser
{
    private const int Utf8BomLength = 3;
    private static readonly Encoding CsvWriteEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding StrictUtf8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding> KoreanCsvEncoding = new(GetKoreanCsvEncoding);

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Read(string path)
        => Read(path, null);

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Read(string path, IList<int>? rowNumbers)
    {
        if (!System.IO.File.Exists(path))
        {
            return[];
        }

        var lines = ReadLines(path);
        if (lines.Count == 0)
        {
            return[];
        }

        string SelectItem(string header)
        {
            return header.Trim();
        }

        var headers = ParseLine(lines[0], path, 1).Select(SelectItem).ToArray();
        ValidateHeaders(path, headers);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (var lineIndex = 1; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseLine(line, path, lineIndex + 1);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Length; index++)
            {
                row[headers[index]] = index < fields.Count ? fields[index] : string.Empty;
            }

            rows.Add(row);
            rowNumbers?.Add(lineIndex + 1);
        }

        return rows;
    }

    public static void Write(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        var rowList = rows.ToArray();
        global::System.Collections.Generic.IEnumerable<string> SelectKeys(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return row.Keys;
        }

        bool ShouldIncludeItem(string key)
        {
            return !headers.Contains(key, StringComparer.OrdinalIgnoreCase);
        }

        var outputHeaders = headers.Concat(rowList.SelectMany(SelectKeys).Where(ShouldIncludeItem)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new List<string>
        {
            string.Join(",", outputHeaders.Select(Escape))
        };
        string SelectItem(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            string SelectItem(string header)
            {
                return Escape(Get(row, header));
            }

            return string.Join(",", outputHeaders.Select(SelectItem));
        }

        lines.AddRange(rowList.Select(SelectItem));
        WriteLines(path, lines);
    }

    public static void Append(
        string path,
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, string> row)
    {
        if (!System.IO.File.Exists(path) || new FileInfo(path).Length == 0)
        {
            Write(path, headers, [row]);
            return;
        }

        var unexpected = row.Keys.FirstOrDefault(key =>
            !headers.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unexpected is not null)
        {
            throw new InvalidDataException($"CSV append failed. Unexpected column: {unexpected}");
        }

        var line = string.Join(",", headers.Select(header => Escape(Get(row, header))));
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        var originalLength = stream.Length;
        stream.Position = originalLength;
        try
        {
            var prefix = originalLength > 0 && ReadLastByte(stream) != (byte)'\n'
                ? Environment.NewLine
                : "";
            stream.Position = originalLength;
            var bytes = CsvWriteEncoding.GetBytes(prefix + line + Environment.NewLine);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            stream.SetLength(originalLength);
            stream.Flush(flushToDisk: true);
            throw;
        }
    }

    private static byte ReadLastByte(FileStream stream)
    {
        stream.Position = stream.Length - 1;
        var value = stream.ReadByte();
        return value < 0 ? (byte)0 : (byte)value;
    }

    public static IReadOnlyList<string> ReadLines(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return[];
        }

        return SplitLines(ReadCsvText(path));
    }

    public static void WriteLines(string path, IEnumerable<string> lines)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllLines(path, lines, CsvWriteEncoding);
    }

    public static string Get(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value : string.Empty;
    }

    public static string GetFirst(IReadOnlyDictionary<string, string> row, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Get(row, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    public static string RequireText(IReadOnlyDictionary<string, string> row, string tableName, int rowNo, params string[] names)
    {
        var value = GetFirst(row, names);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{tableName} validation failed. Row {rowNo} / {names[0]} cannot be empty.");
        }

        return value;
    }

    public static int ReadInt(string value, string tableName, int rowNo, string fieldName, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new InvalidDataException($"{tableName} validation failed. Row {rowNo} / {fieldName} must be integer: {value}");
    }

    public static int ReadRequiredInt(string value, string tableName, int rowNo, string fieldName, bool allowNegative = false)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || (!allowNegative && result < 0))
        {
            var signText = allowNegative ? "" : " non-negative";
            throw new InvalidDataException($"{tableName} validation failed. Row {rowNo} / {fieldName} must be a{signText} integer.");
        }

        return result;
    }

    public static double ReadDouble(string value, string tableName, int rowNo, string fieldName, double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : throw new InvalidDataException($"{tableName} validation failed. Row {rowNo} / {fieldName} must be number: {value}");
    }

    public static bool ReadBool(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return NormalizeHeader(value) switch
        {
            "1" or "TRUE" or "ON" or "YES" or "USE" or "Y" => true,
            "0" or "FALSE" or "OFF" or "NO" or "NOTUSE" or "N" => false,
            _ => defaultValue
        };
    }

    public static bool ReadRequiredBool(string value, string tableName, int rowNo, string fieldName)
    {
        return NormalizeHeader(value) switch
        {
            "1" or "TRUE" or "ON" or "YES" or "USE" or "Y" or "SIMUL" or "SIMULATION" or "SIM" => true,
            "0" or "FALSE" or "OFF" or "NO" or "NOTUSE" or "N" or "ONLINE" or "LIVE" or "REAL" => false,
            _ => throw new InvalidDataException($"{tableName} validation failed. Row {rowNo} / {fieldName} must be 1/0 or ON/OFF.")};
    }

    public static IReadOnlyList<string> ReadHeaders(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return[];
        }

        var firstLine = ReadLines(path).FirstOrDefault();
        string SelectItem(string header)
        {
            return header.Trim();
        }

        bool ShouldIncludeItem(string header)
        {
            return !string.IsNullOrWhiteSpace(header);
        }

        var headers = string.IsNullOrWhiteSpace(firstLine) ? [] : ParseLine(firstLine, path, 1).Select(SelectItem).Where(ShouldIncludeItem).ToArray();
        if (headers.Length > 0)
        {
            ValidateHeaders(path, headers);
        }

        return headers;
    }

    public static void ValidateRequiredHeaders(string path, string tableName, IEnumerable<IEnumerable<string>> requiredHeaderGroups)
    {
        if (!System.IO.File.Exists(path))
        {
            throw new FileNotFoundException($"{tableName} file not found.", path);
        }

        var headers = ReadHeaders(path);
        if (headers.Count == 0)
        {
            throw new InvalidDataException($"{tableName} validation failed. Header row is empty.");
        }

        var headerSet = headers.Select(NormalizeHeader).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ShouldIncludeItem(string header)
        {
            return !string.IsNullOrWhiteSpace(header);
        }

        string SelectItem(string header)
        {
            return header.Trim();
        }

        string[] SelectValue(global::System.Collections.Generic.IEnumerable<string> group)
        {
            return group.Where(ShouldIncludeItem).Select(SelectItem).ToArray();
        }

        bool MatchesItem(string header)
        {
            return headerSet.Contains(NormalizeHeader(header));
        }

        bool MatchesLength(string[] group)
        {
            return group.Length > 0 && !group.Any(MatchesItem);
        }

        string SelectString(string[] group)
        {
            return string.Join(" or ", group);
        }

        var missing = requiredHeaderGroups.Select(SelectValue).Where(MatchesLength).Select(SelectString).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"{tableName} validation failed. Missing column(s): {string.Join(", ", missing)}. " + $"Available: {string.Join(", ", headers)}");
        }
    }

    public static IReadOnlyDictionary<string, string> GetExtra(IReadOnlyDictionary<string, string> row, IEnumerable<string> knownHeaders)
    {
        var known = new HashSet<string>(knownHeaders, StringComparer.OrdinalIgnoreCase);
        bool ShouldIncludeItem(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return !known.Contains(item.Key);
        }

        string SelectKey(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return item.Key;
        }

        string SelectValue(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return item.Value;
        }

        return row.Where(ShouldIncludeItem).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ParseLine(string line, string path, int lineNo)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (current == ',' && !inQuotes)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(current);
            }
        }

        fields.Add(value.ToString());
        if (inQuotes)
        {
            throw new InvalidDataException($"CSV validation failed. {Path.GetFileName(path)} row {lineNo} has an unterminated quoted value.");
        }

        return fields;
    }

    private static void ValidateHeaders(string path, IReadOnlyList<string> headers)
    {
        var emptyIndexes = headers.Select((header, index) => new { Header = header, Index = index + 1 }).Where(item => string.IsNullOrWhiteSpace(item.Header)).Select(item => item.Index).ToArray();
        if (emptyIndexes.Length > 0)
        {
            throw new InvalidDataException($"CSV validation failed. {Path.GetFileName(path)} header column cannot be empty. Column(s): {string.Join(", ", emptyIndexes)}");
        }

        bool ShouldIncludeItem(global::System.Linq.IGrouping<string, string> group)
        {
            return group.Count() > 1;
        }

        string SelectItem(global::System.Linq.IGrouping<string, string> group)
        {
            return group.First();
        }

        var duplicatedHeaders = headers.GroupBy(NormalizeHeader, StringComparer.OrdinalIgnoreCase).Where(ShouldIncludeItem).Select(SelectItem).ToArray();
        if (duplicatedHeaders.Length > 0)
        {
            throw new InvalidDataException($"CSV validation failed. {Path.GetFileName(path)} has duplicated header(s): {string.Join(", ", duplicatedHeaders)}");
        }
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string NormalizeHeader(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static string ReadCsvText(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            return "";
        }

        if (HasUtf8Bom(bytes))
        {
            return StrictUtf8Encoding.GetString(bytes, Utf8BomLength, bytes.Length - Utf8BomLength);
        }

        try
        {
            return StrictUtf8Encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return KoreanCsvEncoding.Value.GetString(bytes);
        }
    }

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes)
    {
        return bytes.Count >= Utf8BomLength && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static string[] SplitLines(string text)
    {
        using var reader = new StringReader(text);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private static Encoding GetKoreanCsvEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949);
    }
}
