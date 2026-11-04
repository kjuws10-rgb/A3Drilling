using System.Globalization;
using Drilling.Common.Review;
using Drilling.Common.Recipe;
using Drilling.File.Parser;

namespace Drilling.File.ReviewOffset;

public sealed class CReviewOffsetFile(string configRoot) : CReviewOffsetFileBase
{
    private static readonly IReadOnlyList<string> Headers =
    [
        "CELLID",
        "SHOTID",
        "REVIEWOFFSETX",
        "REVIEWOFFSETY",
        "COORDINATE_SPACE"
    ];

    private static readonly IReadOnlyList<IReadOnlyList<string>> RequiredHeaderGroups =
    [
        ["CELLID"],
        ["SHOTID"],
        ["REVIEWOFFSETX"],
        ["REVIEWOFFSETY"]
    ];

    private readonly string _recipeRoot = Path.Combine(configRoot, "RECIPE");

    public override string RootPath => _recipeRoot;

    public override Task<ST_REVIEW_OFFSET_FILE_DATA> LoadLatest(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRecipeId = NormalizeRecipeId(recipeId);
        if (string.IsNullOrWhiteSpace(normalizedRecipeId))
        {
            return Task.FromResult(ST_REVIEW_OFFSET_FILE_DATA.Empty(""));
        }

        var directory = GetRecipeOffsetDirectory(normalizedRecipeId);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(ST_REVIEW_OFFSET_FILE_DATA.Empty(normalizedRecipeId));
        }

        var latestFile = Directory
            .EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(latestFile))
        {
            return Task.FromResult(ST_REVIEW_OFFSET_FILE_DATA.Empty(normalizedRecipeId));
        }

        return Task.FromResult(Load(normalizedRecipeId, latestFile, cancellationToken));
    }

    public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(
        string recipeId,
        IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRecipeId = NormalizeRecipeId(recipeId);
        if (string.IsNullOrWhiteSpace(normalizedRecipeId))
        {
            throw new InvalidDataException("Review Offset save failed. Recipe ID is empty.");
        }

        var normalizedRows = CReviewOffsetData.ValidateRows(rows,
            $"{normalizedRecipeId} (저장할 Review Offset)", token: cancellationToken);
        var savedAt = DateTimeOffset.Now;
        var directory = GetRecipeOffsetDirectory(normalizedRecipeId);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        string path;
        try
        {
            CCsvParser.Write(temporary, Headers, normalizedRows.OrderBy(row => row.CellId)
                .ThenBy(row => row.ShotId, StringComparer.OrdinalIgnoreCase).Select(ToCsvRow));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                path = Path.Combine(directory, $"{savedAt:yyyyMMdd_HHmmss_fff}_reviewoffset.csv");
                try { System.IO.File.Move(temporary, path, overwrite: false); break; }
                catch (IOException) when (System.IO.File.Exists(path)) { savedAt = savedAt.AddMilliseconds(1); }
            }
        }
        finally
        {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }

        return Task.FromResult(new ST_REVIEW_OFFSET_FILE_DATA(
            normalizedRecipeId,
            Path.GetFullPath(path),
            Path.GetFileName(path),
            savedAt,
            normalizedRows));
    }

    private ST_REVIEW_OFFSET_FILE_DATA Load(string recipeId, string path, CancellationToken token)
    {
        try
        {
            CCsvParser.ValidateRequiredHeaders(
                path,
                "Review Offset",
                RequiredHeaderGroups);

            var lineNumbers = new List<int>();
            var sourceRows = CCsvParser.Read(path, lineNumbers);
            var parsed = new ST_REVIEW_OFFSET_ROW[sourceRows.Count];
            for (var index = 0; index < sourceRows.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                try { parsed[index] = ToOffsetRow(sourceRows[index], lineNumbers[index]); }
                catch (InvalidDataException error)
                {
                    throw new CReviewOffsetValidationException(path, lineNumbers[index], error.Message);
                }
            }
            var rows = CReviewOffsetData.ValidateRows(parsed, path, lineNumbers, token);
            var fileInfo = new FileInfo(path);
            var savedAt = TryParseSavedAt(fileInfo.Name) ?? fileInfo.LastWriteTime;
            return new ST_REVIEW_OFFSET_FILE_DATA(
                recipeId,
                fileInfo.FullName,
                fileInfo.Name,
                savedAt,
                rows);
        }
        catch (InvalidDataException error) { throw new CReviewOffsetValidationException(path, 0, error.Message); }
    }

    private string GetRecipeOffsetDirectory(string recipeId)
    {
        return Path.Combine(_recipeRoot, $"{SanitizeFileName(recipeId)}_reviewoffset");
    }

    private static ST_REVIEW_OFFSET_ROW ToOffsetRow(
        IReadOnlyDictionary<string, string> row,
        int rowNo)
    {
        var cellId = CCsvParser.ReadRequiredInt(
            CCsvParser.Get(row, "CELLID"),
            "Review Offset",
            rowNo,
            "CELLID");
        var shotId = CCsvParser.RequireText(
            row,
            "Review Offset",
            rowNo,
            "SHOTID").Trim().ToUpperInvariant();
        double Number(string name) => double.TryParse(CCsvParser.Get(row, name), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value
            : throw new InvalidDataException($"{name} 값이 숫자가 아닙니다: '{CCsvParser.Get(row, name)}'.");
        var offsetX = Number("REVIEWOFFSETX");
        var offsetY = Number("REVIEWOFFSETY");
        var space = CCsvParser.Get(row, "COORDINATE_SPACE").Trim().ToUpperInvariant();
        if (space is not ("" or "SCANNER" or "DESIGN"))
            throw new InvalidDataException($"Review Offset row {rowNo}: invalid COORDINATE_SPACE {space}.");
        return new ST_REVIEW_OFFSET_ROW(cellId, shotId, offsetX, offsetY) { IsDesignCoordinate = space == "DESIGN" };
    }

    private static IReadOnlyDictionary<string, string> ToCsvRow(ST_REVIEW_OFFSET_ROW row)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CELLID"] = row.CellId.ToString(CultureInfo.InvariantCulture),
            ["SHOTID"] = row.ShotId,
            ["REVIEWOFFSETX"] = FormatDouble(row.ReviewOffsetX),
            ["REVIEWOFFSETY"] = FormatDouble(row.ReviewOffsetY),
            ["COORDINATE_SPACE"] = row.IsDesignCoordinate ? "DESIGN" : "SCANNER"
        };
    }

    private static DateTimeOffset? TryParseSavedAt(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var marker = "_reviewoffset";
        if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^marker.Length];
        }

        return DateTimeOffset.TryParseExact(
            name,
            "yyyyMMdd_HHmmss_fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var savedAt)
                ? savedAt
                : null;
    }

    private static string NormalizeRecipeId(string value)
    {
        return Path.GetFileNameWithoutExtension(value?.Trim() ?? "");
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalidChars.Contains(character) ? '_' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "UNLINKED" : sanitized;
    }
}
