namespace Drilling.File.ReviewResult
{
    using System.Globalization;
    using Drilling.Common.Review;
    using Drilling.File.Parser;
    using System.Text;
    using Microsoft.VisualBasic.FileIO;

    public sealed partial class CReviewResultFile(string configRoot) : CReviewResultFileBase
    {
#region 공유 상태·초기값
        private static readonly string[] BeamInspectionHeaders = ["GLASS_NAME", "HEAD_NAME", "CELL_NAME", "SHOT_NAME", ..Enumerable.Range(1, 16).SelectMany(beam => new[] { $"BEAM{beam}_X_MM", $"BEAM{beam}_Y_MM", $"BEAM{beam}_JUDGE" })];
        private static readonly IReadOnlyList<IReadOnlyList<string>> RequiredHeaderGroups = [["SAVED_AT"], ["RECIPE_ID"], ["SHOT_KEY", "HOLE_KEY"], ["HEAD_NO"], ["CELL_NO"], ["ERROR_X"], ["ERROR_Y"], ["JUDGE"]];
        private static readonly IReadOnlyList<string> Headers = [
            "SAVED_AT", 
            "GLASS_ID", 
            "RECIPE_ID", 
            "INSPECTION_METHOD", 
            "RESULT_STATUS", 
            "TOTAL_COUNT", 
            "RULE", 
            "RULE_SCOPE", 
            "BEAM_NO", 
            "REFERENCE_CELL_NO", 
            "REFERENCE_SHOT", 
            "ROUGH_REFERENCE", 
            "FINE_LINE_COUNT", 
            "RESULT_SOURCE", 
            "SHOT_NAME", 
            "ROW", 
            "COLUMN", 
            "DESIGN_X", 
            "DESIGN_Y", 
            "SHOT_KEY", 
            "HEAD_NO", 
            "CELL_NO", 
            "ERROR_X", 
            "ERROR_Y", 
            "JUDGE"
        ];
        private readonly string _reviewResultRoot = Path.Combine(Directory.GetParent(configRoot)?.FullName ?? configRoot, "Data", "ReviewResult");
        private readonly string _legacyReviewResultRoot = Path.Combine(Directory.GetParent(configRoot)?.FullName ?? configRoot, "Data", "Result");
        private string? _lastSavedPath;
#endregion
#region 기본 처리
        public override string RootPath => _reviewResultRoot;
        public override IReadOnlyList<string> SearchRootPaths => [_reviewResultRoot, _legacyReviewResultRoot];
        public override string? LastSavedPath => _lastSavedPath;

