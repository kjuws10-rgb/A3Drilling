using System.Globalization;
using Drilling.Common.Review;
using Drilling.Common.Recipe;
using Drilling.File.Parser;

namespace Drilling.File.ReviewOffset;

public sealed class CReviewOffsetFile : CReviewOffsetFileBase
{
    private const string ApplyHistoryDirectoryName = "_apply";
    private const string ApplyHistoryFileName = "AppliedSources.csv";
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

    private static readonly IReadOnlyList<string> ApplyHistoryHeaders =
    [
        "SOURCE_RESULT_FILE",
        "OFFSET_FILE",
        "APPLIED_AT"
    ];

    private static readonly IReadOnlyList<IReadOnlyList<string>> ApplyHistoryRequiredHeaderGroups =
    [
        ["SOURCE_RESULT_FILE"],
        ["OFFSET_FILE"],
        ["APPLIED_AT"]
    ];

    private readonly string _applicationRoot;
    private readonly string _recipeRoot;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Dictionary<string, ST_APPLY_HISTORY_CACHE> _applyHistoryCache
        = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ST_APPLY_HISTORY_CACHE(
        long Length,
        DateTime LastWriteTimeUtc,
        IReadOnlyList<ST_REVIEW_OFFSET_SOURCE_APPLICATION> Rows);

