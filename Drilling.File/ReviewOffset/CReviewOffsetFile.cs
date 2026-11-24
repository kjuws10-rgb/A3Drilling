using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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
        "COORDINATE_SPACE",
        "MEASUREMENT_STATE",
        "MEASUREMENT_INITIALIZED_AT"
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fileLock;
    private readonly Dictionary<string, ST_APPLY_HISTORY_CACHE> _applyHistoryCache
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ST_RECIPE_APPLY_HISTORY_CACHE> _recipeApplyHistoryCache
        = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ST_APPLY_HISTORY_CACHE(
        long Length,
        DateTime LastWriteTimeUtc,
        string ContentSha256,
        IReadOnlyList<ST_REVIEW_OFFSET_SOURCE_APPLICATION> Rows);

    private sealed record ST_RECIPE_APPLY_HISTORY_CACHE(
        string Fingerprint, Dictionary<string, ST_REVIEW_OFFSET_SOURCE_APPLICATION> LatestBySource);

    public CReviewOffsetFile(string configRoot, Func<DateTimeOffset>? now = null)
    {
        var normalizedConfigRoot = Path.GetFullPath(configRoot);
        _applicationRoot = Directory.GetParent(normalizedConfigRoot)?.FullName ?? normalizedConfigRoot;
        _recipeRoot = Path.Combine(normalizedConfigRoot, "RECIPE");
        _fileLock = StoreLocks.GetOrAdd(_recipeRoot, _ => new SemaphoreSlim(1, 1));
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
            using var processLease = await AcquireProcessLease(cancellationToken).ConfigureAwait(false);
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

    public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(
        string recipeId,
        IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        CancellationToken cancellationToken = default)
        => SaveNewWithExpected(recipeId,rows,null,cancellationToken);

    public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewIfLatestUnchanged(
        string recipeId,ST_REVIEW_OFFSET_FILE_DATA expected,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        CancellationToken cancellationToken = default)
        => SaveNewWithExpected(recipeId,rows,expected ?? throw new ArgumentNullException(nameof(expected)),cancellationToken);

    private async Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewWithExpected(
        string recipeId,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,ST_REVIEW_OFFSET_FILE_DATA? expected,CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await AcquireProcessLease(cancellationToken).ConfigureAwait(false);
            using var activeLease = expected is null ? null : VerifyExpectedBasis(recipeId,expected,cancellationToken);
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
            using var processLease = await AcquireProcessLease(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRecipeId = RequireRecipeId(recipeId, "Review Offset 적용 이력 조회");
            var sourceIdentity = NormalizeSourceIdentity(sourceResultFile, requireExistingFile: false);
            return FindSourceApplicationAcrossHistory(
                normalizedRecipeId,
                sourceIdentity,
                cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewFromSource(
        string recipeId,
        IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        string sourceResultFile,
        bool allowDuplicate,
        CancellationToken cancellationToken = default)
        => SaveSourceWithExpected(recipeId,rows,sourceResultFile,allowDuplicate,null,cancellationToken);

    public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewFromSourceIfUnchanged(
        string recipeId,ST_REVIEW_OFFSET_FILE_DATA expected,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        string sourceResultFile,bool allowDuplicate,CancellationToken cancellationToken = default)
        => SaveSourceWithExpected(recipeId,rows,sourceResultFile,allowDuplicate,
            expected ?? throw new ArgumentNullException(nameof(expected)),cancellationToken);

    private async Task<ST_REVIEW_OFFSET_FILE_DATA> SaveSourceWithExpected(
        string recipeId,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,string sourceResultFile,bool allowDuplicate,
        ST_REVIEW_OFFSET_FILE_DATA? expected,CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await AcquireProcessLease(cancellationToken).ConfigureAwait(false);
            // @보호 [옵셋적용] 중복이력 - Source Result의 기존 적용 이력을 확인하고 명시적 허용 없이는 새 Offset을 만들지 않는다.
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRecipeId = RequireRecipeId(recipeId, "Review Offset 저장");
            using var activeLease = expected is null ? null : VerifyExpectedBasis(normalizedRecipeId,expected,cancellationToken);
            var sourceIdentity = NormalizeSourceIdentity(sourceResultFile, requireExistingFile: true);
            var appliedAt = _now();
            var existing = FindSourceApplicationAcrossHistory(
                normalizedRecipeId,
                sourceIdentity,
                cancellationToken);
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
                var historyPath = GetApplyHistoryPath(normalizedRecipeId, saved.SavedAt);
                var historyRows = LoadApplyHistory(historyPath, cancellationToken);
                var application = new ST_REVIEW_OFFSET_SOURCE_APPLICATION(
                    sourceIdentity,
                    saved.FileName,
                    saved.SavedAt,
                    Path.GetFullPath(historyPath));
                AppendApplyHistory(
                    historyPath,
                    historyRows,
                    application,
                    cancellationToken);
                UpdateRecipeApplyHistoryCache(normalizedRecipeId, application);
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

    /// <summary>Active duplicate-source history plus immutable evidence retained by a legacy rollback.</summary>
    public override async Task<ST_REVIEW_OFFSET_SOURCE_APPLICATION?> FindCommittedSourceApplication(
        string recipeId, string sourceResultFile, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await AcquireProcessLease(cancellationToken).ConfigureAwait(false);
            var id = RequireRecipeId(recipeId, "Review Offset 저장 증거 조회");
            var source = NormalizeSourceIdentity(sourceResultFile, requireExistingFile: false);
            var current = FindSourceApplicationAcrossHistory(id, source, cancellationToken);
            if (current is not null) return current;
            var archive = Path.Combine(GetRecipeOffsetDirectory(id), "_committed");
            if (!Directory.Exists(archive)) return null;
            return Directory.EnumerateFiles(archive, "*.csv", SearchOption.TopDirectoryOnly)
                .SelectMany(path => LoadApplyHistory(path, cancellationToken))
                .Where(row => row.SourceResultFile.Equals(source, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.AppliedAt).FirstOrDefault();
        }
        finally { _fileLock.Release(); }
    }

    /// <summary>Compare-and-save for the new tab. All existing save/rollback entry points retain their behavior.</summary>
    public override async Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewIfUnchanged(
        string recipeId, string expectedLatestFilePath, IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
        string sourceResultFile, CancellationToken cancellationToken = default,
        string? expectedLatestContentSha256 = null)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await AcquireProcessLease(cancellationToken).ConfigureAwait(false);
            var id = RequireRecipeId(recipeId, "Manual Inspection 저장");
            var latest = LoadLatestCore(id, cancellationToken);
            // Keep the verified active file read-locked through the transaction. A same-name
            // replacement by a non-cooperating editor must not race the compare-and-save boundary.
            using var activeLease = string.IsNullOrWhiteSpace(latest.FilePath) ? null : OpenStableRead(latest.FilePath);
            var activeSha = activeLease is null ? "" : Digest(activeLease);
            if (activeSha != latest.ContentSha256 || expectedLatestContentSha256 is not null
                && !activeSha.Equals(expectedLatestContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("활성 Review Offset 파일 내용이 변경되었습니다. 최신 기준을 다시 불러오세요.");
            static string Full(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
            if (!Full(latest.FilePath).Equals(Full(expectedLatestFilePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("활성 Review Offset이 변경되었습니다. 최신 기준으로 다시 계산하세요.");
            var source = NormalizeSourceIdentity(sourceResultFile, requireExistingFile: true);
            var existing = FindSourceApplicationAcrossHistory(id, source, cancellationToken);
            if (existing is not null) throw new CReviewOffsetDuplicateSourceException(existing);
            var at = _now();
            var saved = SaveNewCore(id, rows, cancellationToken, at);
            try
            {
                var historyPath = GetApplyHistoryPath(id, saved.SavedAt);
                var history = LoadApplyHistory(historyPath, cancellationToken);
                var application = new ST_REVIEW_OFFSET_SOURCE_APPLICATION(source, saved.FileName, saved.SavedAt, Path.GetFullPath(historyPath));
                AppendApplyHistory(historyPath, history, application, cancellationToken);
                UpdateRecipeApplyHistoryCache(id, application);
                // Return the actual six-decimal-mm representation without new I/O/cancellation
                // after the authoritative file+history commit has succeeded.
                return saved;
            }
            catch (Exception historyError)
            {
                try { if (System.IO.File.Exists(saved.FilePath)) System.IO.File.Delete(saved.FilePath); }
                catch (Exception cleanupError)
                { throw new AggregateException("Manual Inspection 이력 실패 후 신규 Offset 정리에도 실패했습니다.", historyError, cleanupError); }
                throw new IOException("적용 이력 저장 실패로 신규 Offset을 취소했습니다.", historyError);
            }
        }
        finally { _fileLock.Release(); }
    }

    // Called only while the process/store locks are held. Retain the returned non-delete-sharing
    // read handle through publication so a same-name external editor cannot race the SHA check.
    private FileStream? VerifyExpectedBasis(string recipeId,ST_REVIEW_OFFSET_FILE_DATA expected,CancellationToken token)
    {
        var id=RequireRecipeId(recipeId,"Review Offset 저장 기준");
        if(!expected.RecipeId.Equals(id,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Review Offset 계산 기준의 Recipe가 다릅니다.");
        var latest=LoadLatestCore(id,token);
        static string Full(string path)=>string.IsNullOrWhiteSpace(path)?"":Path.GetFullPath(path);
        if(!Full(latest.FilePath).Equals(Full(expected.FilePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("활성 Review Offset이 변경되었습니다. 최신 기준으로 다시 계산하세요.");
        if(string.IsNullOrWhiteSpace(latest.FilePath))
        {
            if(expected.ContentSha256.Length!=0 || expected.Rows.Count!=0)
                throw new InvalidOperationException("비어 있는 Review Offset 계산 기준이 실제 파일과 다릅니다.");
            return null;
        }
        var lease=OpenStableRead(latest.FilePath);
        try
        {
            var digest=Digest(lease);
            if(!digest.Equals(latest.ContentSha256,StringComparison.OrdinalIgnoreCase)
                || !digest.Equals(expected.ContentSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("활성 Review Offset 파일 내용이 변경되었습니다. 최신 기준으로 다시 계산하세요.");
            return lease;
        }
        catch { lease.Dispose();throw; }
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

        // @파일 [리뷰옵셋] 새버전 - 검증된 전체 행을 임시 파일에 쓴 뒤 새 이름으로 이동하며 기존 활성 파일은 수정하지 않는다.
        var normalizedRows = CReviewOffsetData.ValidateRows(rows,
            $"{normalizedRecipeId} (저장할 Review Offset)", token: cancellationToken)
            .Select(row => row with { ReviewOffsetX = WrittenValue(row.ReviewOffsetX), ReviewOffsetY = WrittenValue(row.ReviewOffsetY) }).ToArray();
        if (rows.Any(row => row.IsMeasurementOffsetInitialized && (row.ReviewOffsetX != 0 || row.ReviewOffsetY != 0)))
            throw new InvalidDataException("초기화된 측정 Offset은 X/Y 모두 0이어야 합니다.");
        var savedAt = (fixedSavedAt ?? _now()).ToLocalTime();
        var directory = GetRecipeOffsetDirectory(normalizedRecipeId);
        Directory.CreateDirectory(directory);
        var latestName = EnumerateActiveFiles(directory).Select(Path.GetFileName).FirstOrDefault();
        var latestAt = latestName is null ? null : TryParseSavedAt(latestName);
        if (latestAt.HasValue && StringComparer.OrdinalIgnoreCase.Compare($"{savedAt:yyyyMMdd_HHmmss_fff}_reviewoffset.csv", latestName) <= 0)
            savedAt = latestAt.Value.AddMilliseconds(1);
        if (latestName is not null && StringComparer.OrdinalIgnoreCase.Compare($"{savedAt:yyyyMMdd_HHmmss_fff}_reviewoffset.csv", latestName) <= 0)
            throw new InvalidDataException("현재 활성 Offset 파일명으로 신규 버전 순서를 확인할 수 없습니다. 파일명을 확인하세요.");
        normalizedRows = normalizedRows.Select(row => row with
        {
            MeasurementInitializedAt = row.IsMeasurementOffsetInitialized ? row.MeasurementInitializedAt ?? savedAt : null
        }).ToArray();
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        string contentSha256;
        string path;
        try
        {
            CCsvParser.Write(temporary, Headers, normalizedRows.OrderBy(row => row.CellId)
                .ThenBy(row => row.ShotId, StringComparer.OrdinalIgnoreCase).Select(ToCsvRow));
            using (var immutable = OpenStableRead(temporary)) contentSha256 = Digest(immutable);
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
            normalizedRows) { ContentSha256 = contentSha256, HasMeasurementStateMetadata = true };
    }

    public override async Task<ST_REVIEW_OFFSET_ROLLBACK_RESULT> RollbackLatest(
        string recipeId,
        string expectedLatestFilePath,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await AcquireProcessLease(cancellationToken).ConfigureAwait(false);
            // @파일 [리뷰옵셋] 롤백 - 확인한 최신 파일만 Rollback 폴더로 이동하고 바로 이전 파일을 활성값으로 사용한다.
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
            if (historyChanged)
            {
                // The legacy rollback may remove active duplicate-source history, but a successful
                // past commit remains evidence for Manual Inspection's consumed-raw ledger.
                var auditPath = Path.Combine(directory, "_committed", $"{Guid.NewGuid():N}.csv");
                WriteApplyHistory(auditPath, originalHistory.Where(row => row.OffsetFile.Equals(removed.FileName, StringComparison.OrdinalIgnoreCase)).ToArray(), cancellationToken);
            }
            var offsetMoved = false;
            try
            {
                System.IO.File.Move(latestPath, archivedPath, overwrite: false);
                offsetMoved = true;
                if (historyChanged)
                {
                    WriteApplyHistory(historyPath, retainedHistory, cancellationToken);
                }

                _recipeApplyHistoryCache.Remove(normalizedRecipeId);

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
                    _recipeApplyHistoryCache.Remove(normalizedRecipeId);
                    throw new AggregateException(
                        "Review Offset 롤백 실패 후 파일 상태 복구에도 실패했습니다.",
                        new[] { rollbackError }.Concat(recoveryErrors));
                }

                _recipeApplyHistoryCache.Remove(normalizedRecipeId);
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

    private async Task<FileStream> AcquireProcessLease(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_applicationRoot);
        var path = Path.Combine(_applicationRoot, ".reviewoffset.lock.tmp");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
            { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }

    private string HistoryFingerprint(string recipeId, CancellationToken token)
    {
        var directory = Path.Combine(GetRecipeOffsetDirectory(recipeId), ApplyHistoryDirectoryName);
        if (!Directory.Exists(directory)) return "";
        var files = Directory.EnumerateFiles(directory, ApplyHistoryFileName, SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var stamps = new List<string>(files.Length);
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            stamps.Add(Path.GetFullPath(path) + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + FileDigest(path));
        }
        return string.Join("\n", stamps);
    }

    private static FileStream OpenStableRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static string Digest(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    private static string FileDigest(string path) { using var immutable = OpenStableRead(path); return Digest(immutable); }
    private static double WrittenValue(double value) => double.Parse(FormatDouble(value), CultureInfo.InvariantCulture);

    private ST_REVIEW_OFFSET_SOURCE_APPLICATION? FindSourceApplicationAcrossHistory(
        string recipeId,
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        var fingerprint = HistoryFingerprint(recipeId, cancellationToken);
        if (!_recipeApplyHistoryCache.TryGetValue(recipeId, out var cache) || cache.Fingerprint != fingerprint)
        {
            // @성능 [옵셋적용] 이력색인 - Recipe의 일자별 CSV를 Source 경로별 최신 이력으로 한 번 색인해 반복 APPLY 조회를 줄인다.
            var latestBySource = new Dictionary<string, ST_REVIEW_OFFSET_SOURCE_APPLICATION>(
                StringComparer.OrdinalIgnoreCase);
            var applyDirectory = Path.Combine(
                GetRecipeOffsetDirectory(recipeId),
                ApplyHistoryDirectoryName);
            if (Directory.Exists(applyDirectory))
            {
                foreach (var historyPath in Directory.EnumerateFiles(
                             applyDirectory,
                             ApplyHistoryFileName,
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var application in LoadApplyHistory(historyPath, cancellationToken))
                    {
                        if (!latestBySource.TryGetValue(application.SourceResultFile, out var current)
                            || application.AppliedAt > current.AppliedAt)
                        {
                            latestBySource[application.SourceResultFile] = application;
                        }
                    }
                }
            }

            if (HistoryFingerprint(recipeId, cancellationToken) != fingerprint)
                throw new IOException("Review Offset 적용 이력이 읽는 중 변경되었습니다. 다시 조회하세요.");
            cache = new ST_RECIPE_APPLY_HISTORY_CACHE(fingerprint, latestBySource);
            _recipeApplyHistoryCache[recipeId] = cache;
        }

        return cache.LatestBySource.GetValueOrDefault(sourceIdentity);
    }

    private void UpdateRecipeApplyHistoryCache(
        string recipeId,
        ST_REVIEW_OFFSET_SOURCE_APPLICATION application)
    {
        try
        {
            if (!_recipeApplyHistoryCache.TryGetValue(recipeId, out var cache))
            {
                return;
            }

            cache.LatestBySource[application.SourceResultFile] = application;
            _recipeApplyHistoryCache[recipeId] = cache with { Fingerprint = HistoryFingerprint(recipeId, CancellationToken.None) };
        }
        catch
        {
            // History is already committed. Cache failures must not invalidate a
            // valid Offset/history pair; the next lookup rebuilds the index.
            _recipeApplyHistoryCache.Remove(recipeId);
        }
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

        using var immutable = OpenStableRead(fullPath);
        var contentSha256 = Digest(immutable);
        var fileInfo = new FileInfo(fullPath);
        // @성능 [옵셋적용] 파일캐시 - 길이와 수정시각이 같으면 CSV 파싱 결과를 재사용한다.
        if (_applyHistoryCache.TryGetValue(fullPath, out var cached)
            && cached.Length == fileInfo.Length
            && cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc
            && cached.ContentSha256 == contentSha256)
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
        // @파일 [옵셋적용] 이력저장 - Offset 파일 생성이 성공한 같은 작업에서 Source, Offset, 적용시각을 CSV에 추가한다.
        cancellationToken.ThrowIfCancellationRequested();
        // Never append directly to the authoritative CSV: a failed append can leave half a row
        // after the new offset is rolled back. Publish the complete next history atomically.
        WriteApplyHistory(historyPath, existingRows.Concat([application]).ToArray(), cancellationToken);
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
                FileDigest(fullPath),
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

    /// <summary>Read an existing Review CSV without selecting or publishing an active version.
    /// The CSV has no Recipe identity; recipeId is the caller's explicitly confirmed model context.</summary>
    public static ST_REVIEW_OFFSET_FILE_DATA ReadSnapshotFile(string recipeId, string path, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(recipeId)) throw new InvalidDataException("Review CSV의 현재 모델을 확인하세요.");
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Review Offset CSV 파일을 선택하세요.");
        return Load(recipeId, full, token);
    }

    private static ST_REVIEW_OFFSET_FILE_DATA Load(string recipeId, string path, CancellationToken token)
    {
        try
        {
            // Hold a read-only, non-delete-sharing handle across parsing so returned rows and SHA
            // describe exactly the same file even if another program attempts an in-place edit.
            using var immutable = OpenStableRead(path);
            var contentSha256 = Digest(immutable);
            CCsvParser.ValidateRequiredHeaders(
                path,
                "Review Offset",
                RequiredHeaderGroups);

            var lineNumbers = new List<int>();
            var hasMeasurementState = CCsvParser.ReadHeaders(path)
                .Any(header => string.Equals(header.Trim(), "MEASUREMENT_STATE", StringComparison.OrdinalIgnoreCase));
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
            // Freeze the legacy state-column fallback once, so an unrelated later save cannot
            // move this row's reset boundary and hide measurements acquired in the meantime.
            rows = rows.Select(row => row.IsMeasurementOffsetInitialized && !row.MeasurementInitializedAt.HasValue
                ? row with { MeasurementInitializedAt = savedAt } : row).ToArray();
            return new ST_REVIEW_OFFSET_FILE_DATA(
                recipeId,
                fileInfo.FullName,
                fileInfo.Name,
                savedAt,
                rows) { ContentSha256 = contentSha256, HasMeasurementStateMetadata = hasMeasurementState };
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
        var state = CCsvParser.Get(row, "MEASUREMENT_STATE").Trim().ToUpperInvariant();
        if (state is not ("" or "MEASURED" or "INITIALIZED"))
            throw new InvalidDataException($"Review Offset row {rowNo}: invalid MEASUREMENT_STATE {state}.");
        if (state == "INITIALIZED" && (offsetX != 0 || offsetY != 0))
            throw new InvalidDataException($"Review Offset row {rowNo}: 초기화된 측정 Offset은 X/Y 모두 0이어야 합니다.");
        var initializedAtText = CCsvParser.Get(row, "MEASUREMENT_INITIALIZED_AT").Trim();
        DateTimeOffset? initializedAt = null;
        if (initializedAtText.Length > 0)
        {
            if (state != "INITIALIZED" || !DateTimeOffset.TryParseExact(initializedAtText, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedAt))
                throw new InvalidDataException($"Review Offset row {rowNo}: invalid MEASUREMENT_INITIALIZED_AT.");
            initializedAt = parsedAt;
        }
        return new ST_REVIEW_OFFSET_ROW(cellId, shotId, offsetX, offsetY)
        {
            IsDesignCoordinate = space == "DESIGN",
            IsMeasurementOffsetInitialized = state == "INITIALIZED",
            MeasurementInitializedAt = initializedAt
        };
    }

    private static IReadOnlyDictionary<string, string> ToCsvRow(ST_REVIEW_OFFSET_ROW row)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CELLID"] = row.CellId.ToString(CultureInfo.InvariantCulture),
            ["SHOTID"] = row.ShotId,
            ["REVIEWOFFSETX"] = FormatDouble(row.ReviewOffsetX),
            ["REVIEWOFFSETY"] = FormatDouble(row.ReviewOffsetY),
            ["COORDINATE_SPACE"] = row.IsDesignCoordinate ? "DESIGN" : "SCANNER",
            ["MEASUREMENT_STATE"] = row.IsMeasurementOffsetInitialized ? "INITIALIZED" : "MEASURED",
            ["MEASUREMENT_INITIALIZED_AT"] = row.IsMeasurementOffsetInitialized ? row.MeasurementInitializedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "" : ""
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