        public override Task<ST_REVIEW_RESULT_FILE_DATA> Load(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CCsvParser.ValidateRequiredHeaders(path, "Review Result", RequiredHeaderGroups);
            var sourceRows = CCsvParser.Read(path);
            if (sourceRows.Count == 0)
            {
                throw new InvalidDataException("Review Result validation failed. Result row is empty.");
            }

            var rows = sourceRows.Select((row, index) => ToResultRow(row, index + 2)).ToArray();
            var recipeIds = rows.Select(row => row.RecipeId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (recipeIds.Length != 1)
            {
                throw new InvalidDataException("Review Result validation failed. All rows must have the same RECIPE_ID.");
            }

            var fullPath = Path.GetFullPath(path);
            var metadataRow = sourceRows[0];
            return Task.FromResult(new ST_REVIEW_RESULT_FILE_DATA(fullPath, Path.GetFileName(fullPath), recipeIds[0], rows.Max(row => row.SavedAt), rows) { GlassId = CCsvParser.Get(metadataRow, "GLASS_ID"), InspectionMethod = CCsvParser.Get(metadataRow, "INSPECTION_METHOD"), ResultStatus = NormalizeResultStatus(CCsvParser.Get(metadataRow, "RESULT_STATUS")), TotalCount = ReadOptionalInt(metadataRow, "TOTAL_COUNT", rows.Length), Rule = CCsvParser.Get(metadataRow, "RULE"), RuleScope = CCsvParser.Get(metadataRow, "RULE_SCOPE"), BeamNo = ReadOptionalInt(metadataRow, "BEAM_NO", 1), ReferenceCellNo = ReadOptionalNullableInt(metadataRow, "REFERENCE_CELL_NO"), ReferenceShot = CCsvParser.Get(metadataRow, "REFERENCE_SHOT"), RoughReference = CCsvParser.Get(metadataRow, "ROUGH_REFERENCE"), FineLineCount = ReadOptionalInt(metadataRow, "FINE_LINE_COUNT", 0) });
        }

        public override Task Save(ST_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRows = result.Results.Count > 0 ? result.Results : result.Plan.ReviewPoints;
            var rows = new List<ST_REVIEW_PLAN_POINT>();
            foreach (var point in sourceRows)
            {
                if (point.Use && point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng)
                {
                    rows.Add(point);
                }
            }

            if (rows.Count == 0)
            {
                throw new InvalidDataException("Review Result save failed. There are no measured Shots.");
            }

            var metadata = CreateMetadata(result);
            _lastSavedPath = null;
            var resultPath = string.IsNullOrWhiteSpace(result.TargetFilePath) ? GetResultPath(result, metadata) : Path.GetFullPath(result.TargetFilePath);
            CCsvParser.Write(resultPath, Headers, rows.Select(point => ToRow(result, point, metadata)));
            _lastSavedPath = Path.GetFullPath(resultPath);
            return Task.CompletedTask;
        }

        private static ST_REVIEW_RESULT_FILE_ROW ToResultRow(IReadOnlyDictionary<string, string> row, int rowNo)
        {
            var savedAtText = CCsvParser.RequireText(row, "Review Result", rowNo, "SAVED_AT");
            if (!DateTimeOffset.TryParse(savedAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var savedAt))
            {
                throw new InvalidDataException($"Review Result validation failed. Row {rowNo} / SAVED_AT must be a date and time.");
            }

            var recipeId = CCsvParser.RequireText(row, "Review Result", rowNo, "RECIPE_ID");
            var sourceShotKey = CCsvParser.RequireText(row, "Review Result", rowNo, "SHOT_KEY", "HOLE_KEY");
            var shotName = CCsvParser.Get(row, "SHOT_NAME");
            var normalizedShotKey = CReviewManager.NormalizeShotKey(sourceShotKey);
            var shotKey = string.IsNullOrWhiteSpace(normalizedShotKey) ? sourceShotKey : normalizedShotKey;
            var headNo = CCsvParser.ReadRequiredInt(CCsvParser.Get(row, "HEAD_NO"), "Review Result", rowNo, "HEAD_NO");
            var cellNo = CCsvParser.ReadRequiredInt(CCsvParser.Get(row, "CELL_NO"), "Review Result", rowNo, "CELL_NO");
            var errorX = CCsvParser.ReadDouble(CCsvParser.Get(row, "ERROR_X"), "Review Result", rowNo, "ERROR_X", 0.0);
            var errorY = CCsvParser.ReadDouble(CCsvParser.Get(row, "ERROR_Y"), "Review Result", rowNo, "ERROR_Y", 0.0);
            var judge = CCsvParser.RequireText(row, "Review Result", rowNo, "JUDGE").ToUpperInvariant();
            return new ST_REVIEW_RESULT_FILE_ROW(savedAt, recipeId, shotKey, shotName, headNo, cellNo, errorX, errorY, judge);
        }

        private string GetResultPath(ST_REVIEW_RESULT_DATA result, ST_REVIEW_RESULT_METADATA metadata)
        {
            var savedAt = result.SavedAt;
            var glassId = SanitizeFileName(result.GlassId);
            var fileName = CreateResultFileName(metadata, savedAt);
            return Path.Combine(_reviewResultRoot, savedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture), glassId, NormalizeInspectionMethod(result.InspectionMethod), fileName);
        }

        private static IReadOnlyDictionary<string, string> ToRow(ST_REVIEW_RESULT_DATA result, ST_REVIEW_PLAN_POINT point, ST_REVIEW_RESULT_METADATA metadata)
        {
            var plan = result.Plan;
            var columnCount = Math.Max(1, point.PixelCountX);
            var zeroBasedShotNo = Math.Max(0, point.ShotNo - 1);
            var rowNo = (zeroBasedShotNo / columnCount) + 1;
            var columnNo = (zeroBasedShotNo % columnCount) + 1;
            var shotName = FormatShotName(point);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SAVED_AT"] = FormatDate(result.SavedAt),
                ["GLASS_ID"] = result.GlassId,
                ["RECIPE_ID"] = plan.RecipeId,
                ["INSPECTION_METHOD"] = result.InspectionMethod,
                ["RESULT_STATUS"] = NormalizeResultStatus(result.ResultStatus),
                ["TOTAL_COUNT"] = Math.Max(result.TotalCount, plan.ReviewPoints.Count).ToString(CultureInfo.InvariantCulture),
                ["RULE"] = metadata.Rule,
                ["RULE_SCOPE"] = metadata.RuleScope,
                ["BEAM_NO"] = metadata.BeamNo.ToString(CultureInfo.InvariantCulture),
                ["REFERENCE_CELL_NO"] = metadata.ReferenceCellNo?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["REFERENCE_SHOT"] = metadata.ReferenceShot,
                ["ROUGH_REFERENCE"] = metadata.RoughReference,
                ["FINE_LINE_COUNT"] = metadata.FineLineCount > 0 ? metadata.FineLineCount.ToString(CultureInfo.InvariantCulture) : "",
                ["RESULT_SOURCE"] = "MEASURED",
                ["SHOT_NAME"] = shotName,
                ["ROW"] = rowNo.ToString(CultureInfo.InvariantCulture),
                ["COLUMN"] = columnNo.ToString(CultureInfo.InvariantCulture),
                ["DESIGN_X"] = FormatDouble(point.DesignX),
                ["DESIGN_Y"] = FormatDouble(point.DesignY),
                ["SHOT_KEY"] = $"CELL{point.CellNo}_{shotName}",
                ["HEAD_NO"] = point.HeadNo.ToString(CultureInfo.InvariantCulture),
                ["CELL_NO"] = point.CellNo.ToString(CultureInfo.InvariantCulture),
                ["ERROR_X"] = FormatDouble(point.ErrorX),
                ["ERROR_Y"] = FormatDouble(point.ErrorY),
                ["JUDGE"] = point.Judge
            };
        }

