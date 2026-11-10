namespace Drilling.UI.Menu.Menus
{
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Windows;
    using System.Windows.Media;
    using Drilling.Common.Managers;
    using Drilling.Common.Recipe;
    using Drilling.Common.Review;
    using Drilling.UI.Menu;
    using Drilling.UI.Popup;

    public sealed partial class CMenuCorrection
    {
        public sealed partial class CReviewDataPanel
        {
            private bool _isOffsetBusy;

            public bool CanEditOffsets => HasLoadedData && !_isOffsetBusy && !_isDataLoading && !IsBusy;
            public bool CanApplyOffset => CanEditOffsets
                && _loadedCatalog is { } catalog
                && IsOffsetApplicableResultType(catalog.ResultType)
                && catalog.ResultStatus.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);

            public string OffsetActionHint
            {
                get
                {
                    if (!HasLoadedData || _loadedCatalog is null)
                    {
                        return "검사 결과를 DATA LOAD한 뒤 Review Offset 작업을 사용할 수 있습니다.";
                    }

                    if (!_loadedCatalog.ResultStatus.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"OFFSET APPLY 불가능. 사유 : 완료된 결과가 아닙니다. ({_loadedCatalog.ResultStatus})";
                    }

                    return _loadedCatalog.ResultType.ToUpperInvariant() switch
                    {
                        "ALL_SHOT" => "ALL SHOT의 측정 OK 값을 같은 Shot의 Review Offset에 직접 누적합니다.",
                        "ONE_SHOT" => "ONE SHOT의 측정 OK 값을 선택 Shot의 Review Offset에 직접 누적합니다.",
                        "ROUGH" => "ROUGH 결과의 HEAD 또는 HEAD + CELL 범위에 측정 OK 값을 누적합니다.",
                        "FINE" => "FINE 측정값을 Cell 열별로 보간하여 Review Offset에 누적합니다.",
                        "AUTO" => "AUTO 검사 측정값을 Cell 열별로 보간하여 Review Offset에 누적합니다.",
                        "BEAM_INSPECTION" => "OFFSET APPLY 불가능. 사유 : BEAM INSPECTION은 Beam 상태 확인 결과입니다.",
                        "ZERO_DEFENCE" or "FOLLOWUP" => "OFFSET APPLY 불가능. 사유 : Zero Defence 결과는 Review Offset 적용 대상이 아닙니다.",
                        _ => $"OFFSET APPLY 불가능. 사유 : 지원하지 않는 결과 종류입니다. ({_loadedCatalog.ResultType})"
                    };
                }
            }

            private async void OffsetApplyRequested(object? parameter)
            {
                await ShowOffsetApplyDialog();
            }

            private async void OffsetInitRequested(object? parameter)
            {
                await InitializeReviewOffsets();
            }

            private async void OffsetRollbackRequested(object? parameter)
            {
                await _rollbackReviewOffset();
            }

            private async void OffsetViewRequested(object? parameter)
            {
                await ShowCurrentReviewOffsets();
            }

            private async Task ShowOffsetApplyDialog()
            {
                if (!CanApplyOffset || _loadedCatalog is null || _loadedResultFile is null || _loadedRecipe is null)
                {
                    return;
                }

                var sourcePath = _loadedResultFile.FilePath;
                var recipe = _loadedRecipe;
                var catalog = _loadedCatalog;
                var result = _loadedResultFile;
                var parameters = new Dictionary<string, string>(_loadedParameters, StringComparer.OrdinalIgnoreCase);
                SetOffsetBusy(true);
                try
                {
                    var latest = await Task.Run(() => _reviewOffsetFile.LoadLatest(recipe.Id));
                    var plan = await Task.Run(() => BuildOffsetApplyPlan(catalog, result, recipe, parameters, latest));
                    EnsureLoadedResultUnchanged(sourcePath, recipe.Id);

                    var panel = new CReviewDataOffsetApplyPanel(plan);
                    var dialog = new CReviewDataOffsetApplyDialog(panel)
                    {
                        Owner = Application.Current?.MainWindow
                    };
                    if (dialog.ShowDialog() != true)
                    {
                        _statusReporter("Review Data Offset Apply를 취소했습니다.");
                        return;
                    }

                    EnsureLoadedResultUnchanged(sourcePath, recipe.Id);
                    var current = await Task.Run(() => _reviewOffsetFile.LoadLatest(recipe.Id));
                    if (!SameOffsetSource(latest, current))
                    {
                        throw new InvalidOperationException("팝업을 확인하는 동안 Review Offset 파일이 변경되었습니다. DATA LOAD 상태는 유지되므로 OFFSET APPLY를 다시 열어 확인해 주세요.");
                    }

                    var saved = await Task.Run(() => _reviewOffsetFile.SaveNew(recipe.Id, plan.SavedRows));
                    var message = $"{plan.ApplicationType.Name} / {plan.Rows.Count:N0}개 Shot의 Review Offset을 누적 적용했습니다.\n{saved.FileName}";
                    _statusReporter(message.Replace(Environment.NewLine, " "));
                    CRecipeConfirmDialog.ShowCompletion("Review Data Offset Apply 완료", message);
                }
                catch (Exception exception)
                {
                    _statusReporter($"Review Data Offset Apply: {exception.Message}");
                    CRecipeConfirmDialog.ShowWarning("Review Data Offset Apply 확인", exception.Message);
                }
                finally
                {
                    SetOffsetBusy(false);
                }
            }

            private async Task InitializeReviewOffsets()
            {
                if (!CanEditOffsets || _loadedRecipe is null || _loadedResultFile is null)
                {
                    return;
                }

                var sourcePath = _loadedResultFile.FilePath;
                var recipe = _loadedRecipe;
                SetOffsetBusy(true);
                try
                {
                    var points = await Task.Run(() => _reviewManager.CreateAllPlan(recipe, 1).Points
                        .Where(point => point.HeadNo > 0)
                        .ToArray());
                    if (points.Length == 0)
                    {
                        throw new InvalidOperationException("현재 레시피에 Review Offset을 초기화할 Shot이 없습니다.");
                    }

                    if (!CRecipeConfirmDialog.ShowConfirmation(
                            "Review Offset Init",
                            $"현재 레시피: {recipe.Name}\n대상: {points.Select(point => point.CellNo).Distinct().Count():N0} Cells / {points.Length:N0} Shots\n\n기존 Review Offset X/Y를 모두 0.000으로 초기화하시겠습니까?\n새 REVIEW OFFSET 파일을 생성합니다.",
                            "초기화",
                            useDangerStyle: true))
                    {
                        return;
                    }

                    EnsureLoadedResultUnchanged(sourcePath, recipe.Id);
                    var saved = await Task.Run(async () =>
                    {
                        var latest = await _reviewOffsetFile.LoadLatest(recipe.Id);
                        var rows = new Dictionary<(int Cell, string Shot), ST_REVIEW_OFFSET_ROW>();
                        foreach (var row in latest.Rows)
                        {
                            var shot = NormalizeReviewOffsetShotId(row.ShotId);
                            if (row.CellId > 0 && shot.Length > 0)
                            {
                                rows[(row.CellId, shot)] = new ST_REVIEW_OFFSET_ROW(row.CellId, shot, 0.0, 0.0)
                                {
                                    IsDesignCoordinate = true
                                };
                            }
                        }

                        foreach (var point in points)
                        {
                            var shot = NormalizeReviewOffsetShotId(CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX));
                            rows[(point.CellNo, shot)] = new ST_REVIEW_OFFSET_ROW(point.CellNo, shot, 0.0, 0.0)
                            {
                                IsDesignCoordinate = true
                            };
                        }

                        return await _reviewOffsetFile.SaveNew(
                            recipe.Id,
                            rows.Values.OrderBy(row => row.CellId).ThenBy(row => row.ShotId, StringComparer.OrdinalIgnoreCase).ToArray());
                    });
                    var message = $"{points.Length:N0}개 Shot의 Review Offset을 0.000으로 초기화했습니다.\n{saved.FileName}";
                    _statusReporter(message.Replace(Environment.NewLine, " "));
                    CRecipeConfirmDialog.ShowCompletion("Review Offset Init 완료", message);
                }
                catch (Exception exception)
                {
                    _statusReporter($"Review Offset Init: {exception.Message}");
                    CRecipeConfirmDialog.ShowWarning("Review Offset Init 확인", exception.Message);
                }
                finally
                {
                    SetOffsetBusy(false);
                }
            }

            private async Task ShowCurrentReviewOffsets()
            {
                if (!CanEditOffsets || _loadedRecipe is null || _loadedResultFile is null)
                {
                    return;
                }

                var sourcePath = _loadedResultFile.FilePath;
                var recipe = _loadedRecipe;
                SetOffsetBusy(true);
                try
                {
                    var saved = await Task.Run(() => _reviewOffsetFile.LoadLatest(recipe.Id));
                    EnsureLoadedResultUnchanged(sourcePath, recipe.Id);
                    var parameters = new Dictionary<string, string>(_loadedParameters, StringComparer.OrdinalIgnoreCase);
                    CReviewOffsetData.ApplyToParameters(parameters, saved);
                    var cellCount = ReadOutputViewerInt(parameters, 0, "MAX_CELL_NUMBER");
                    if (cellCount <= 0)
                    {
                        throw new InvalidDataException("현재 레시피의 MAX_CELL_NUMBER를 확인할 수 없습니다.");
                    }

                    var viewer = new CAlignOffsetViewerPanel(
                        recipe,
                        parameters,
                        cellNo => BuildOutputViewerGlassPreview(parameters, cellCount, cellNo),
                        reviewOffsets: true);
                    new CAlignOffsetViewerDialog(viewer)
                    {
                        Owner = Application.Current?.MainWindow
                    }.ShowDialog();
                }
                catch (Exception exception)
                {
                    _statusReporter($"Cur Review Offset View: {exception.Message}");
                    CRecipeConfirmDialog.ShowWarning("Cur Review Offset View 확인", exception.Message);
                }
                finally
                {
                    SetOffsetBusy(false);
                }
            }

            private ST_REVIEW_OFFSET_APPLY_PLAN BuildOffsetApplyPlan(
                ST_REVIEW_RESULT_CATALOG_ITEM catalog,
                ST_REVIEW_RESULT_FILE_DATA result,
                ST_RECIPE_DEFINITION recipe,
                IReadOnlyDictionary<string, string> parameters,
                ST_REVIEW_OFFSET_FILE_DATA latest)
            {
                if (!catalog.ResultStatus.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)
                    || !result.ResultStatus.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("COMPLETED 상태의 검사 결과만 Offset에 적용할 수 있습니다.");
                }

                var beamNo = result.BeamNo > 0 ? result.BeamNo : result.Rows.Select(row => row.BeamNo).FirstOrDefault(value => value > 0);
                beamNo = Math.Max(1, beamNo);
                var allPlan = _reviewManager.CreateAllPlan(recipe, beamNo);
                // Match AUTO FINE: excluded MASKING/CHESS shots stay visible in the map,
                // but never receive an interpolated Review Offset.
                var allPoints = allPlan.Points.Where(point => point.HeadNo > 0 && point.Use).ToArray();
                if (allPoints.Length == 0)
                {
                    throw new InvalidOperationException("현재 레시피에 Head가 배정된 Shot이 없습니다.");
                }

                var pointLookup = BuildPointLookup(allPoints);
                var resultType = catalog.ResultType.Trim().ToUpperInvariant();
                var applicationType = CreateApplicationType(resultType, result);
                IReadOnlyList<ST_REVIEW_OFFSET_DELTA> deltas = resultType switch
                {
                    "ALL_SHOT" or "ONE_SHOT" => BuildDirectDeltas(result.Rows, pointLookup),
                    "ROUGH" => BuildRoughDeltas(result, allPoints, pointLookup),
                    "FINE" or "AUTO" => BuildFineDeltas(result, allPoints, pointLookup),
                    _ => throw new InvalidOperationException($"{catalog.ResultType} 결과는 Review Offset 적용을 지원하지 않습니다.")
                };
                if (deltas.Count == 0)
                {
                    throw new InvalidOperationException("검사 결과에서 Offset을 적용할 수 있는 OK Shot을 찾지 못했습니다.");
                }

                var latestByKey = new Dictionary<(int Cell, string Shot), ST_REVIEW_OFFSET_ROW>();
                foreach (var row in latest.Rows)
                {
                    var shot = NormalizeReviewOffsetShotId(row.ShotId);
                    if (row.CellId > 0 && shot.Length > 0)
                    {
                        latestByKey[(row.CellId, shot)] = row with { ShotId = shot };
                    }
                }

                var displayRows = new List<ST_REVIEW_OFFSET_APPLY_GRID_ROW>(deltas.Count);
                var seen = new HashSet<(int Cell, string Shot)>();
                foreach (var delta in deltas)
                {
                    var point = delta.Point;
                    var shotName = NormalizeReviewOffsetShotId(CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX));
                    var key = (point.CellNo, shotName);
                    if (!seen.Add(key))
                    {
                        throw new InvalidDataException($"Offset 적용 대상 Shot이 중복되었습니다: CELL{point.CellNo:00}_{shotName}");
                    }

                    var previous = latestByKey.GetValueOrDefault(key);
                    var current = previous is null
                        ? (X: 0.0, Y: 0.0)
                        : CReviewOffsetData.ToDesign(parameters, point.HeadNo, previous.ReviewOffsetX, previous.ReviewOffsetY, previous.IsDesignCoordinate);
                    var finalX = CAlignShotOffsetData.RoundValue(current.X + delta.X);
                    var finalY = CAlignShotOffsetData.RoundValue(current.Y + delta.Y);
                    latestByKey[key] = new ST_REVIEW_OFFSET_ROW(point.CellNo, shotName, finalX, finalY)
                    {
                        IsDesignCoordinate = true
                    };
                    displayRows.Add(new ST_REVIEW_OFFSET_APPLY_GRID_ROW(
                        point.HeadNo,
                        point.CellNo,
                        shotName,
                        point.ShotKey,
                        current.X,
                        current.Y,
                        delta.X,
                        delta.Y,
                        finalX,
                        finalY));
                }

                var orderedRows = displayRows
                    .OrderBy(row => row.CellNo)
                    .ThenBy(row => row.ShotName, CReviewShotNameComparer.Instance)
                    .ToArray();
                var targetCells = orderedRows.Select(row => row.CellNo).ToHashSet();
                var targetKeys = orderedRows.Select(row => NormalizeReviewShotKey(row.ShotKey, row.CellNo)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var shotsByCell = new Dictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>>();
                foreach (var cellNo in targetCells.Order())
                {
                    var shots = allPlan.GetCellPoints(cellNo)
                        .Select(point => new ST_REVIEW_DATA_SHOT_POSITION(
                            point.ShotKey,
                            point.CellNo,
                            point.ShotNo,
                            (point.ShotNo - 1) % Math.Max(1, point.PixelCountX),
                            (point.ShotNo - 1) / Math.Max(1, point.PixelCountX),
                            Math.Max(1, point.PixelCountX),
                            Math.Max(1, point.PixelCountY),
                            true)
                        {
                            HeadNo = point.HeadNo,
                            IsResult = targetKeys.Contains(NormalizeReviewShotKey(point.ShotKey, point.CellNo)),
                            IsMasked = point.ExclusionReason.Contains("MASKING", StringComparison.OrdinalIgnoreCase),
                            IsChessSkipped = point.ExclusionReason.Contains("CHESS", StringComparison.OrdinalIgnoreCase)
                        })
                        .ToArray();
                    shotsByCell[cellNo] = shots;
                }

                return new ST_REVIEW_OFFSET_APPLY_PLAN(
                    applicationType,
                    result.FileName,
                    recipe.Name,
                    new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
                    ReadOutputViewerInt(parameters, allPlan.CellCount, "MAX_CELL_NUMBER"),
                    targetCells,
                    shotsByCell,
                    orderedRows,
                    latestByKey.Values.OrderBy(row => row.CellId).ThenBy(row => row.ShotId, StringComparer.OrdinalIgnoreCase).ToArray());
            }

            private static IReadOnlyList<ST_REVIEW_OFFSET_DELTA> BuildDirectDeltas(
                IReadOnlyList<ST_REVIEW_RESULT_FILE_ROW> rows,
                IReadOnlyDictionary<string, ST_REVIEW_PLAN_POINT> pointLookup)
            {
                return MeasuredRows(rows)
                    .Where(row => row.Judge.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    .Select(row =>
                    {
                        var point = ResolveResultPoint(row, pointLookup);
                        return new ST_REVIEW_OFFSET_DELTA(point, row.ErrorX, row.ErrorY);
                    })
                    .ToArray();
            }

            private static IReadOnlyList<ST_REVIEW_OFFSET_DELTA> BuildRoughDeltas(
                ST_REVIEW_RESULT_FILE_DATA result,
                IReadOnlyList<ST_REVIEW_PLAN_POINT> allPoints,
                IReadOnlyDictionary<string, ST_REVIEW_PLAN_POINT> pointLookup)
            {
                var scope = result.RuleScope.Trim().ToUpperInvariant() switch
                {
                    "HEAD" => EN_REVIEW_CORRECTION_SCOPE.Head,
                    "HEAD_CELL" => EN_REVIEW_CORRECTION_SCOPE.HeadCell,
                    _ => throw new InvalidDataException("ROUGH 결과의 RULE_SCOPE가 HEAD 또는 HEAD_CELL이 아닙니다.")
                };
                var measured = MeasuredRows(result.Rows)
                    .Where(row => row.Judge.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    .Select(row =>
                    {
                        var point = ResolveResultPoint(row, pointLookup);
                        return point with
                        {
                            Use = true,
                            ErrorX = row.ErrorX,
                            ErrorY = row.ErrorY,
                            State = EN_REVIEW_POINT_STATE.Ok,
                            Judge = "OK"
                        };
                    })
                    .ToArray();
                var duplicateScope = measured
                    .GroupBy(point => scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell ? (point.HeadNo, point.CellNo) : (point.HeadNo, 0))
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateScope is not null)
                {
                    throw new InvalidDataException($"ROUGH 결과에 같은 적용 범위의 측정값이 중복되었습니다: H{duplicateScope.Key.HeadNo:00} / CELL {duplicateScope.Key.Item2:00}");
                }

                var correctionPlan = new ST_REVIEW_CORRECTION_PLAN(EN_REVIEW_CORRECTION_RULE.Rough, scope, allPoints);
                var plan = new ST_REVIEW_PLAN("OFFSET_PREVIEW", 0, 0, 0, EN_VISION_FLIP_TYPE.Normal, DateTimeOffset.Now, measured)
                {
                    CorrectionPlan = correctionPlan
                };
                return CReviewManager.ProjectCorrectionRows(plan, measured)
                    .Select(point => new ST_REVIEW_OFFSET_DELTA(point, point.ErrorX, point.ErrorY))
                    .ToArray();
            }

            private static IReadOnlyList<ST_REVIEW_OFFSET_DELTA> BuildFineDeltas(
                ST_REVIEW_RESULT_FILE_DATA result,
                IReadOnlyList<ST_REVIEW_PLAN_POINT> allPoints,
                IReadOnlyDictionary<string, ST_REVIEW_PLAN_POINT> pointLookup)
            {
                var measuredRows = MeasuredRows(result.Rows).ToArray();
                if (measuredRows.Any(row => !row.Judge.Equals("OK", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("FINE/AUTO 측정 결과에 NG 또는 비정상 상태가 있어 전체 Offset을 적용할 수 없습니다.");
                }

                var measurements = measuredRows.Select(row =>
                {
                    var point = ResolveResultPoint(row, pointLookup);
                    return point with
                    {
                        Use = true,
                        ErrorX = row.ErrorX,
                        ErrorY = row.ErrorY,
                        State = EN_REVIEW_POINT_STATE.Ok,
                        Judge = "OK"
                    };
                }).ToArray();
                return CReviewManager.CalculateFineOffsets(allPoints, measurements, EN_VISION_FLIP_TYPE.Normal)
                    .Select(delta => new ST_REVIEW_OFFSET_DELTA(delta.Shot, delta.X, delta.Y))
                    .ToArray();
            }

            private static IEnumerable<ST_REVIEW_RESULT_FILE_ROW> MeasuredRows(IReadOnlyList<ST_REVIEW_RESULT_FILE_ROW> rows)
            {
                var hasSourceMetadata = rows.Any(row => !string.IsNullOrWhiteSpace(row.ResultSource));
                return hasSourceMetadata
                    ? rows.Where(row => row.ResultSource.Equals("MEASURED", StringComparison.OrdinalIgnoreCase))
                    : rows.Where(row => row.Judge.Equals("OK", StringComparison.OrdinalIgnoreCase) || row.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase));
            }

            private static IReadOnlyDictionary<string, ST_REVIEW_PLAN_POINT> BuildPointLookup(IReadOnlyList<ST_REVIEW_PLAN_POINT> points)
            {
                var lookup = new Dictionary<string, ST_REVIEW_PLAN_POINT>(StringComparer.OrdinalIgnoreCase);
                foreach (var point in points)
                {
                    lookup[NormalizeReviewShotKey(point.ShotKey, point.CellNo)] = point;
                    var shotName = CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX);
                    lookup[NormalizeReviewShotKey(shotName, point.CellNo)] = point;
                }

                return lookup;
            }

            private static ST_REVIEW_PLAN_POINT ResolveResultPoint(
                ST_REVIEW_RESULT_FILE_ROW row,
                IReadOnlyDictionary<string, ST_REVIEW_PLAN_POINT> pointLookup)
            {
                var key = NormalizeReviewShotKey(row.ShotKey, row.CellNo);
                if (!pointLookup.TryGetValue(key, out var point)
                    && !pointLookup.TryGetValue(NormalizeReviewShotKey(row.ShotName, row.CellNo), out point))
                {
                    throw new InvalidDataException($"현재 레시피에서 검사 결과 Shot을 찾지 못했습니다: CELL{row.CellNo:00} / {row.ShotName} / {row.ShotKey}");
                }

                if (point.HeadNo != row.HeadNo)
                {
                    throw new InvalidDataException($"검사 결과와 현재 레시피의 Head 배정이 다릅니다: CELL{row.CellNo:00}_{row.ShotName} / Result=H{row.HeadNo:00}, Recipe=H{point.HeadNo:00}");
                }

                return point;
            }

            private static ST_REVIEW_OFFSET_APPLY_TYPE CreateApplicationType(string resultType, ST_REVIEW_RESULT_FILE_DATA result)
            {
                return resultType switch
                {
                    "ALL_SHOT" => new("DIRECT_ALL", "DIRECT / ALL SHOT", "측정 OK Shot의 변환된 X/Y를 같은 Shot에 직접 누적"),
                    "ONE_SHOT" => new("DIRECT_ONE", "DIRECT / ONE SHOT", "선택 검사 Shot의 변환된 X/Y를 같은 Shot에 직접 누적"),
                    "ROUGH" => new("ROUGH", $"ROUGH / {result.RuleScope.Trim().ToUpperInvariant().Replace("_", " + ", StringComparison.Ordinal)}", "측정 OK 값을 같은 Head 또는 Head + Cell 범위에 누적"),
                    "FINE" => new("FINE", $"FINE / {Math.Max(result.LineCount, result.FineLineCount)} LINE", "Cell의 같은 X 열에서 측정값을 Y 방향 보간 후 누적"),
                    "AUTO" => new("AUTO_FINE", $"AUTO / {Math.Max(result.LineCount, result.FineLineCount)} LINE", "자동 검사 측정값을 Cell의 같은 X 열에서 Y 방향 보간 후 누적"),
                    _ => throw new InvalidOperationException($"{resultType} 결과는 Review Offset 적용을 지원하지 않습니다.")
                };
            }

            private static bool IsOffsetApplicableResultType(string resultType)
            {
                return resultType.Trim().ToUpperInvariant() is "ALL_SHOT" or "ONE_SHOT" or "ROUGH" or "FINE" or "AUTO";
            }

            private void EnsureLoadedResultUnchanged(string filePath, string recipeId)
            {
                if (!HasLoadedData
                    || _loadedResultFile is null
                    || _loadedRecipe is null
                    || !_loadedResultFile.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)
                    || !_loadedRecipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("작업 중 선택한 Review Result 또는 현재 레시피가 변경되었습니다. DATA LOAD 후 다시 실행해 주세요.");
                }
            }

            private static bool SameOffsetSource(ST_REVIEW_OFFSET_FILE_DATA expected, ST_REVIEW_OFFSET_FILE_DATA current)
            {
                return expected.FilePath.Equals(current.FilePath, StringComparison.OrdinalIgnoreCase)
                    && expected.SavedAt.Equals(current.SavedAt)
                    && expected.Rows.Count == current.Rows.Count;
            }

            private void SetOffsetBusy(bool value)
            {
                if (_isOffsetBusy == value)
                {
                    return;
                }

                _isOffsetBusy = value;
                NotifyLoadCommandState();
            }

            internal void SetSharedOffsetOperationRunning(bool value)
            {
                SetOffsetBusy(value);
            }

            internal void AcceptOffsetRollback()
            {
                NotifyLoadCommandState();
            }

            public sealed class CReviewDataOffsetApplyPanel : INotifyPropertyChanged
            {
                private readonly ST_REVIEW_OFFSET_APPLY_PLAN _plan;
                private int _selectedCellNo;
                private string _selectedShotKey = "";

                internal CReviewDataOffsetApplyPanel(ST_REVIEW_OFFSET_APPLY_PLAN plan)
                {
                    _plan = plan;
                    ApplicationTypes = [plan.ApplicationType];
                    SelectedApplicationType = plan.ApplicationType;
                    SelectCellCommand = new CButtonCommand(SelectCell);
                    var firstCell = plan.CellNos.Order().FirstOrDefault();
                    if (firstCell > 0)
                    {
                        ApplyCellSelection(firstCell);
                    }
                }

                public event PropertyChangedEventHandler? PropertyChanged;

                public CButtonCommand SelectCellCommand { get; }
                public IReadOnlyList<ST_REVIEW_OFFSET_APPLY_TYPE> ApplicationTypes { get; }
                public ST_REVIEW_OFFSET_APPLY_TYPE SelectedApplicationType { get; }
                public ImageSource? GlassPreviewImage { get; private set; }
                public ImageSource? ShotMapImage { get; private set; }
                public IReadOnlyList<ST_REVIEW_DATA_CELL_LABEL> CellLabels { get; private set; } = [];
                public IReadOnlyList<ST_REVIEW_OFFSET_APPLY_GRID_ROW> Rows { get; private set; } = [];
                public ST_REVIEW_OFFSET_APPLY_GRID_ROW? SelectedRow { get; private set; }
                public double GlassSurfaceWidth { get; private set; } = 760.0;
                public double GlassSurfaceHeight { get; private set; } = 500.0;
                public double ShotMapSurfaceWidth { get; private set; } = 680.0;
                public double ShotMapSurfaceHeight { get; private set; } = 420.0;
                public string RecipeName => _plan.RecipeName;
                public string ResultFileName => _plan.ResultFileName;
                public string Summary => $"{_plan.CellNos.Count:N0} Cells / {_plan.Rows.Count:N0} Shots";
                public string SelectedCellTitle => _selectedCellNo > 0 ? $"CELL{_selectedCellNo:00} / {Rows.Count:N0} APPLY SHOTS" : "NO CELL SELECTED";
                public string SelectedShotTitle => SelectedRow is null ? "NO SHOT SELECTED" : $"{SelectedRow.Cell} / {SelectedRow.ShotName} / {SelectedRow.Head}";
                public bool CanSave => _plan.Rows.Count > 0;

                public void SelectShotAt(double x, double y)
                {
                    if (!_plan.ShotsByCell.TryGetValue(_selectedCellNo, out var shots) || shots.Count == 0)
                    {
                        return;
                    }

                    var first = shots[0];
                    var mapRect = GetReviewDataShotMapRect(first.GroupCountX, first.GroupCountY);
                    if (!mapRect.Contains(new Point(x, y)))
                    {
                        return;
                    }

                    var column = Math.Clamp((int)((x - mapRect.Left) / mapRect.Width * first.GroupCountX), 0, first.GroupCountX - 1);
                    var row = Math.Clamp((int)((y - mapRect.Top) / mapRect.Height * first.GroupCountY), 0, first.GroupCountY - 1);
                    var shot = shots.FirstOrDefault(item => item.GroupColumn == column && item.GroupRow == row && item.IsResult);
                    if (shot is not null)
                    {
                        SelectShot(shot.ShotKey, null);
                    }
                }

                public void SelectRow(ST_REVIEW_OFFSET_APPLY_GRID_ROW row)
                {
                    if (_selectedCellNo != row.CellNo)
                    {
                        ApplyCellSelection(row.CellNo);
                    }

                    SelectShot(row.ShotKey, row);
                }

                private void SelectCell(object? parameter)
                {
                    if (int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo)
                        && _plan.CellNos.Contains(cellNo))
                    {
                        ApplyCellSelection(cellNo);
                    }
                }

                private void ApplyCellSelection(int cellNo)
                {
                    _selectedCellNo = cellNo;
                    Rows = _plan.Rows.Where(row => row.CellNo == cellNo).ToArray();
                    SelectedRow = Rows.FirstOrDefault();
                    _selectedShotKey = SelectedRow?.ShotKey ?? "";
                    RefreshImages();
                    NotifyAll();
                }

                private void SelectShot(string shotKey, ST_REVIEW_OFFSET_APPLY_GRID_ROW? preferred)
                {
                    _selectedShotKey = shotKey;
                    SelectedRow = preferred ?? Rows.FirstOrDefault(row => row.ShotKey.Equals(shotKey, StringComparison.OrdinalIgnoreCase));
                    var shots = _plan.ShotsByCell.GetValueOrDefault(_selectedCellNo, []);
                    ShotMapImage = BuildReviewDataShotMap(shots, _selectedShotKey);
                    Notify(nameof(ShotMapImage));
                    Notify(nameof(SelectedRow));
                    Notify(nameof(SelectedShotTitle));
                }

                private void RefreshImages()
                {
                    var preview = BuildReviewDataGlassPreview(_plan.Parameters, _plan.CellCount, _plan.CellNos, _selectedCellNo);
                    GlassPreviewImage = preview.Image;
                    CellLabels = preview.CellLabels;
                    GlassSurfaceWidth = preview.Width;
                    GlassSurfaceHeight = preview.Height;
                    var shots = _plan.ShotsByCell.GetValueOrDefault(_selectedCellNo, []);
                    var size = GetReviewDataShotMapSize(shots);
                    ShotMapSurfaceWidth = size.Width;
                    ShotMapSurfaceHeight = size.Height;
                    ShotMapImage = BuildReviewDataShotMap(shots, _selectedShotKey);
                }

                private void NotifyAll()
                {
                    Notify(nameof(GlassPreviewImage));
                    Notify(nameof(ShotMapImage));
                    Notify(nameof(CellLabels));
                    Notify(nameof(Rows));
                    Notify(nameof(SelectedRow));
                    Notify(nameof(SelectedCellTitle));
                    Notify(nameof(SelectedShotTitle));
                    Notify(nameof(GlassSurfaceWidth));
                    Notify(nameof(GlassSurfaceHeight));
                    Notify(nameof(ShotMapSurfaceWidth));
                    Notify(nameof(ShotMapSurfaceHeight));
                }

                private void Notify(string propertyName)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }
            }

            private sealed record ST_REVIEW_OFFSET_DELTA(ST_REVIEW_PLAN_POINT Point, double X, double Y);
        }

        public sealed record ST_REVIEW_OFFSET_APPLY_TYPE(string Key, string Name, string Detail);

        public sealed record ST_REVIEW_OFFSET_APPLY_GRID_ROW(
            int HeadNo,
            int CellNo,
            string ShotName,
            string ShotKey,
            double CurrentX,
            double CurrentY,
            double AddX,
            double AddY,
            double FinalX,
            double FinalY)
        {
            public string Head => $"H{HeadNo:00}";
            public string Cell => $"CELL{CellNo:00}";
            public string CurrentXText => CurrentX.ToString("F3", CultureInfo.InvariantCulture);
            public string CurrentYText => CurrentY.ToString("F3", CultureInfo.InvariantCulture);
            public string AddXText => AddX.ToString("F3", CultureInfo.InvariantCulture);
            public string AddYText => AddY.ToString("F3", CultureInfo.InvariantCulture);
            public string FinalXText => FinalX.ToString("F3", CultureInfo.InvariantCulture);
            public string FinalYText => FinalY.ToString("F3", CultureInfo.InvariantCulture);
        }

        internal sealed record ST_REVIEW_OFFSET_APPLY_PLAN(
            ST_REVIEW_OFFSET_APPLY_TYPE ApplicationType,
            string ResultFileName,
            string RecipeName,
            IReadOnlyDictionary<string, string> Parameters,
            int CellCount,
            IReadOnlySet<int> CellNos,
            IReadOnlyDictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>> ShotsByCell,
            IReadOnlyList<ST_REVIEW_OFFSET_APPLY_GRID_ROW> Rows,
            IReadOnlyList<ST_REVIEW_OFFSET_ROW> SavedRows);

        private sealed class CReviewShotNameComparer : IComparer<string>
        {
            public static CReviewShotNameComparer Instance { get; } = new();

            public int Compare(string? x, string? y)
            {
                static (int Row, int Column, string Text) Parse(string? value)
                {
                    var text = value?.Trim().ToUpperInvariant() ?? "";
                    var letters = text.TakeWhile(char.IsLetter).ToArray();
                    var column = 0;
                    foreach (var letter in letters)
                    {
                        column = (column * 26) + letter - 'A' + 1;
                    }

                    var row = int.TryParse(text.AsSpan(letters.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : int.MaxValue;
                    return (row, column, text);
                }

                var left = Parse(x);
                var right = Parse(y);
                var rowCompare = left.Row.CompareTo(right.Row);
                if (rowCompare != 0)
                {
                    return rowCompare;
                }

                var columnCompare = left.Column.CompareTo(right.Column);
                return columnCompare != 0 ? columnCompare : StringComparer.OrdinalIgnoreCase.Compare(left.Text, right.Text);
            }
        }
    }
}