    public CReviewOffsetFile(string configRoot, Func<DateTimeOffset>? now = null)
    {
        var normalizedConfigRoot = Path.GetFullPath(configRoot);
        _applicationRoot = Directory.GetParent(normalizedConfigRoot)?.FullName ?? normalizedConfigRoot;
        _recipeRoot = Path.Combine(normalizedConfigRoot, "RECIPE");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public override string RootPath => _recipeRoot;

    public override async Task<ST_REVIEW_OFFSET_FILE_DATA> LoadLatest(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadLatestCore(recipeId, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private ST_REVIEW_OFFSET_FILE_DATA LoadLatestCore(
        string recipeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRecipeId = NormalizeRecipeId(recipeId);
        if (string.IsNullOrWhiteSpace(normalizedRecipeId))
        {
            return ST_REVIEW_OFFSET_FILE_DATA.Empty("");
        }

        var directory = GetRecipeOffsetDirectory(normalizedRecipeId);
        if (!Directory.Exists(directory))
        {
            return ST_REVIEW_OFFSET_FILE_DATA.Empty(normalizedRecipeId);
        }

        var latestFile = EnumerateActiveFiles(directory).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(latestFile))
        {
            return ST_REVIEW_OFFSET_FILE_DATA.Empty(normalizedRecipeId);
        }

        return Load(normalizedRecipeId, latestFile, cancellationToken);
    }

    public override async Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(
        string recipeId,
        IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return SaveNewCore(recipeId, rows, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public override async Task<ST_REVIEW_OFFSET_SOURCE_APPLICATION?> FindSourceApplication(
        string recipeId,
        string sourceResultFile,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRecipeId = RequireRecipeId(recipeId, "Review Offset 적용 이력 조회");
            var sourceIdentity = NormalizeSourceIdentity(sourceResultFile, requireExistingFile: false);
            var appliedAt = _now();
            var historyPath = GetApplyHistoryPath(normalizedRecipeId, appliedAt);
            var historyRows = LoadApplyHistory(historyPath, cancellationToken);
            return FindSourceApplicationCore(sourceIdentity, historyRows);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public override async Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewFromSource(
        string recipeId,
        IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        string sourceResultFile,
        bool allowDuplicate,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRecipeId = RequireRecipeId(recipeId, "Review Offset 저장");
            var sourceIdentity = NormalizeSourceIdentity(sourceResultFile, requireExistingFile: true);
            var appliedAt = _now();
            var historyPath = GetApplyHistoryPath(normalizedRecipeId, appliedAt);
            var historyRows = LoadApplyHistory(historyPath, cancellationToken);
            var existing = FindSourceApplicationCore(sourceIdentity, historyRows);
            if (existing is not null && !allowDuplicate)
            {
                throw new CReviewOffsetDuplicateSourceException(existing);
            }

            var saved = SaveNewCore(
                normalizedRecipeId,
                rows,
                cancellationToken,
                appliedAt);
            try
            {
                var application = new ST_REVIEW_OFFSET_SOURCE_APPLICATION(
                    sourceIdentity,
                    saved.FileName,
                    appliedAt,
                    Path.GetFullPath(historyPath));
                AppendApplyHistory(
                    historyPath,
                    historyRows,
                    application,
                    cancellationToken);
                return saved;
            }
            catch (Exception historyError)
            {
                try
                {
                    if (System.IO.File.Exists(saved.FilePath))
                    {
                        System.IO.File.Delete(saved.FilePath);
                    }
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Review Offset 적용 이력 저장 실패 후 신규 Offset 파일 정리에도 실패했습니다.",
                        historyError,
                        cleanupError);
                }

                throw new IOException(
                    "Review Offset 적용 이력을 저장하지 못해 신규 Offset 파일 생성을 취소했습니다.",
                    historyError);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private ST_REVIEW_OFFSET_FILE_DATA SaveNewCore(
        string recipeId,
        IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        CancellationToken cancellationToken,
        DateTimeOffset? fixedSavedAt = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRecipeId = NormalizeRecipeId(recipeId);
        if (string.IsNullOrWhiteSpace(normalizedRecipeId))
        {
            throw new InvalidDataException("Review Offset save failed. Recipe ID is empty.");
        }

        var normalizedRows = CReviewOffsetData.ValidateRows(rows,
            $"{normalizedRecipeId} (저장할 Review Offset)", token: cancellationToken);
        var savedAt = fixedSavedAt ?? _now();
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

        return new ST_REVIEW_OFFSET_FILE_DATA(
            normalizedRecipeId,
            Path.GetFullPath(path),
            Path.GetFileName(path),
            savedAt,
            normalizedRows);
    }

    public override async Task<ST_REVIEW_OFFSET_ROLLBACK_RESULT> RollbackLatest(
        string recipeId,
        string expectedLatestFilePath,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRecipeId = NormalizeRecipeId(recipeId);
            if (string.IsNullOrWhiteSpace(normalizedRecipeId))
            {
                throw new InvalidOperationException("Review Offset rollback failed. Recipe ID is empty.");
            }

            var directory = GetRecipeOffsetDirectory(normalizedRecipeId);
            var activeFiles = Directory.Exists(directory)
                ? EnumerateActiveFiles(directory).ToArray()
                : [];
            if (activeFiles.Length == 0)
            {
                throw new InvalidOperationException($"{normalizedRecipeId} 레시피에 되돌릴 Review Offset 파일이 없습니다.");
            }

            var latestPath = Path.GetFullPath(activeFiles[0]);
            var expectedPath = string.IsNullOrWhiteSpace(expectedLatestFilePath)
                ? ""
                : Path.GetFullPath(expectedLatestFilePath);
            if (!latestPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("확인 중 최신 Review Offset 파일이 변경되었습니다. 현재 값을 다시 확인한 뒤 롤백해 주세요.");
            }

            var removed = Load(normalizedRecipeId, latestPath, cancellationToken);
            var current = activeFiles.Length > 1
                ? Load(normalizedRecipeId, activeFiles[1], cancellationToken)
                : ST_REVIEW_OFFSET_FILE_DATA.Empty(normalizedRecipeId);

            var rollbackDirectory = Path.Combine(directory, "Rollback");
            Directory.CreateDirectory(rollbackDirectory);
            var archivedPath = CreateArchivePath(rollbackDirectory, removed.FileName);
            var historyPath = GetApplyHistoryPath(normalizedRecipeId, removed.SavedAt);
            var historyExisted = System.IO.File.Exists(historyPath);
            var originalHistory = LoadApplyHistory(historyPath, cancellationToken);
            var retainedHistory = originalHistory
                .Where(row => !row.OffsetFile.Equals(removed.FileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var historyChanged = retainedHistory.Length != originalHistory.Count;
            var offsetMoved = false;
            try
            {
                System.IO.File.Move(latestPath, archivedPath, overwrite: false);
                offsetMoved = true;
                if (historyChanged)
                {
                    WriteApplyHistory(historyPath, retainedHistory, cancellationToken);
                }

                var latestAfterRollback = EnumerateActiveFiles(directory).FirstOrDefault();
                var expectedCurrentPath = current.FilePath;
                if (!string.Equals(
                        string.IsNullOrWhiteSpace(latestAfterRollback) ? "" : Path.GetFullPath(latestAfterRollback),
                        expectedCurrentPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Review Offset 롤백 후 활성 파일 검증에 실패했습니다.");
                }
            }
            catch (Exception rollbackError)
            {
                var recoveryErrors = new List<Exception>();
                if (offsetMoved)
                {
                    try
                    {
                        if (System.IO.File.Exists(archivedPath) && !System.IO.File.Exists(latestPath))
                        {
                            System.IO.File.Move(archivedPath, latestPath, overwrite: false);
                        }
                    }
                    catch (Exception recoveryError)
                    {
                        recoveryErrors.Add(recoveryError);
                    }
                }

                if (historyChanged)
                {
                    try
                    {
                        if (historyExisted)
                        {
                            WriteApplyHistory(historyPath, originalHistory, CancellationToken.None);
                        }
                        else if (System.IO.File.Exists(historyPath))
                        {
                            System.IO.File.Delete(historyPath);
                            _applyHistoryCache.Remove(Path.GetFullPath(historyPath));
                        }
                    }
                    catch (Exception recoveryError)
                    {
                        recoveryErrors.Add(recoveryError);
                    }
                }

                if (recoveryErrors.Count > 0)
                {
                    throw new AggregateException(
                        "Review Offset 롤백 실패 후 파일 상태 복구에도 실패했습니다.",
                        new[] { rollbackError }.Concat(recoveryErrors));
                }

                throw;
            }

            return new ST_REVIEW_OFFSET_ROLLBACK_RESULT(
                normalizedRecipeId,
                removed.FileName,
                removed.SavedAt,
                Path.GetFullPath(archivedPath),
                current);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static IEnumerable<string> EnumerateActiveFiles(string directory)
    {
        return Directory
            .EnumerateFiles(directory, "*_reviewoffset.csv", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    private static ST_REVIEW_OFFSET_SOURCE_APPLICATION? FindSourceApplicationCore(
        string sourceIdentity,
        IReadOnlyList<ST_REVIEW_OFFSET_SOURCE_APPLICATION> historyRows)
    {
        return historyRows
            .Where(row => row.SourceResultFile.Equals(sourceIdentity, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.AppliedAt)
            .FirstOrDefault();
    }

    private IReadOnlyList<ST_REVIEW_OFFSET_SOURCE_APPLICATION> LoadApplyHistory(
        string historyPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(historyPath);
        if (!System.IO.File.Exists(fullPath))
        {
            _applyHistoryCache.Remove(fullPath);
            return [];
        }

        var fileInfo = new FileInfo(fullPath);
        if (_applyHistoryCache.TryGetValue(fullPath, out var cached)
            && cached.Length == fileInfo.Length
            && cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
        {
            return cached.Rows;
        }

        CCsvParser.ValidateRequiredHeaders(
            fullPath,
            "Review Offset 적용 이력",
            ApplyHistoryRequiredHeaderGroups);
        var lineNumbers = new List<int>();
        var sourceRows = CCsvParser.Read(fullPath, lineNumbers);
        var rows = new List<ST_REVIEW_OFFSET_SOURCE_APPLICATION>(sourceRows.Count);
        for (var index = 0; index < sourceRows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = sourceRows[index];
            var lineNo = lineNumbers[index];
            var source = NormalizeStoredSourceIdentity(CCsvParser.RequireText(
                row,
                "Review Offset 적용 이력",
                lineNo,
                "SOURCE_RESULT_FILE"));
            var offsetFile = CCsvParser.RequireText(
                row,
                "Review Offset 적용 이력",
                lineNo,
                "OFFSET_FILE").Trim();
            if (!offsetFile.Equals(Path.GetFileName(offsetFile), StringComparison.Ordinal)
                || !offsetFile.EndsWith("_reviewoffset.csv", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Review Offset 적용 이력 {lineNo}행의 OFFSET_FILE이 올바르지 않습니다: {offsetFile}");
            }

            var appliedAtText = CCsvParser.RequireText(
                row,
                "Review Offset 적용 이력",
                lineNo,
                "APPLIED_AT");
            if (!DateTimeOffset.TryParse(
                    appliedAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var appliedAt))
            {
                throw new InvalidDataException(
                    $"Review Offset 적용 이력 {lineNo}행의 APPLIED_AT이 올바르지 않습니다: {appliedAtText}");
            }

            rows.Add(new(
                source,
                offsetFile,
                appliedAt,
                fullPath));
        }

        var result = rows.ToArray();
        UpdateApplyHistoryCache(fullPath, result);
        return result;
    }

    private void AppendApplyHistory(
        string historyPath,
        IReadOnlyList<ST_REVIEW_OFFSET_SOURCE_APPLICATION> existingRows,
        ST_REVIEW_OFFSET_SOURCE_APPLICATION application,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.IO.File.Exists(historyPath))
        {
            WriteApplyHistory(historyPath, [application], cancellationToken);
            return;
        }

        CCsvParser.Append(
            historyPath,
            ApplyHistoryHeaders,
            ToApplyHistoryCsvRow(application));
        UpdateApplyHistoryCache(
            historyPath,
            existingRows.Concat([application]).ToArray());
    }

    private void WriteApplyHistory(
        string historyPath,
        IReadOnlyList<ST_REVIEW_OFFSET_SOURCE_APPLICATION> rows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(historyPath)
            ?? throw new InvalidOperationException("Review Offset 적용 이력 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            CCsvParser.Write(
                temporary,
                ApplyHistoryHeaders,
                rows.OrderBy(row => row.AppliedAt).Select(ToApplyHistoryCsvRow));
            cancellationToken.ThrowIfCancellationRequested();
            System.IO.File.Move(temporary, historyPath, overwrite: true);
            UpdateApplyHistoryCache(historyPath, rows.ToArray());
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
            {
                System.IO.File.Delete(temporary);
            }
        }
    }

    private void UpdateApplyHistoryCache(
        string historyPath,
        IReadOnlyList<ST_REVIEW_OFFSET_SOURCE_APPLICATION> rows)
    {
        var fullPath = Path.GetFullPath(historyPath);
        try
        {
            var fileInfo = new FileInfo(fullPath);
            fileInfo.Refresh();
            _applyHistoryCache[fullPath] = new(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                rows);
        }
        catch
        {
            // The CSV is already committed. A cache refresh failure must not roll back
            // a valid Offset/history pair; the next lookup reloads it from disk.
            _applyHistoryCache.Remove(fullPath);
        }
    }

    private static IReadOnlyDictionary<string, string> ToApplyHistoryCsvRow(
        ST_REVIEW_OFFSET_SOURCE_APPLICATION row)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SOURCE_RESULT_FILE"] = row.SourceResultFile,
            ["OFFSET_FILE"] = row.OffsetFile,
            ["APPLIED_AT"] = row.AppliedAt.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private string GetApplyHistoryPath(string recipeId, DateTimeOffset appliedAt)
    {
        var local = appliedAt.ToLocalTime();
        return Path.Combine(
            GetRecipeOffsetDirectory(recipeId),
            ApplyHistoryDirectoryName,
            local.Year.ToString("0000", CultureInfo.InvariantCulture),
            local.Month.ToString("00", CultureInfo.InvariantCulture),
            local.Day.ToString("00", CultureInfo.InvariantCulture),
            ApplyHistoryFileName);
    }

    private string NormalizeSourceIdentity(string sourceResultFile, bool requireExistingFile)
    {
        if (string.IsNullOrWhiteSpace(sourceResultFile))
        {
            throw new InvalidDataException("Review Offset 적용 출처 파일이 비어 있습니다.");
        }

        var fullPath = Path.GetFullPath(sourceResultFile.Trim());
        if (requireExistingFile && !System.IO.File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "Review Offset 적용 출처 파일을 찾을 수 없습니다.",
                fullPath);
        }

        var relativePath = Path.GetRelativePath(_applicationRoot, fullPath);
        if (!Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return relativePath.Replace('\\', '/');
        }

        return fullPath.Replace('\\', '/');
    }

    private static string NormalizeStoredSourceIdentity(string sourceResultFile)
    {
        var normalized = sourceResultFile.Trim().Replace('\\', '/');
        return Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized).Replace('\\', '/')
            : normalized;
    }

    private static string RequireRecipeId(string recipeId, string operation)
    {
        var normalized = NormalizeRecipeId(recipeId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidDataException($"{operation} 실패. Recipe ID가 비어 있습니다.");
        }

        return normalized;
    }

    private static string CreateArchivePath(string rollbackDirectory, string fileName)
    {
        var path = Path.Combine(rollbackDirectory, fileName);
        if (!System.IO.File.Exists(path))
        {
            return path;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Path.Combine(rollbackDirectory, $"{name}_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}{extension}");
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