        private static ST_REVIEW_RESULT_METADATA CreateMetadata(ST_REVIEW_RESULT_DATA result)
        {
            var projection = result.Plan.CorrectionPlan;
            var rule = projection?.Rule.ToString().ToUpperInvariant() ?? "ALL";
            var ruleScope = projection is null ? "" : projection.Scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell ? "HEAD_CELL" : "HEAD";
            ST_REVIEW_PLAN_POINT? referencePoint = null;
            foreach (var point in result.Plan.ReviewPoints)
            {
                referencePoint ??= point;
                if (point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng)
                {
                    referencePoint = point;
                    break;
                }
            }

            ST_REVIEW_PLAN_POINT? beamPoint = referencePoint;
            if (beamPoint is null && result.Results.Count > 0)
            {
                beamPoint = result.Results[0];
            }

            var isSimple = projection?.Rule == EN_REVIEW_CORRECTION_RULE.Simple;
            var isRough = projection?.Rule == EN_REVIEW_CORRECTION_RULE.Rough;
            var isFine = projection?.Rule == EN_REVIEW_CORRECTION_RULE.Fine;
            return new ST_REVIEW_RESULT_METADATA(
                rule, 
                ruleScope, 
                beamPoint?.BeamNo ?? 1, 
                isSimple ? referencePoint?.CellNo : null, 
                isSimple ? FormatShotName(referencePoint) : "", 
                isRough ? projection!.RoughReference.ToString().ToUpperInvariant() : "", 
                isFine ? projection!.FineLineCount : 0
            );
        }

        private static string FormatShotName(ST_REVIEW_PLAN_POINT? point)
        {
            return point is null ? "" : CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX);
        }

        private static string CreateResultFileName(ST_REVIEW_RESULT_METADATA metadata, DateTimeOffset savedAt)
        {
            var detail = metadata.Rule switch
            {
                "SIMPLE" => $"SIMPLE_Cell{metadata.ReferenceCellNo?.ToString(CultureInfo.InvariantCulture) ?? ""}_{metadata.ReferenceShot}",
                "ROUGH" => $"ROUGH_{metadata.RuleScope}_{metadata.RoughReference}",
                "FINE" => $"FINE_{metadata.FineLineCount.ToString(CultureInfo.InvariantCulture)}LINE",
                "EDGE" => "EDGE",
                _ => "ALL"
            };
            return $"ReviewResult_{SanitizeFileName(detail)}_{savedAt:HHmmss}.csv";
        }

        private static int ReadOptionalInt(IReadOnlyDictionary<string, string> row, string name, int defaultValue)
        {
            return int.TryParse(CCsvParser.Get(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;
        }

        private static string NormalizeResultStatus(string? resultStatus)
        {
            return resultStatus?.Trim().Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase) == true ? "IN_PROGRESS" : "COMPLETED";
        }

        private static string NormalizeInspectionMethod(string? inspectionMethod)
        {
            return inspectionMethod?.Trim().Equals("IOF", StringComparison.OrdinalIgnoreCase) == true ? "IOF" : "P2P";
        }

        private static int? ReadOptionalNullableInt(IReadOnlyDictionary<string, string> row, string name)
        {
            return int.TryParse(CCsvParser.Get(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        private static string FormatDate(DateTimeOffset? value)
        {
            return value?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "";
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string (value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "UNLINKED" : sanitized;
        }

        private sealed record ST_REVIEW_RESULT_METADATA(string Rule, string RuleScope, int BeamNo, int? ReferenceCellNo, string ReferenceShot, string RoughReference, int FineLineCount);
#endregion
#region BeamInspection
        public override Task<string> SaveBeamInspection(ST_BEAM_INSPECTION_SNAPSHOT result, string glassId, DateTimeOffset savedAt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Points.Count == 0 || result.Points.Any(p => p.BeamNo is < 1 or > 16))
                throw new InvalidDataException("Beam Inspection 결과에는 1~16번 Beam이 필요합니다.");
            string[] headers = ["GLASS_NAME", "HEAD_NAME", "CELL_NAME", "SHOT_NAME", ..Enumerable.Range(1, 16).SelectMany(beam => new[] { $"BEAM{beam}_X_MM", $"BEAM{beam}_Y_MM", $"BEAM{beam}_JUDGE" })];
            var rows = result.Points.GroupBy(p => (p.HeadNo, p.CellNo, p.ShotNo)).OrderBy(g => g.Key.HeadNo).ThenBy(g => g.Key.CellNo).ThenBy(g => g.Key.ShotNo).Select(group =>
            {
                var point = group.First();
                var beams = group.ToDictionary(p => p.BeamNo);
                var row = new Dictionary<string, string>
                {
                    ["GLASS_NAME"] = string.IsNullOrWhiteSpace(glassId) ? "UNLINKED" : glassId,
                    ["HEAD_NAME"] = $"HEAD {point.HeadNo:00}",
                    ["CELL_NAME"] = $"CELL {point.CellNo:00}",
                    ["SHOT_NAME"] = CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX)
                };
                for (var beam = 1; beam <= 16; beam++)
                {
                    beams.TryGetValue(beam, out var measurement);
                    var measured = measurement?.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng;
                    // Store the same measured X/Y deviations (mm) shown in the inspection view.
                    row[$"BEAM{beam}_X_MM"] = measured ? FormatDouble(measurement!.ErrorX) : "미검사";
                    row[$"BEAM{beam}_Y_MM"] = measured ? FormatDouble(measurement!.ErrorY) : "미검사";
                    row[$"BEAM{beam}_JUDGE"] = !measured ? "미검사" : measurement!.State == EN_REVIEW_POINT_STATE.Ok ? "OK" : "NG";
                }

                return row;
            }).ToArray();
            var root = Path.Combine(Path.GetDirectoryName(_reviewResultRoot)!, "BeamInspection");
            if (result.IsSimulation)
                root = Path.Combine(root, "SIM");
            var directory = Path.Combine(root, savedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var fileName = $"BeamInspection_{savedAt:HHmmss_fff}";
            var path = Path.Combine(directory, fileName + ".csv");
            var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                CCsvParser.Write(temporaryPath, headers, rows);
                for (var duplicate = 1;; duplicate++)
                {
                    try
                    {
                        System.IO.File.Move(temporaryPath, path);
                        break;
                    }
                    catch (IOException)when (System.IO.File.Exists(path))
                    {
                        path = Path.Combine(directory, $"{fileName}_{duplicate}.csv");
                    }
                }
            }
            finally
            {
                if (System.IO.File.Exists(temporaryPath))
                    System.IO.File.Delete(temporaryPath);
            }

            return Task.FromResult(Path.GetFullPath(path));
        }

        public override Task<ST_BEAM_INSPECTION_RESULT_FILE_DATA?> LoadLastBeamInspection(CancellationToken cancellationToken = default) => Task.Run(() => LoadLastBeamInspectionCore(cancellationToken), cancellationToken);
        private ST_BEAM_INSPECTION_RESULT_FILE_DATA? LoadLastBeamInspectionCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(Path.GetDirectoryName(_reviewResultRoot)!, "BeamInspection");
            if (!Directory.Exists(root))
                return null;
            FileInfo? latest;
            try
            {
                // Filename suffixes are intentionally unrestricted: old files include completion/GUID suffixes.
                latest = Directory.EnumerateFiles(root, "*.csv", System.IO.SearchOption.AllDirectories).Where(path => Path.GetFileName(path).StartsWith("BeamInspection_", StringComparison.OrdinalIgnoreCase)).Select(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new FileInfo(path);
                }).OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(file => file.FullName, StringComparer.Ordinal).FirstOrDefault();
            }
            catch (Exception exception)when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"최근 Beam Inspection 결과를 검색하지 못했습니다.\n경로: {root}\n{exception.Message}", exception);
            }

            if (latest is null)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // TextFieldParser checks row widths and preserves quoted names, including embedded commas/newlines.
                using var parser = new TextFieldParser(latest.FullName, new UTF8Encoding(false, true), detectEncoding: true)
                {
                    HasFieldsEnclosedInQuotes = true,
                    TrimWhiteSpace = false
                };
                parser.SetDelimiters(",");
                var headers = parser.ReadFields()?.Select(header => header.Trim()).ToArray();
                if (headers is null || headers.Length != BeamInspectionHeaders.Length || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != BeamInspectionHeaders.Length || !headers.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(BeamInspectionHeaders))
                    throw new InvalidDataException("CSV 헤더에는 GLASS_NAME/HEAD_NAME/CELL_NAME/SHOT_NAME과 1~16번 Beam의 X_MM/Y_MM/JUDGE, 총 52개 열이 필요합니다.");
                var indexes = headers.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
                var shots = new List<ST_BEAM_INSPECTION_RESULT_SHOT>();
                var identities = new HashSet<(int Head, int Cell, string Shot)>();
                string? glassName = null;
                while (!parser.EndOfData)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rowNo = parser.LineNumber;
                    var fields = parser.ReadFields();
                    if (fields is null)
                        break;
                    if (fields.Length != BeamInspectionHeaders.Length)
                        throw Error(rowNo, $"열 개수가 {fields.Length}개입니다. 각 Shot 행에는 52개 열이 필요합니다.");
                    string Get(string name) => fields[indexes[name]];
                    string RequireName(string name)
                    {
                        var value = Get(name);
                        if (string.IsNullOrWhiteSpace(value))
                            throw Error(rowNo, $"{name} 값이 없습니다.");
                        return value;
                    }

                    var glass = RequireName("GLASS_NAME");
                    if (glassName is not null && !glass.Equals(glassName, StringComparison.Ordinal))
                        throw Error(rowNo, "한 결과 파일의 GLASS_NAME은 모두 같아야 합니다.");
                    glassName ??= glass;
                    var headName = RequireName("HEAD_NAME");
                    var cellName = RequireName("CELL_NAME");
                    var shotName = RequireName("SHOT_NAME");
                    var headNo = ReadNameNumber(headName, "HEAD", rowNo);
                    var cellNo = ReadNameNumber(cellName, "CELL", rowNo);
                    if (headNo > 8)
                        throw Error(rowNo, "HEAD_NAME의 Head 번호는 1~8이어야 합니다.");
                    if (!identities.Add((headNo, cellNo, shotName)))
                        throw Error(rowNo, $"동일한 Head/Cell/Shot이 중복되었습니다. {headName} / {cellName} / {shotName}");
                    var beams = new List<ST_BEAM_INSPECTION_RESULT_BEAM>(16);
                    for (var beam = 1; beam <= 16; beam++)
                    {
                        var x = Get($"BEAM{beam}_X_MM").Trim();
                        var y = Get($"BEAM{beam}_Y_MM").Trim();
                        var judge = Get($"BEAM{beam}_JUDGE").Trim().ToUpperInvariant();
                        if (judge == "미검사")
                        {
                            if (x != "미검사" || y != "미검사")
                                throw Error(rowNo, $"Beam {beam}: 미검사 결과의 X/Y/JUDGE는 모두 '미검사'여야 합니다.");
                            beams.Add(new(beam, null, null, "미검사"));
                        }
                        else if (judge is "OK" or "NG")
                        {
                            beams.Add(new(beam, ReadFinite(x, rowNo, beam, "X_MM"), ReadFinite(y, rowNo, beam, "Y_MM"), judge));
                        }
                        else
                            throw Error(rowNo, $"Beam {beam}: JUDGE는 OK, NG 또는 미검사여야 합니다. 입력: {judge}");
                    }

                    shots.Add(new(glass, headName, headNo, cellName, cellNo, shotName, beams.AsReadOnly()));
                }

                if (shots.Count == 0)
                    throw new InvalidDataException("CSV에 Shot 결과 행이 없습니다.");
                var relative = Path.GetRelativePath(root, latest.FullName);
                var simulation = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals("SIM", StringComparison.OrdinalIgnoreCase));
                return new(latest.FullName, latest.Name, new DateTimeOffset(latest.LastWriteTimeUtc).ToLocalTime(), simulation, glassName!, shots.AsReadOnly());
            }
            catch (Exception exception)when (exception is InvalidDataException or IOException or UnauthorizedAccessException or MalformedLineException or DecoderFallbackException)
            {
                // Never skip a damaged newest file and silently present an older run as the latest result.
                throw new InvalidDataException($"최근 Beam Inspection 결과를 불러오지 못했습니다.\n파일: {latest.FullName}\n{exception.Message}", exception);
            }
        }

        private static int ReadNameNumber(string name, string prefix, long rowNo)
        {
            var parts = name.Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !parts[0].Equals(prefix, StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
                throw Error(rowNo, $"{prefix}_NAME 형식이 잘못되었습니다. 예: {prefix} 01. 입력: {name}");
            return number;
        }

        private static double ReadFinite(string text, long rowNo, int beam, string name)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw Error(rowNo, $"Beam {beam}: {name}은 유한한 숫자여야 합니다. 소수점은 '.'을 사용하세요. 입력: {text}");
            return value;
        }

        private static InvalidDataException Error(long rowNo, string detail) => new($"CSV {rowNo}행: {detail}");
#endregion
#region Correction
        public override Task<string> SaveFineCorrection(ST_FINE_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = result.Snapshot;
            if (snapshot.RecipeId != result.Request.Plan.RecipeId || snapshot.Lines.Count == 0)
                throw new InvalidDataException("FINE 검사 결과 대상 또는 레시피가 올바르지 않습니다.");
            var status = snapshot.State switch
            {
                EN_REVIEW_SEQUENCE_STATE.Completed => "COMPLETED",
                EN_REVIEW_SEQUENCE_STATE.Stopped => snapshot.IsNgPause ? "NG_PAUSED" : "STOPPED",
                EN_REVIEW_SEQUENCE_STATE.Failed => "FAILED",
                _ => throw new InvalidDataException("FINE 검사 종료 상태가 아닙니다.")};
            string[] headers = [
                "SAVED_AT", 
                "RECIPE_ID", 
                "GLASS_ID", 
                "ORIGIN", 
                "METHOD", 
                "IMMEDIATE_RETRY", 
                "RETRY_LIMIT", 
                "CAPTURE_ATTEMPT", 
                "RUN_TYPE", 
                "START_LINE", 
                "RESULT_STATUS", 
                "RESULT_MESSAGE", 
                "STAGE_SIMULATION", 
                "VISION_SIMULATION", 
                "INSPECTION_LINE", 
                "MODEL_NO", 
                "DIRECTION", 
                "LINE_STATE", 
                "HEAD_NO", 
                "CELL_NO", 
                "SHOT_NAME", 
                "SHOT_KEY", 
                "BEAM_NO", 
                "LINE_NO", 
                "INDEX_X", 
                "INDEX_Y", 
                "DESIGN_X", 
                "DESIGN_Y", 
                "REVIEW_X_TARGET", 
                "STAGE_Y_TARGET", 
                "VISION_X_FLIP", 
                "VISION_Y_FLIP", 
                "VISION_XY_FLIP", 
                "RECV_TARGET_MINUS_HOLE_X", 
                "RECV_TARGET_MINUS_HOLE_Y", 
                "VISION_FLIP_CONVERT_X", 
                "VISION_FLIP_CONVERT_Y", 
                "RESULT_SOURCE", 
                "JUDGE"
            ];
            var plan = result.Request.Plan;
            var rows = snapshot.Lines.SelectMany(line => line.Points.Select(point =>
            {
                var measured = point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng;
                var converted = CReviewManager.VisionErrorToStageOffset(point.ErrorX, point.ErrorY, plan.VisionFlipType);
                var indexX = (point.ShotNo - 1) % Math.Max(1, point.PixelCountX) + 1;
                var indexY = (point.ShotNo - 1) / Math.Max(1, point.PixelCountX) + 1;
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["SAVED_AT"] = FormatDate(result.SavedAt),
                    ["RECIPE_ID"] = snapshot.RecipeId,
                    ["GLASS_ID"] = result.Request.Context?.GlassId ?? "-",
                    ["ORIGIN"] = result.Request.Context?.ZeroLineFollowUp == true ? "AUTO ZERO_LINE_FOLLOWUP (INSPECTION ONLY)" : result.Request.Context?.Origin ?? "MANUAL FINE",
                    ["METHOD"] = result.Request.Method == EN_FINE_INSPECTION_METHOD.Flying ? "FLYING" : "P2P",
                    ["IMMEDIATE_RETRY"] = result.Request.ImmediateRetry ? "ON" : "OFF",
                    ["RETRY_LIMIT"] = Int(result.Request.CountMismatchRetries),
                    ["CAPTURE_ATTEMPT"] = Int(result.CaptureAttempts.GetValueOrDefault(point.ShotKey)),
                    ["RUN_TYPE"] = result.Request.IsResume ? "RESUME_NG" : "INITIAL",
                    ["START_LINE"] = Int(result.Request.StartLineNo),
                    ["RESULT_STATUS"] = status,
                    ["RESULT_MESSAGE"] = snapshot.Message,
                    ["STAGE_SIMULATION"] = result.IsStageSimulation ? "ON" : "OFF",
                    ["VISION_SIMULATION"] = result.IsVisionSimulation ? "ON" : "OFF",
                    ["INSPECTION_LINE"] = Int(line.Number),
                    ["MODEL_NO"] = Int(line.ModelNo),
                    ["DIRECTION"] = line.Points[0].ReviewTargetX <= line.Points[^1].ReviewTargetX ? "X+" : "X-",
                    ["LINE_STATE"] = line.State.ToString().ToUpperInvariant(),
                    ["HEAD_NO"] = Int(point.HeadNo),
                    ["CELL_NO"] = Int(point.CellNo),
                    ["SHOT_NAME"] = CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX),
                    ["SHOT_KEY"] = point.ShotKey,
                    ["BEAM_NO"] = Int(point.BeamNo),
                    ["LINE_NO"] = Int(indexY),
                    ["INDEX_X"] = Int(indexX),
                    ["INDEX_Y"] = Int(indexY),
                    ["DESIGN_X"] = Number(point.DesignX),
                    ["DESIGN_Y"] = Number(point.DesignY),
                    ["REVIEW_X_TARGET"] = Number(point.ReviewTargetX),
                    ["STAGE_Y_TARGET"] = Number(point.ReviewTargetY),
                    ["VISION_X_FLIP"] = plan.VisionFlipType.HasFlag(EN_VISION_FLIP_TYPE.XFlip) ? "ON" : "OFF",
                    ["VISION_Y_FLIP"] = plan.VisionFlipType.HasFlag(EN_VISION_FLIP_TYPE.YFlip) ? "ON" : "OFF",
                    ["VISION_XY_FLIP"] = plan.VisionFlipType.HasFlag(EN_VISION_FLIP_TYPE.XyFlip) ? "ON" : "OFF",
                    ["RECV_TARGET_MINUS_HOLE_X"] = measured ? Number(point.ErrorX) : "미검사",
                    ["RECV_TARGET_MINUS_HOLE_Y"] = measured ? Number(point.ErrorY) : "미검사",
                    ["VISION_FLIP_CONVERT_X"] = measured ? Number(converted.X) : "미검사",
                    ["VISION_FLIP_CONVERT_Y"] = measured ? Number(converted.Y) : "미검사",
                    ["RESULT_SOURCE"] = measured ? result.AttemptedShotKeys.Contains(point.ShotKey) ? "MEASURED" : "RETAINED" : point.Judge == "ERROR" ? "ERROR" : "UNMEASURED",
                    ["JUDGE"] = measured ? point.State == EN_REVIEW_POINT_STATE.Ok ? "OK" : "NG" : point.Judge == "ERROR" ? "ERROR" : "미검사"
                };
            })).ToArray();
            var directory = Path.Combine(_reviewResultRoot, "FINE", SanitizeFileName(plan.RecipeId), result.SavedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var name = result.SavedAt.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, name + ".csv");
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                CCsvParser.Write(temporary, headers, rows);
                for (var duplicate = 1;; duplicate++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        System.IO.File.Move(temporary, path, overwrite: false);
                        break;
                    }
                    catch (IOException)when (System.IO.File.Exists(path))
                    {
                        path = Path.Combine(directory, $"{name}_{duplicate}.csv");
                    }
                }
            }
            finally
            {
                if (System.IO.File.Exists(temporary))
                    System.IO.File.Delete(temporary);
            }

            _lastSavedPath = Path.GetFullPath(path);
            return Task.FromResult(_lastSavedPath);
        }

        public override Task<string> SaveRoughCorrection(ST_ROUGH_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Snapshot.Points.Count == 0 || result.Snapshot.RecipeId != result.Plan.RecipeId)
                throw new InvalidDataException("ROUGH 검사 결과의 대상 또는 레시피가 올바르지 않습니다.");
            string[] headers = [
                "SAVED_AT", 
                "RECIPE_ID", 
                "RUN_TYPE", 
                "RULE_SCOPE", 
                "RESULT_STATUS", 
                "RESULT_MESSAGE", 
                "STAGE_SIMULATION", 
                "VISION_SIMULATION", 
                "TOTAL_COUNT", 
                "POINT_NO", 
                "HEAD_NO", 
                "CELL_NO", 
                "SHOT_NAME", 
                "SHOT_KEY", 
                "BEAM_NO", 
                "LINE_NO", 
                "INDEX_X", 
                "INDEX_Y", 
                "DESIGN_X", 
                "DESIGN_Y", 
                "REVIEW_X_TARGET", 
                "STAGE_Y_TARGET", 
                "VISION_X_FLIP", 
                "VISION_Y_FLIP", 
                "VISION_XY_FLIP", 
                "RECV_TARGET_MINUS_HOLE_X", 
                "RECV_TARGET_MINUS_HOLE_Y", 
                "VISION_FLIP_CONVERT_X", 
                "VISION_FLIP_CONVERT_Y", 
                "RESULT_SOURCE", 
                "JUDGE"
            ];
            var flip = result.Plan.VisionFlipType;
            var status = result.Snapshot.State switch
            {
                EN_REVIEW_SEQUENCE_STATE.Completed => "COMPLETED",
                EN_REVIEW_SEQUENCE_STATE.Stopped => "STOPPED",
                EN_REVIEW_SEQUENCE_STATE.Failed => "FAILED",
                _ => throw new InvalidDataException("ROUGH 검사 종료 상태가 아닙니다.")};
            var rows = result.Snapshot.Points.Select(point =>
            {
                var measured = point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng;
                var error = point.Judge == "ERROR";
                var design = CReviewManager.VisionErrorToStageOffset(point.ErrorX, point.ErrorY, flip);
                var x = (Math.Max(1, point.ShotNo) - 1) % Math.Max(1, point.PixelCountX) + 1;
                var y = (Math.Max(1, point.ShotNo) - 1) / Math.Max(1, point.PixelCountX) + 1;
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["SAVED_AT"] = FormatDate(result.SavedAt),
                    ["RECIPE_ID"] = result.Plan.RecipeId,
                    ["RUN_TYPE"] = result.IsNgRetry ? "NG_RETRY" : "INITIAL",
                    ["RULE_SCOPE"] = result.Plan.CorrectionPlan?.Scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell ? "HEAD_CELL" : "HEAD",
                    ["RESULT_STATUS"] = status,
                    ["RESULT_MESSAGE"] = result.Snapshot.Message,
                    ["STAGE_SIMULATION"] = result.IsStageSimulation ? "ON" : "OFF",
                    ["VISION_SIMULATION"] = result.IsVisionSimulation ? "ON" : "OFF",
                    ["TOTAL_COUNT"] = Int(result.Snapshot.Points.Count),
                    ["POINT_NO"] = Int(point.PointNo),
                    ["HEAD_NO"] = Int(point.HeadNo),
                    ["CELL_NO"] = Int(point.CellNo),
                    ["SHOT_NAME"] = CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX),
                    ["SHOT_KEY"] = point.ShotKey,
                    ["BEAM_NO"] = Int(point.BeamNo),
                    ["LINE_NO"] = Int(y),
                    ["INDEX_X"] = Int(x),
                    ["INDEX_Y"] = Int(y),
                    ["DESIGN_X"] = Number(point.DesignX),
                    ["DESIGN_Y"] = Number(point.DesignY),
                    ["REVIEW_X_TARGET"] = Number(point.ReviewTargetX),
                    ["STAGE_Y_TARGET"] = Number(point.ReviewTargetY),
                    ["VISION_X_FLIP"] = flip.HasFlag(EN_VISION_FLIP_TYPE.XFlip) ? "ON" : "OFF",
                    ["VISION_Y_FLIP"] = flip.HasFlag(EN_VISION_FLIP_TYPE.YFlip) ? "ON" : "OFF",
                    ["VISION_XY_FLIP"] = flip.HasFlag(EN_VISION_FLIP_TYPE.XyFlip) ? "ON" : "OFF",
                    ["RECV_TARGET_MINUS_HOLE_X"] = measured ? Number(point.ErrorX) : "미검사",
                    ["RECV_TARGET_MINUS_HOLE_Y"] = measured ? Number(point.ErrorY) : "미검사",
                    ["VISION_FLIP_CONVERT_X"] = measured ? Number(design.X) : "미검사",
                    ["VISION_FLIP_CONVERT_Y"] = measured ? Number(design.Y) : "미검사",
                    ["RESULT_SOURCE"] = measured ? result.AttemptedShotKeys.Contains(point.ShotKey) ? "MEASURED" : "RETAINED" : error ? "ERROR" : "UNMEASURED",
                    ["JUDGE"] = measured ? point.State == EN_REVIEW_POINT_STATE.Ok ? "OK" : "NG" : error ? "ERROR" : "미검사"
                };
            }).ToArray();
            var directory = Path.Combine(_reviewResultRoot, "ROUGH", SanitizeFileName(result.Plan.RecipeId), result.SavedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var name = result.SavedAt.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, name + ".csv");
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                CCsvParser.Write(temporary, headers, rows);
                for (var duplicate = 1;; duplicate++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        System.IO.File.Move(temporary, path, overwrite: false);
                        break;
                    }
                    catch (IOException)when (System.IO.File.Exists(path))
                    {
                        path = Path.Combine(directory, $"{name}_{duplicate}.csv");
                    }
                }
            }
            finally
            {
                if (System.IO.File.Exists(temporary))
                    System.IO.File.Delete(temporary);
            }

            var fullPath = Path.GetFullPath(path);
            _lastSavedPath = fullPath;
            return Task.FromResult(fullPath);
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Number(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.000", CultureInfo.InvariantCulture);
        public override async Task<string> SaveZeroLine(ST_ZERO_LINE_RESULT_DATA data, CancellationToken token = default)
        {
            var directory = Path.Combine(Directory.GetParent(Path.GetFullPath(configRoot))!.FullName, "Data", "ZeroDefence", data.SavedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"ZeroDefence_{data.SavedAt:HHmmss_fffffff}_{data.Plan.RunId:N}.csv");
            static string Field(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
            var lines = new List<string>
            {
                "SavedAt,RunId,Recipe,Glass,Group,ResultStatus,GroupJudge,Head,Cell,Shot,Beam,ReviewX,StageY,State,Resolution,TargetX,TargetY,HoleX,HoleY,RecvX,RecvY,ConvertedX,ConvertedY,Judge,Detail,StageSimulation,VisionSimulation,ToleranceX,ToleranceY,VisionFlip"
            };
            var results = data.Status.Results ?? throw new InvalidDataException("0선 결과가 없습니다.");
            foreach (var result in results)
            {
                var p = result.Point;
                var converted = CReviewManager.VisionErrorToStageOffset(p.ErrorX, p.ErrorY, data.Plan.VisionPlan.VisionFlipType);
                var measured = result.Mark.HasValue;
                lines.Add(string.Join(",", new object? [] { data.SavedAt.ToString("O"), data.Plan.RunId, data.Plan.RecipeId, data.Plan.GlassId, data.Plan.Group, data.Status.State, data.Status.Judge, p.HeadNo, p.CellNo, CReviewShotNameFormatter.ToMatrixName(p.ShotNo, p.PixelCountX), p.BeamNo, p.ReviewTargetX, p.ReviewTargetY, result.State, result.Resolution, result.Mark?.TargetMarkX, result.Mark?.TargetMarkY, result.Mark?.HoleX, result.Mark?.HoleY, measured ? p.ErrorX : null, measured ? p.ErrorY : null, measured ? converted.X : null, measured ? converted.Y : null, p.Judge, result.Detail, data.StageSimulation, data.VisionSimulation, data.Plan.VisionPlan.ToleranceX, data.Plan.VisionPlan.ToleranceY, data.Plan.VisionPlan.VisionFlipType }.Select(Field)));
            }

            // Unique run ID and atomic publication prevent a partial result file appearing as complete.
            var temporary = path + ".tmp";
            try
            {
                await System.IO.File.WriteAllLinesAsync(temporary, lines, new UTF8Encoding(true), token).ConfigureAwait(false);
                System.IO.File.Move(temporary, path);
            }
            finally
            {
                if (System.IO.File.Exists(temporary))
                    System.IO.File.Delete(temporary);
            }

            return path;
        }
#endregion
    }
}
