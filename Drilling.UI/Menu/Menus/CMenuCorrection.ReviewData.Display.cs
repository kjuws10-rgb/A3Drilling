namespace Drilling.UI.Menu.Menus
{
    using System.Globalization;
    using System.IO;
    using System.Windows;
    using System.Windows.Media;
    using Drilling.Common.Managers;
    using Drilling.Common.Recipe;
    using Drilling.Common.Review;
    using Drilling.UI.Menu;

    public sealed partial class CMenuCorrection
    {
        public sealed partial class CReviewDataPanel
        {
            private CancellationTokenSource? _dataLoadCancellation;
            private int _dataLoadVersion;
            private int _shotMapRenderVersion;
            private bool _isDataLoading;
            private bool _hasLoadedData;
            private double _loadProgress;
            private string _loadProgressText = "WAITING";
            private string _dataLoadHint = "DATA LOAD 불가능. 사유 : 검사 결과 파일을 선택하세요.";
            private Brush _dataLoadHintBrush = CStatusBrush.ForDisplayState("WAIT");
            private IReadOnlyDictionary<string, string> _loadedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private ST_REVIEW_RESULT_LEVEL_SETTINGS _reviewLevelSettings = CReviewResultLevelSettings.Default;
            private IReadOnlyDictionary<int, EN_REVIEW_RESULT_LEVEL> _cellLevels = new Dictionary<int, EN_REVIEW_RESULT_LEVEL>();
            private Brush _reviewLevel1Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel1Color);
            private Brush _reviewLevel2Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel2Color);
            private Brush _reviewLevel3Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel3Color);
            private Brush _reviewLevel4Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel4Color);
            private IReadOnlySet<int> _resultCellNos = new HashSet<int>();
            private IReadOnlyDictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>> _shotsByCell = new Dictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>>();
            private IReadOnlyList<ST_REVIEW_DATA_GRID_ROW> _allRows = [];
            private ST_REVIEW_RESULT_FILE_DATA? _loadedResultFile;
            private ST_REVIEW_RESULT_CATALOG_ITEM? _loadedCatalog;
            private ST_RECIPE_DEFINITION? _loadedRecipe;
            private int _loadedCellCount;
            private int _selectedCellNo;
            private string _selectedShotKey = "";
            private IReadOnlyList<ST_REVIEW_DATA_SCATTER_HIT> _scatterPlotHits = [];
            private double _scatterPlotMinimum = -5.0;
            private double _scatterPlotMaximum = 5.0;
            private double _scatterPlotGridUnit = 0.5;
            private string _scatterPlotMinimumText = "-5";
            private string _scatterPlotMaximumText = "5";
            private string _scatterPlotGridUnitText = "0.5";

            public bool IsDataLoading => _isDataLoading;
            public bool HasLoadedData => _hasLoadedData;
            public bool ShowDataPlaceholder => !_hasLoadedData && !_isDataLoading;
            public bool CanLoadData => _selectedNode?.Result is { } result && !IsBlockedStatus(result.ResultStatus) && !IsBusy && !_isDataLoading;
            public double LoadProgress => _loadProgress;
            public string LoadProgressText => _loadProgressText;
            public string DataLoadHint => _dataLoadHint;
            public Brush DataLoadHintBrush => _dataLoadHintBrush;
            public Brush ReviewLevel1Brush => _reviewLevel1Brush;
            public Brush ReviewLevel2Brush => _reviewLevel2Brush;
            public Brush ReviewLevel3Brush => _reviewLevel3Brush;
            public Brush ReviewLevel4Brush => _reviewLevel4Brush;
            public ImageSource? GlassPreviewImage { get; private set; }
            public ImageSource? ShotMapImage { get; private set; }
            public ImageSource? ScatterPlotImage { get; private set; }
            public CReviewCameraPreview Camera { get; } = new();
            public IReadOnlyList<ST_REVIEW_DATA_CELL_LABEL> CellLabels { get; private set; } = [];
            public IReadOnlyList<ST_REVIEW_DATA_GRID_ROW> Rows { get; private set; } = [];
            public ST_REVIEW_DATA_GRID_ROW? SelectedRow { get; private set; }
            public string GlassSummary { get; private set; } = "0 Result Cells / 0 Result Rows";
            public string SelectedCellTitle { get; private set; } = "NO CELL SELECTED";
            public string SelectedShotTitle { get; private set; } = "NO SHOT SELECTED";
            public string SelectedShotDetail { get; private set; } = "-";
            public double GlassSurfaceWidth { get; private set; } = 760.0;
            public double GlassSurfaceHeight { get; private set; } = 500.0;
            public double ShotMapSurfaceWidth { get; private set; } = 680.0;
            public double ShotMapSurfaceHeight { get; private set; } = 420.0;
            public double ScatterPlotSurfaceWidth { get; } = 920.0;
            public double ScatterPlotSurfaceHeight { get; } = 230.0;
            public string ScatterPlotSummary { get; private set; } = "NO RESULT DATA";
            public string ScatterPlotMinimumText
            {
                get => _scatterPlotMinimumText;
                set
                {
                    if (_scatterPlotMinimumText == value)
                    {
                        return;
                    }

                    _scatterPlotMinimumText = value;
                    Notify(nameof(ScatterPlotMinimumText));
                }
            }

            public string ScatterPlotMaximumText
            {
                get => _scatterPlotMaximumText;
                set
                {
                    if (_scatterPlotMaximumText == value)
                    {
                        return;
                    }

                    _scatterPlotMaximumText = value;
                    Notify(nameof(ScatterPlotMaximumText));
                }
            }

            public string ScatterPlotGridUnitText
            {
                get => _scatterPlotGridUnitText;
                set
                {
                    if (_scatterPlotGridUnitText == value)
                    {
                        return;
                    }

                    _scatterPlotGridUnitText = value;
                    Notify(nameof(ScatterPlotGridUnitText));
                }
            }

            private void PrepareSelectedResult(ST_REVIEW_RESULT_CATALOG_ITEM? result)
            {
                _dataLoadCancellation?.Cancel();
                _dataLoadCancellation?.Dispose();
                _dataLoadCancellation = null;
                Interlocked.Increment(ref _dataLoadVersion);
                SetDataLoading(false);
                ClearLoadedDisplay();

                if (result is null)
                {
                    SetDataLoadHint("DATA LOAD 불가능. 사유 : 검사 결과 파일을 선택하세요.", "WAIT");
                }
                else if (result.ResultStatus.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    var reason = string.IsNullOrWhiteSpace(result.ResultMessage) ? "실패 사유가 기록되어 있지 않습니다." : result.ResultMessage;
                    SetDataLoadHint($"DATA LOAD 불가능. 사유 : 검사 실패 - {reason}", "ERROR");
                }
                else if (result.ResultStatus.Equals("INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    var reason = string.IsNullOrWhiteSpace(result.ResultMessage) ? "결과 파일이 올바르지 않습니다." : result.ResultMessage;
                    SetDataLoadHint($"DATA LOAD 불가능. 사유 : {reason}", "ERROR");
                }
                else
                {
                    SetDataLoadHint("선택한 검사 결과를 불러오려면 DATA LOAD를 누르세요.", "WAIT");
                }

                NotifyLoadCommandState();
            }

            private async void LoadDataRequested(object? parameter)
            {
                await LoadSelectedData();
            }

            private async Task LoadSelectedData()
            {
                if (_selectedNode?.Result is not { } catalog || IsBlockedStatus(catalog.ResultStatus) || _isDataLoading)
                {
                    return;
                }

                _dataLoadCancellation?.Cancel();
                _dataLoadCancellation?.Dispose();
                _dataLoadCancellation = new CancellationTokenSource();
                var token = _dataLoadCancellation.Token;
                var version = Interlocked.Increment(ref _dataLoadVersion);
                ClearLoadedDisplay();
                SetDataLoading(true);
                SetLoadProgress(5.0, "검사 결과 준비 중...");
                SetDataLoadHint("선택한 검사 결과를 불러오는 중입니다.", "WAIT");

                try
                {
                    var resultFile = await Task.Run(() => _reviewResultFile.Load(catalog.FilePath, token), token);
                    token.ThrowIfCancellationRequested();
                    if (version != Volatile.Read(ref _dataLoadVersion))
                    {
                        return;
                    }

                    SetLoadProgress(30.0, "결과 파일 로드 완료");
                    var recipes = await Task.Run(() => _recipeManager.LoadRecipes(token), token);
                    token.ThrowIfCancellationRequested();
                    var recipe = recipes.FirstOrDefault(item => item.Id.Equals(resultFile.RecipeId, StringComparison.OrdinalIgnoreCase))
                        ?? recipes.FirstOrDefault(item => item.Name.Equals(resultFile.RecipeId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"Recipe '{resultFile.RecipeId}' was not found.");
                    var settings = await Task.Run(() =>
                    {
                        var values = new List<ST_SYSTEM_PARAMETER>();
                        values.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Option, token));
                        values.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Motor, token));
                        return (IReadOnlyList<ST_SYSTEM_PARAMETER>)values;
                    }, token);
                    token.ThrowIfCancellationRequested();
                    SetLoadProgress(55.0, "레시피 배치 로드 완료");

                    var parameters = BuildOutputViewerParameterMap(recipe, settings);
                    var model = await Task.Run(() => BuildDisplayModel(resultFile, parameters, token), token);
                    token.ThrowIfCancellationRequested();
                    if (version != Volatile.Read(ref _dataLoadVersion))
                    {
                        return;
                    }

                    SetLoadProgress(90.0, "프리뷰 생성 중...");
                    ApplyDisplayModel(model, resultFile, catalog, recipe);
                    SetLoadProgress(100.0, "DATA LOAD 완료");
                    SetDataLoadHint($"{catalog.FileName}에서 결과 {model.Rows.Count:N0}건을 불러왔습니다.", "OK");
                    _statusReporter($"Review Result data loaded: {catalog.FileName} / {model.Rows.Count:N0} rows.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (version == Volatile.Read(ref _dataLoadVersion))
                    {
                        SetDataLoadHint("검사 결과 불러오기가 취소되었습니다.", "WARN");
                    }
                }
                catch (Exception exception)
                {
                    if (version == Volatile.Read(ref _dataLoadVersion))
                    {
                        ClearLoadedDisplay();
                        SetDataLoadHint($"DATA LOAD 불가능. 사유 : 검사 결과 로드 실패 - {exception.Message}", "ERROR");
                        _statusReporter($"Review Result data load failed: {exception.Message}");
                    }
                }
                finally
                {
                    if (version == Volatile.Read(ref _dataLoadVersion))
                    {
                        SetDataLoading(false);
                    }
                }
            }

            private static ST_REVIEW_DATA_DISPLAY_MODEL BuildDisplayModel(
                ST_REVIEW_RESULT_FILE_DATA resultFile,
                IReadOnlyDictionary<string, string> parameters,
                CancellationToken token)
            {
                var cellCount = ReadOutputViewerInt(parameters, CCellPatternCalculator.MinCellCount, "MAX_CELL_NUMBER");
                if (cellCount is < CCellPatternCalculator.MinCellCount or > 100)
                {
                    throw new InvalidDataException($"MAX_CELL_NUMBER must be between 1 and 100. Current={cellCount}");
                }

                var levelSettings = CReviewResultLevelSettings.Read(parameters);

                var resultCells = resultFile.Rows.Select(row => row.CellNo).Distinct().Order().ToArray();
                var resultCellSet = resultCells.ToHashSet();
                if (resultCells.Any(cellNo => cellNo < 1 || cellNo > cellCount))
                {
                    throw new InvalidDataException("The result contains a Cell outside the current Recipe range.");
                }

                var layouts = new Dictionary<int, ST_CELL_PATTERN_LAYOUT>();
                foreach (var cellNo in resultCells)
                {
                    token.ThrowIfCancellationRequested();
                    var layout = CalculateReviewDataCellLayout(parameters, cellNo);
                    if (!layout.IsValid)
                    {
                        throw new InvalidDataException($"CELL{cellNo:00} pattern is invalid: {layout.ValidationMessage}");
                    }

                    layouts[cellNo] = layout;
                }

                var displayRows = new List<ST_REVIEW_DATA_GRID_ROW>(resultFile.Rows.Count);
                var resultShotKeys = new Dictionary<int, HashSet<string>>();
                foreach (var (source, index) in resultFile.Rows.Select((row, index) => (row, index)))
                {
                    token.ThrowIfCancellationRequested();
                    var layout = layouts[source.CellNo];
                    var position = ResolveShotPosition(source, layout, index);
                    var shotName = position.IsResolved
                        ? CReviewShotNameFormatter.ToMatrixName(position.ShotNo, position.GroupCountX)
                        : string.IsNullOrWhiteSpace(source.ShotName) ? "-" : source.ShotName;
                    var level = levelSettings.Resolve(source.ErrorX, source.ErrorY);
                    var row = new ST_REVIEW_DATA_GRID_ROW(
                        source.HeadNo,
                        source.CellNo,
                        shotName,
                        source.BeamNo,
                        source.ErrorX,
                        source.ErrorY,
                        source.Judge,
                        position.ShotKey,
                        position.GroupColumn,
                        position.GroupRow,
                        position.GroupCountX,
                        position.GroupCountY,
                        position.IsResolved,
                        level,
                        CreateReviewLevelBrush(levelSettings.ColorFor(level)));
                    displayRows.Add(row);

                    if (position.IsResolved)
                    {
                        if (!resultShotKeys.TryGetValue(source.CellNo, out var cellShotKeys))
                        {
                            cellShotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            resultShotKeys[source.CellNo] = cellShotKeys;
                        }

                        cellShotKeys.Add(position.ShotKey);
                    }
                }

                var orderedRows = displayRows
                    .OrderBy(row => row.CellNo)
                    .ThenBy(row => row.GroupRow < 0 ? int.MaxValue : row.GroupRow)
                    .ThenBy(row => row.GroupColumn < 0 ? int.MaxValue : row.GroupColumn)
                    .ThenBy(row => row.BeamNo)
                    .ToArray();
                var cellLevels = orderedRows
                    .GroupBy(row => row.CellNo)
                    .ToDictionary(
                        group => group.Key,
                        group => (EN_REVIEW_RESULT_LEVEL)group.Max(row => row.LevelNo));
                var resultRowsByShot = orderedRows
                    .Where(row => row.IsResolved)
                    .GroupBy(row => row.ShotKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.OrderByDescending(row => row.LevelNo).First(),
                        StringComparer.OrdinalIgnoreCase);
                var coordinateParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
                for (var headNo = 1; headNo <= 8; headNo++)
                {
                    coordinateParameters[$"H{headNo:00}_USE"] = "ON";
                }

                token.ThrowIfCancellationRequested();
                var designShots = CShotCoordinatePlanBuilder.BuildDesignShots(coordinateParameters)
                    .Where(shot => resultCellSet.Contains(shot.CellNo))
                    .ToArray();
                token.ThrowIfCancellationRequested();
                var assignedShots = CShotCoordinatePlanBuilder.Build(coordinateParameters).Shots
                    .Where(shot => resultCellSet.Contains(shot.CellNo))
                    .ToDictionary(shot => shot.ShotKey, StringComparer.OrdinalIgnoreCase);
                var maskingPlan = CRecipeShotMaskingPlan.Build(coordinateParameters);
                var shotsByCell = designShots
                    .GroupBy(shot => shot.CellNo)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>)group
                            .OrderBy(shot => shot.GroupRow)
                            .ThenBy(shot => shot.GroupColumn)
                            .Select(shot =>
                            {
                                token.ThrowIfCancellationRequested();
                                var assigned = assignedShots.GetValueOrDefault(shot.ShotKey);
                                var state = assigned is null
                                    ? ST_RECIPE_SHOT_MASKING_RESULT.Empty
                                    : maskingPlan.EvaluateShot(assigned);
                                var resultRow = resultRowsByShot.GetValueOrDefault(shot.ShotKey);
                                return new ST_REVIEW_DATA_SHOT_POSITION(
                                    shot.ShotKey,
                                    shot.CellNo,
                                    shot.CellShotNo,
                                    shot.GroupColumn,
                                    shot.GroupRow,
                                    shot.GroupCountX,
                                    shot.GroupCountY,
                                    true)
                                {
                                    HeadNo = assigned?.HeadNo ?? 0,
                                    IsResult = resultShotKeys.GetValueOrDefault(shot.CellNo)?.Contains(shot.ShotKey) == true,
                                    IsMasked = state.IsMasked,
                                     IsChessSkipped = state.IsChessSkipped,
                                     ResultLevel = resultRow?.Level,
                                     ResultLevelBrush = resultRow?.LevelBrush
                                };
                            })
                            .ToArray());
                return new ST_REVIEW_DATA_DISPLAY_MODEL(
                    new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
                    cellCount,
                    resultCellSet,
                    shotsByCell,
                    orderedRows,
                    levelSettings,
                    cellLevels);
            }

            private static ST_CELL_PATTERN_LAYOUT CalculateReviewDataCellLayout(
                IReadOnlyDictionary<string, string> parameters,
                int cellNo)
            {
                var pixelSize = ReadOutputViewerDouble(parameters, 0.09, "PIXEL_SIZE");
                var splitBeamCount = ReadOutputViewerInt(parameters, 1, "SPLITED_BEAM_COUNT");
                var alignKeyMargins = CAlignKeyMarginSettings.Read(parameters);
                var modelTypeCount = CRecipeModelPixelCountReader.ReadModelTypeCount(parameters);
                var modelType = CRecipeModelPixelCountReader.ReadCellModelType(parameters, cellNo, modelTypeCount);
                var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameters, modelType);
                return CCellPatternCalculator.Calculate(
                    new ST_CELL_PATTERN_DEFINITION(
                        cellNo,
                        ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X"),
                        ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y"),
                        ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ROTATION"),
                        pixelSize,
                        modelPattern.PixelCountX,
                        modelPattern.PixelCountY,
                        modelPattern.ProcessPitch,
                        splitBeamCount,
                        alignKeyMargins.X,
                        alignKeyMargins.Y),
                    includeShotCenters: false);
            }

            private static ST_REVIEW_DATA_SHOT_POSITION ResolveShotPosition(
                ST_REVIEW_RESULT_FILE_ROW row,
                ST_CELL_PATTERN_LAYOUT layout,
                int rowIndex)
            {
                var shotNo = ReadShotNo(row.ShotKey);
                var column = row.GroupColumn;
                var groupRow = row.GroupRow;
                if (shotNo > 0)
                {
                    column = (shotNo - 1) % layout.GroupCountX;
                    groupRow = (shotNo - 1) / layout.GroupCountX;
                }
                else if (column >= 0 && groupRow >= 0)
                {
                    shotNo = (groupRow * layout.GroupCountX) + column + 1;
                }
                else if (TryParseMatrixName(row.ShotName, out column, out groupRow))
                {
                    shotNo = (groupRow * layout.GroupCountX) + column + 1;
                }
                else
                {
                    var suffix = row.ShotKey.Contains('_') ? row.ShotKey[(row.ShotKey.LastIndexOf('_') + 1)..] : row.ShotKey;
                    if (TryParseMatrixName(suffix, out column, out groupRow))
                    {
                        shotNo = (groupRow * layout.GroupCountX) + column + 1;
                    }
                }

                var resolved = shotNo > 0
                    && column >= 0
                    && column < layout.GroupCountX
                    && groupRow >= 0
                    && groupRow < layout.GroupCountY;
                return new ST_REVIEW_DATA_SHOT_POSITION(
                    resolved ? CReviewManager.ToShotKey(row.CellNo, shotNo) : $"UNRESOLVED_{row.CellNo}_{rowIndex}",
                    row.CellNo,
                    resolved ? shotNo : 0,
                    resolved ? column : -1,
                    resolved ? groupRow : -1,
                    layout.GroupCountX,
                    layout.GroupCountY,
                    resolved);
            }

            private static int ReadShotNo(string shotKey)
            {
                var normalized = CReviewManager.NormalizeShotKey(shotKey);
                var separator = normalized.IndexOf("_SHOT", StringComparison.Ordinal);
                return separator >= 0
                    && int.TryParse(normalized.AsSpan(separator + 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var shotNo)
                    ? shotNo
                    : 0;
            }

            private static bool TryParseMatrixName(string value, out int column, out int row)
            {
                column = -1;
                row = -1;
                var text = value.Trim().ToUpperInvariant();
                var letterCount = text.TakeWhile(char.IsLetter).Count();
                if (letterCount == 0 || letterCount == text.Length || !int.TryParse(text.AsSpan(letterCount), NumberStyles.Integer, CultureInfo.InvariantCulture, out var oneBasedRow) || oneBasedRow <= 0)
                {
                    return false;
                }

                var oneBasedColumn = 0;
                foreach (var character in text.AsSpan(0, letterCount))
                {
                    if (character is < 'A' or > 'Z')
                    {
                        return false;
                    }

                    oneBasedColumn = checked((oneBasedColumn * 26) + (character - 'A' + 1));
                }

                column = oneBasedColumn - 1;
                row = oneBasedRow - 1;
                return true;
            }

            private void ApplyDisplayModel(
                ST_REVIEW_DATA_DISPLAY_MODEL model,
                ST_REVIEW_RESULT_FILE_DATA resultFile,
                ST_REVIEW_RESULT_CATALOG_ITEM catalog,
                ST_RECIPE_DEFINITION recipe)
            {
                _loadedParameters = model.Parameters;
                _reviewLevelSettings = model.LevelSettings;
                _cellLevels = model.CellLevels;
                _reviewLevel1Brush = CreateReviewLevelBrush(model.LevelSettings.Level1Color);
                _reviewLevel2Brush = CreateReviewLevelBrush(model.LevelSettings.Level2Color);
                _reviewLevel3Brush = CreateReviewLevelBrush(model.LevelSettings.Level3Color);
                _reviewLevel4Brush = CreateReviewLevelBrush(model.LevelSettings.Level4Color);
                _loadedResultFile = resultFile;
                _loadedCatalog = catalog;
                _loadedRecipe = recipe;
                _loadedCellCount = model.CellCount;
                _resultCellNos = model.ResultCellNos;
                _shotsByCell = model.ShotsByCell;
                _allRows = model.Rows;
                _hasLoadedData = true;
                var firstCell = model.ResultCellNos.Order().FirstOrDefault();
                if (firstCell > 0)
                {
                    ApplyCellSelection(firstCell, true);
                }
                else
                {
                    RefreshGlassPreview();
                }

                NotifyDisplayState();
            }

            private void SelectCell(object? parameter)
            {
                if (!_hasLoadedData || !int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo) || !_resultCellNos.Contains(cellNo))
                {
                    return;
                }

                ApplyCellSelection(cellNo);
            }

            private void ApplyCellSelection(int cellNo, bool renderSynchronously = false)
            {
                var previousCellNo = _selectedCellNo;
                if (previousCellNo > 0 && previousCellNo != cellNo)
                {
                    foreach (var row in _allRows.Where(row => row.CellNo == previousCellNo))
                    {
                        row.IsReinspectionSelected = false;
                    }

                    NotifyReinspectionCommandState();
                }

                _selectedCellNo = cellNo;
                Rows = _allRows.Where(row => row.CellNo == cellNo).ToArray();
                var firstResolved = Rows.FirstOrDefault(row => row.IsResolved);
                SelectedRow = firstResolved ?? Rows.FirstOrDefault();
                _selectedShotKey = SelectedRow?.ShotKey ?? "";
                RefreshScatterPlot();
                if (GlassPreviewImage is null)
                {
                    RefreshGlassPreview();
                }
                else
                {
                    RefreshCellSelectionOverlay();
                }

                if (renderSynchronously)
                {
                    RefreshShotMap();
                }
                else
                {
                    QueueShotMapRender(cellNo, _selectedShotKey);
                }

                RefreshSelectionText();
                NotifyDisplayState();
            }

            public void SelectShotAt(double x, double y)
            {
                if (!_hasLoadedData || !_shotsByCell.TryGetValue(_selectedCellNo, out var shots) || shots.Count == 0)
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
                var shot = shots.FirstOrDefault(item => item.GroupColumn == column && item.GroupRow == row);
                if (shot is not null)
                {
                    ApplyShotSelection(shot.ShotKey, null);
                }
            }

            public void SelectRow(ST_REVIEW_DATA_GRID_ROW row)
            {
                if (!_hasLoadedData
                    || row is null
                    || row.CellNo != _selectedCellNo
                    || !Rows.Any(currentRow => ReferenceEquals(currentRow, row)))
                {
                    return;
                }

                ApplyShotSelection(row.ShotKey, row);
            }

            private void ApplyShotSelection(string shotKey, ST_REVIEW_DATA_GRID_ROW? preferredRow)
            {
                if (string.IsNullOrWhiteSpace(shotKey))
                {
                    return;
                }

                _selectedShotKey = shotKey;
                SelectedRow = preferredRow ?? Rows.FirstOrDefault(row => row.ShotKey.Equals(shotKey, StringComparison.OrdinalIgnoreCase));
                QueueShotMapRender(_selectedCellNo, _selectedShotKey);
                RefreshScatterPlot();
                RefreshSelectionText();
                Notify(nameof(SelectedRow));
                Notify(nameof(SelectedShotTitle));
                Notify(nameof(SelectedShotDetail));
                NotifyReinspectionCommandState();
                NotifyResultEditCommandState();
            }

            private void RefreshGlassPreview()
            {
                if (_loadedParameters.Count == 0 || _loadedCellCount <= 0)
                {
                    GlassPreviewImage = null;
                    CellLabels = [];
                    Camera.Clear();
                    return;
                }

                var preview = BuildReviewDataGlassPreview(
                    _loadedParameters,
                    _loadedCellCount,
                    _resultCellNos,
                    _selectedCellNo,
                    _cellLevels,
                    _reviewLevelSettings);
                GlassPreviewImage = preview.Image;
                CellLabels = preview.CellLabels;
                GlassSurfaceWidth = preview.Width;
                GlassSurfaceHeight = preview.Height;
                GlassSummary = $"{_resultCellNos.Count:N0} Result Cells / {_allRows.Count:N0} Result Rows";
                if (_isReinspectionMode)
                {
                    var cameraParameters = new Dictionary<string, string>(_loadedParameters, StringComparer.OrdinalIgnoreCase)
                    {
                        [CReviewCameraPreview.UseSettingKey] = "ON"
                    };
                    Camera.Configure(cameraParameters, preview.GlassBounds);
                }
                else
                {
                    Camera.Clear();
                }
            }

            private void RefreshShotMap()
            {
                var shots = _shotsByCell.GetValueOrDefault(_selectedCellNo, []);
                var size = GetReviewDataShotMapSize(shots);
                ShotMapSurfaceWidth = size.Width;
                ShotMapSurfaceHeight = size.Height;
                ShotMapImage = BuildReviewDataShotMap(shots, _selectedShotKey, GetResultEditSelectedShotKeys(_selectedCellNo));
            }

            public void SelectScatterPointAt(double x, double y)
            {
                if (!_hasLoadedData || _scatterPlotHits.Count == 0 || !double.IsFinite(x) || !double.IsFinite(y))
                {
                    return;
                }

                ST_REVIEW_DATA_SCATTER_HIT? nearest = null;
                var nearestDistanceSquared = 100.0;
                foreach (var hit in _scatterPlotHits)
                {
                    var deltaX = hit.Point.X - x;
                    var deltaY = hit.Point.Y - y;
                    var distanceSquared = deltaX * deltaX + deltaY * deltaY;
                    if (distanceSquared <= nearestDistanceSquared)
                    {
                        nearestDistanceSquared = distanceSquared;
                        nearest = hit;
                    }
                }

                if (nearest is not null)
                {
                    ApplyShotSelection(nearest.Row.ShotKey, nearest.Row);
                }
            }

            private void ApplyScatterPlotScale(object? parameter)
            {
                if (!TryParseScatterScale(_scatterPlotMinimumText, out var minimum)
                    || !TryParseScatterScale(_scatterPlotMaximumText, out var maximum)
                    || !TryParseScatterScale(_scatterPlotGridUnitText, out var gridUnit)
                    || !double.IsFinite(minimum)
                    || !double.IsFinite(maximum)
                    || !double.IsFinite(gridUnit)
                    || minimum >= maximum
                    || gridUnit <= 0.0
                    || gridUnit > maximum - minimum)
                {
                    _statusReporter("산포도 범위는 MIN < MAX, UNIT > 0인 유한한 mm 값으로 입력해 주세요.");
                    return;
                }

                var gridIntervalCount = (maximum - minimum) / gridUnit;
                if (!double.IsFinite(gridIntervalCount) || gridIntervalCount > 40.0)
                {
                    _statusReporter("산포도 눈금은 40칸 이하가 되도록 MIN, MAX, UNIT을 입력해 주세요.");
                    return;
                }

                _scatterPlotMinimum = minimum;
                _scatterPlotMaximum = maximum;
                _scatterPlotGridUnit = gridUnit;
                _scatterPlotMinimumText = FormatScatterScaleInput(minimum);
                _scatterPlotMaximumText = FormatScatterScaleInput(maximum);
                _scatterPlotGridUnitText = FormatScatterScaleInput(gridUnit);
                Notify(nameof(ScatterPlotMinimumText));
                Notify(nameof(ScatterPlotMaximumText));
                Notify(nameof(ScatterPlotGridUnitText));
                RefreshScatterPlot();
                _statusReporter($"Result scatter range {minimum:0.###} ~ {maximum:0.###} mm / grid {gridUnit:0.###} mm applied.");
            }

            private static bool TryParseScatterScale(string text, out double value)
            {
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            }

            private static string FormatScatterScaleInput(double value)
            {
                return value.ToString("0.###", CultureInfo.InvariantCulture);
            }

            private void RefreshScatterPlot()
            {
                const double left = 62.0;
                const double top = 14.0;
                const double right = 18.0;
                const double bottom = 40.0;
                var plotRect = new Rect(
                    left,
                    top,
                    ScatterPlotSurfaceWidth - left - right,
                    ScatterPlotSurfaceHeight - top - bottom);
                var finiteRows = Rows
                    .Where(row => double.IsFinite(row.ResultX) && double.IsFinite(row.ResultY))
                    .ToArray();
                var drawing = new DrawingGroup();
                var hits = new List<ST_REVIEW_DATA_SCATTER_HIT>(finiteRows.Length);
                using (var context = drawing.Open())
                {
                    context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ScatterPlotSurfaceWidth, ScatterPlotSurfaceHeight));
                    DrawScatterGrid(context, plotRect, _scatterPlotMinimum, _scatterPlotMaximum, _scatterPlotGridUnit);
                    DrawScatterAxes(context, plotRect, _scatterPlotMinimum, _scatterPlotMaximum);

                    if (finiteRows.Length == 0)
                    {
                        DrawScatterText(context, "NO RESULT DATA", new Point(plotRect.Left + plotRect.Width / 2.0, plotRect.Top + plotRect.Height / 2.0), 14.0, CStatusBrush.Frozen(0x64, 0x74, 0x8B), true);
                    }
                    else
                    {
                        var radius = finiteRows.Length >= 10000 ? 1.4 : finiteRows.Length >= 3000 ? 1.8 : 2.5;
                        foreach (var row in finiteRows)
                        {
                            if (!IsInsideScatterRange(row.ResultX, row.ResultY, _scatterPlotMinimum, _scatterPlotMaximum))
                            {
                                continue;
                            }

                            var point = ScatterPoint(row.ResultX, row.ResultY, plotRect, _scatterPlotMinimum, _scatterPlotMaximum);
                            var brush = row.LevelBrush;
                            context.DrawEllipse(brush, null, point, radius, radius);
                            if (_isResultEditMode && row.IsResultEditSelected)
                            {
                                context.DrawEllipse(null, new Pen(CStatusBrush.Frozen(0xF5, 0x9E, 0x0B), 1.8), point, radius + 4.0, radius + 4.0);
                            }

                            hits.Add(new ST_REVIEW_DATA_SCATTER_HIT(row, point));
                        }

                        if (SelectedRow is { } selected
                            && double.IsFinite(selected.ResultX)
                            && double.IsFinite(selected.ResultY)
                            && IsInsideScatterRange(selected.ResultX, selected.ResultY, _scatterPlotMinimum, _scatterPlotMaximum))
                        {
                            var point = ScatterPoint(selected.ResultX, selected.ResultY, plotRect, _scatterPlotMinimum, _scatterPlotMaximum);
                            context.DrawEllipse(CStatusBrush.Frozen(0xF6, 0xC4, 0x53), new Pen(Brushes.White, 1.2), point, 6.0, 6.0);
                            context.DrawEllipse(selected.LevelBrush, null, point, 2.6, 2.6);
                        }
                    }
                }

                drawing.Freeze();
                var image = new DrawingImage(drawing);
                image.Freeze();
                ScatterPlotImage = image;
                _scatterPlotHits = hits;
                ScatterPlotSummary = BuildScatterPlotSummary(finiteRows, hits.Count);
                Notify(nameof(ScatterPlotImage));
                Notify(nameof(ScatterPlotSummary));
            }

            private string BuildScatterPlotSummary(IReadOnlyList<ST_REVIEW_DATA_GRID_ROW> rows, int visibleCount)
            {
                if (rows.Count == 0)
                {
                    return _selectedCellNo > 0 ? $"CELL{_selectedCellNo:00} · NO RESULT DATA" : "NO RESULT DATA";
                }

                var minX = rows.Min(row => row.ResultX);
                var maxX = rows.Max(row => row.ResultX);
                var minY = rows.Min(row => row.ResultY);
                var maxY = rows.Max(row => row.ResultY);
                var visible = visibleCount == rows.Count ? "" : $" · {visibleCount:N0} VISIBLE";
                return $"CELL{_selectedCellNo:00} · {rows.Count:N0} POINTS{visible} · X {minX:0.###} ~ {maxX:0.###} / Y {minY:0.###} ~ {maxY:0.###} mm";
            }

            private static void DrawScatterGrid(DrawingContext context, Rect plotRect, double minimum, double maximum, double gridUnit)
            {
                var gridPen = new Pen(CStatusBrush.Frozen(0x94, 0xA3, 0xB8), 0.55);
                var firstIndex = (int)Math.Ceiling(minimum / gridUnit - 1e-9);
                var lastIndex = (int)Math.Floor(maximum / gridUnit + 1e-9);
                var lineCount = Math.Max(1, lastIndex - firstIndex + 1);
                var labelStride = Math.Max(1, (int)Math.Ceiling(lineCount / 10.0));
                var labelBrush = CStatusBrush.Frozen(0x55, 0x70, 0x86);
                for (var index = firstIndex; index <= lastIndex; index++)
                {
                    var value = index * gridUnit;
                    var ratio = (value - minimum) / (maximum - minimum);
                    var x = plotRect.Left + plotRect.Width * ratio;
                    var y = plotRect.Bottom - plotRect.Height * ratio;
                    context.DrawLine(gridPen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));
                    context.DrawLine(gridPen, new Point(plotRect.Left, y), new Point(plotRect.Right, y));

                    if ((index - firstIndex) % labelStride == 0 || Math.Abs(value) <= gridUnit * 1e-9 || index == lastIndex)
                    {
                        var text = FormatScatterAxisValue(value, gridUnit);
                        DrawScatterText(context, text, new Point(x, plotRect.Bottom + 13.0), 9.0, labelBrush, true);
                        DrawScatterText(context, text, new Point(plotRect.Left - 8.0, y), 9.0, labelBrush, false, true);
                    }
                }

                context.DrawRectangle(null, new Pen(CStatusBrush.Frozen(0x64, 0x74, 0x8B), 1.0), plotRect);
            }

            private static void DrawScatterAxes(DrawingContext context, Rect plotRect, double minimum, double maximum)
            {
                var axisPen = new Pen(CStatusBrush.Frozen(0x38, 0x7D, 0xA8), 1.5);
                if (minimum <= 0.0 && maximum >= 0.0)
                {
                    var zeroRatio = (0.0 - minimum) / (maximum - minimum);
                    var zeroX = plotRect.Left + plotRect.Width * zeroRatio;
                    var zeroY = plotRect.Bottom - plotRect.Height * zeroRatio;
                    context.DrawLine(axisPen, new Point(zeroX, plotRect.Top), new Point(zeroX, plotRect.Bottom));
                    context.DrawLine(axisPen, new Point(plotRect.Left, zeroY), new Point(plotRect.Right, zeroY));
                }

                var labelBrush = CStatusBrush.Frozen(0x55, 0x70, 0x86);
                DrawScatterText(context, "RESULT X (mm)", new Point(plotRect.Right, ScatterPlotHeightForLabel(plotRect)), 9.5, labelBrush, false, true);
                DrawScatterText(context, "RESULT Y (mm)", new Point(plotRect.Left, 1.0), 9.5, labelBrush);
            }

            private static double ScatterPlotHeightForLabel(Rect plotRect)
            {
                return plotRect.Bottom + 27.0;
            }

            private static void DrawScatterText(
                DrawingContext context,
                string text,
                Point anchor,
                double fontSize,
                Brush brush,
                bool center = false,
                bool right = false)
            {
                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    fontSize,
                    brush,
                    1.0);
                var x = center ? anchor.X - formatted.Width / 2.0 : right ? anchor.X - formatted.Width : anchor.X;
                var y = center ? anchor.Y - formatted.Height / 2.0 : anchor.Y - formatted.Height / 2.0;
                context.DrawText(formatted, new Point(x, y));
            }

            private static Point ScatterPoint(double x, double y, Rect plotRect, double minimum, double maximum)
            {
                return new Point(
                    plotRect.Left + (x - minimum) / (maximum - minimum) * plotRect.Width,
                    plotRect.Top + (maximum - y) / (maximum - minimum) * plotRect.Height);
            }

            private static bool IsInsideScatterRange(double x, double y, double minimum, double maximum)
            {
                return x >= minimum && x <= maximum && y >= minimum && y <= maximum;
            }

            private static string FormatScatterAxisValue(double value, double gridUnit)
            {
                var format = gridUnit >= 1.0 ? "0.##" : gridUnit >= 0.1 ? "0.0#" : "0.###";
                return value.ToString(format, CultureInfo.InvariantCulture);
            }

            private static Brush CreateReviewLevelBrush(string colorHex, byte? alpha = null)
            {
                var normalized = CReviewResultLevelSettings.NormalizeColor(colorHex);
                var color = (Color)ColorConverter.ConvertFromString(normalized);
                if (alpha.HasValue)
                {
                    color = Color.FromArgb(alpha.Value, color.R, color.G, color.B);
                }

                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }

            private void RefreshCellSelectionOverlay()
            {
                CellLabels = CellLabels
                    .Select(label => label with
                    {
                        IsSelected = label.IsIncluded && label.CellNo == _selectedCellNo
                    })
                    .ToArray();
            }

            private async void QueueShotMapRender(int cellNo, string selectedShotKey)
            {
                var version = Interlocked.Increment(ref _shotMapRenderVersion);
                var shots = _shotsByCell.GetValueOrDefault(cellNo, []);
                var editSelectedShotKeys = GetResultEditSelectedShotKeys(cellNo);
                try
                {
                    var render = await Task.Run(() =>
                    {
                        var size = GetReviewDataShotMapSize(shots);
                        var image = BuildReviewDataShotMap(shots, selectedShotKey, editSelectedShotKeys);
                        return (Size: size, Image: image);
                    });
                    if (version != Volatile.Read(ref _shotMapRenderVersion)
                        || !_hasLoadedData
                        || _selectedCellNo != cellNo
                        || !_selectedShotKey.Equals(selectedShotKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    ShotMapSurfaceWidth = render.Size.Width;
                    ShotMapSurfaceHeight = render.Size.Height;
                    ShotMapImage = render.Image;
                    Notify(nameof(ShotMapSurfaceWidth));
                    Notify(nameof(ShotMapSurfaceHeight));
                    Notify(nameof(ShotMapImage));
                }
                catch (Exception exception)
                {
                    if (version == Volatile.Read(ref _shotMapRenderVersion))
                    {
                        _statusReporter($"Review Data Shot View render failed: {exception.Message}");
                    }
                }
            }

            private void RefreshSelectionText()
            {
                var shots = _shotsByCell.GetValueOrDefault(_selectedCellNo, []);
                var selectedShot = string.IsNullOrWhiteSpace(_selectedShotKey)
                    ? null
                    : shots.FirstOrDefault(shot => shot.ShotKey.Equals(_selectedShotKey, StringComparison.OrdinalIgnoreCase));
                var resultShotCount = shots.Count(shot => shot.IsResult);
                SelectedCellTitle = _selectedCellNo > 0
                    ? $"CELL{_selectedCellNo:00} / RESULT {resultShotCount:N0} / TOTAL {shots.Count:N0}"
                    : "NO CELL SELECTED";
                SelectedShotTitle = selectedShot is null
                    ? "NO SHOT SELECTED"
                    : $"{CReviewShotNameFormatter.ToMatrixName(selectedShot.ShotNo, selectedShot.GroupCountX)} / "
                        + (selectedShot.HeadNo > 0 ? $"H{selectedShot.HeadNo:00}" : "HEAD -")
                        + (selectedShot.IsResult ? "" : " / NOT INSPECTED");
                SelectedShotDetail = selectedShot is null
                    ? "-"
                    : $"ROW {selectedShot.GroupRow + 1:N0} / COL {selectedShot.GroupColumn + 1:N0}"
                        + (SelectedRow is null ? " / NO RESULT" : $" / LEVEL {SelectedRow.LevelNo}");
            }

            private static ST_REVIEW_DATA_GLASS_PREVIEW BuildReviewDataGlassPreview(
                IReadOnlyDictionary<string, string> parameters,
                int cellCount,
                IReadOnlySet<int> resultCellNos,
                int selectedCellNo,
                IReadOnlyDictionary<int, EN_REVIEW_RESULT_LEVEL>? cellLevels = null,
                ST_REVIEW_RESULT_LEVEL_SETTINGS? levelSettings = null)
            {
                var glassWidth = ReadOutputViewerDouble(parameters, 500.0, "GLASS_SIZE_X");
                var glassHeight = ReadOutputViewerDouble(parameters, 300.0, "GLASS_SIZE_Y");
                if (!double.IsFinite(glassWidth) || !double.IsFinite(glassHeight) || glassWidth <= 0.0 || glassHeight <= 0.0)
                {
                    throw new InvalidDataException("Glass size is not valid.");
                }

                var portrait = glassHeight > glassWidth;
                var canvasWidth = portrait ? 520.0 : 760.0;
                var canvasHeight = portrait ? 650.0 : 500.0;
                const double margin = 42.0;
                var scale = Math.Min((canvasWidth - margin * 2.0) / glassWidth, (canvasHeight - margin * 2.0) / glassHeight);
                if (!double.IsFinite(scale) || scale <= 0.0)
                {
                    throw new InvalidDataException("Glass preview scale is not valid.");
                }

                var frameWidth = glassWidth * scale;
                var frameHeight = glassHeight * scale;
                var frameLeft = (canvasWidth - frameWidth) / 2.0;
                var frameTop = (canvasHeight - frameHeight) / 2.0;
                var frame = new ST_GLASS_PREVIEW_FRAME(frameLeft, frameTop, frameWidth, frameHeight);
                var frameBounds = new Rect(frameLeft, frameTop, frameWidth, frameHeight);
                var alignKeyMargins = CAlignKeyMarginSettings.Read(parameters);
                var akMarginX = alignKeyMargins.X;
                var akMarginY = alignKeyMargins.Y;
                var pixelSize = ReadOutputViewerDouble(parameters, 0.09, "PIXEL_SIZE");
                var modelTypeCount = CRecipeModelPixelCountReader.ReadModelTypeCount(parameters);
                var drawing = new DrawingGroup();
                var labels = new List<ST_REVIEW_DATA_CELL_LABEL>();
                using (var context = drawing.Open())
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0.0, 0.0, canvasWidth, canvasHeight));
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(14, 102, 136, 164)), null, frameBounds);
                    context.PushClip(new RectangleGeometry(frameBounds));
                    for (var cellNo = 1; cellNo <= cellCount; cellNo++)
                    {
                        var modelType = CRecipeModelPixelCountReader.ReadCellModelType(parameters, cellNo, modelTypeCount);
                        var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameters, modelType);
                        var boundary = BuildOutputViewerCellBoundaryGeometry(
                            frameLeft,
                            frameTop,
                            scale,
                            pixelSize,
                            modelPattern.PixelCountX,
                            modelPattern.PixelCountY,
                            akMarginX + ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X"),
                            akMarginY + ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y"),
                            ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ROTATION"));
                        if (boundary is null)
                        {
                            continue;
                        }

                        var isIncluded = resultCellNos.Contains(cellNo);
                        var isSelected = isIncluded && cellNo == selectedCellNo;
                        var level = EN_REVIEW_RESULT_LEVEL.Level4;
                        var hasLevel = cellLevels?.TryGetValue(cellNo, out level) == true && levelSettings is not null;
                        var levelColor = hasLevel
                            ? levelSettings!.ColorFor(level)
                            : "#4ADE80";
                        var fill = isIncluded
                            ? CreateReviewLevelBrush(levelColor, 92)
                            : CreateReviewLevelBrush("#8B9DAE", 20);
                        var border = isIncluded
                            ? CreateReviewLevelBrush(levelColor)
                            : CreateReviewLevelBrush("#8B9DAE", 110);
                        var pen = new Pen(border, isIncluded ? 2.0 : 1.0);
                        pen.Freeze();
                        context.DrawGeometry(fill, pen, boundary);
                        var label = CCellPreviewDrawing.CreateCellLabel(cellNo, boundary.Bounds, canvasWidth, canvasHeight, isSelected);
                        if (label is not null)
                        {
                            labels.Add(new ST_REVIEW_DATA_CELL_LABEL(
                                label.CellNo,
                                label.CanvasCenterX,
                                label.CanvasCenterY,
                                Math.Max(18.0, label.CellNo.ToString(CultureInfo.InvariantCulture).Length * 6.2 + 8.0),
                                isIncluded,
                                isSelected));
                        }
                    }

                    context.Pop();
                    CCellPreviewDrawing.DrawAlignKeys(context, frame, glassWidth, glassHeight, akMarginX, akMarginY);
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(102, 136, 164)), 1.8), frameBounds);
                    DrawOutputViewerPreviewAxisIndicator(context, frameBounds);
                }

                drawing.Freeze();
                var image = new DrawingImage(drawing);
                image.Freeze();
                return new ST_REVIEW_DATA_GLASS_PREVIEW(image, labels, canvasWidth, canvasHeight, frameBounds);
            }

            private static ImageSource? BuildReviewDataShotMap(
                IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION> shots,
                string selectedShotKey,
                IReadOnlySet<string>? editSelectedShotKeys = null)
            {
                if (shots.Count == 0)
                {
                    return null;
                }

                var first = shots[0];
                var size = GetReviewDataShotMapSize(shots);
                var mapRect = GetReviewDataShotMapRect(first.GroupCountX, first.GroupCountY);
                var cellWidth = mapRect.Width / first.GroupCountX;
                var cellHeight = mapRect.Height / first.GroupCountY;
                var cellExtent = Math.Min(cellWidth, cellHeight);
                var cellInset = Math.Clamp(cellExtent * 0.07, 0.15, 1.1);
                var drawing = new DrawingGroup();
                using (var context = drawing.Open())
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0.0, 0.0, size.Width, size.Height));
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(12, 102, 136, 164)), null, mapRect);
                    var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 100, 116, 139)), 0.8);
                    gridPen.Freeze();
                    var columnStep = cellWidth >= 1.25 ? 1 : Math.Max(1, first.GroupCountX / 40);
                    var rowStep = cellHeight >= 1.25 ? 1 : Math.Max(1, first.GroupCountY / 60);
                    for (var column = columnStep; column < first.GroupCountX; column += columnStep)
                    {
                        var x = mapRect.Left + mapRect.Width * column / first.GroupCountX;
                        context.DrawLine(gridPen, new Point(x, mapRect.Top), new Point(x, mapRect.Bottom));
                    }

                    for (var row = rowStep; row < first.GroupCountY; row += rowStep)
                    {
                        var y = mapRect.Top + mapRect.Height * row / first.GroupCountY;
                        context.DrawLine(gridPen, new Point(mapRect.Left, y), new Point(mapRect.Right, y));
                    }

                    var normalGeometry = new StreamGeometry();
                    var maskingCellGeometry = new StreamGeometry();
                    var maskingGeometry = new StreamGeometry();
                    var chessGeometry = new StreamGeometry();
                    var resultCells = new List<(Rect Bounds, Brush Brush)>();
                    using (var normalContext = normalGeometry.Open())
                    using (var maskingCellContext = maskingCellGeometry.Open())
                    using (var maskingContext = maskingGeometry.Open())
                    using (var chessContext = chessGeometry.Open())
                    {
                        foreach (var shot in shots)
                        {
                            var x = mapRect.Left + (shot.GroupColumn + 0.5) * cellWidth;
                            var y = mapRect.Top + (shot.GroupRow + 0.5) * cellHeight;
                            var cellBounds = GetReviewDataShotCellBounds(
                                mapRect,
                                shot.GroupColumn,
                                shot.GroupRow,
                                cellWidth,
                                cellHeight,
                                cellInset);
                            if (shot.IsMasked)
                            {
                                AddReviewDataShotCell(maskingCellContext, cellBounds);
                                AddOutputViewerMaskCross(maskingContext, x, y, Math.Max(1.8, cellExtent * 0.28));
                            }
                            else if (shot.IsChessSkipped)
                            {
                                AddReviewDataShotCell(chessContext, cellBounds);
                            }
                            else if (shot.IsResult)
                            {
                                resultCells.Add((
                                    cellBounds,
                                    shot.ResultLevelBrush ?? CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel4Color)));
                            }
                            else
                            {
                                AddReviewDataShotCell(normalContext, cellBounds);
                            }
                        }
                    }

                    normalGeometry.Freeze();
                    maskingCellGeometry.Freeze();
                    maskingGeometry.Freeze();
                    chessGeometry.Freeze();
                    var maskingBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    var chessBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247));
                    maskingBrush.Freeze();
                    chessBrush.Freeze();
                    var maskingPen = new Pen(maskingBrush, 1.8);
                    var chessPen = new Pen(chessBrush, 1.5);
                    maskingPen.Freeze();
                    chessPen.Freeze();
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(78, 100, 116, 139)), null, normalGeometry);
                    foreach (var resultCell in resultCells)
                    {
                        context.DrawRectangle(resultCell.Brush, null, resultCell.Bounds);
                    }
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 239, 68, 68)), null, maskingCellGeometry);
                    context.DrawGeometry(null, maskingPen, maskingGeometry);
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(105, 168, 85, 247)), chessPen, chessGeometry);
                    if (editSelectedShotKeys is { Count: > 0 })
                    {
                        var editPen = new Pen(new SolidColorBrush(Color.FromRgb(245, 158, 11)), 2.0);
                        editPen.Freeze();
                        foreach (var editShot in shots.Where(shot => editSelectedShotKeys.Contains(shot.ShotKey)))
                        {
                            var cellBounds = GetReviewDataShotCellBounds(
                                mapRect,
                                editShot.GroupColumn,
                                editShot.GroupRow,
                                cellWidth,
                                cellHeight,
                                Math.Min(0.6, cellInset));
                            context.DrawRectangle(null, editPen, cellBounds);
                        }
                    }

                    var selected = shots.FirstOrDefault(shot => shot.ShotKey.Equals(selectedShotKey, StringComparison.OrdinalIgnoreCase));
                    if (selected is not null)
                    {
                        var x = mapRect.Left + (selected.GroupColumn + 0.5) * cellWidth;
                        var y = mapRect.Top + (selected.GroupRow + 0.5) * cellHeight;
                        var selectedBrush = new SolidColorBrush(Color.FromRgb(246, 196, 83));
                        selectedBrush.Freeze();
                        var selectedPen = new Pen(selectedBrush, 2.2);
                        selectedPen.Freeze();
                        var selectedBounds = GetReviewDataShotCellBounds(
                            mapRect,
                            selected.GroupColumn,
                            selected.GroupRow,
                            cellWidth,
                            cellHeight,
                            Math.Min(0.6, cellInset));
                        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(92, 246, 196, 83)), selectedPen, selectedBounds);
                        var crossRadius = Math.Clamp(cellExtent * 0.28, 1.5, 6.0);
                        context.DrawLine(selectedPen, new Point(x - crossRadius, y), new Point(x + crossRadius, y));
                        context.DrawLine(selectedPen, new Point(x, y - crossRadius), new Point(x, y + crossRadius));
                    }

                    var framePen = new Pen(new SolidColorBrush(Color.FromRgb(102, 136, 164)), 1.4);
                    framePen.Freeze();
                    context.DrawRectangle(null, framePen, mapRect);
                }

                drawing.Freeze();
                var image = new DrawingImage(drawing);
                image.Freeze();
                return image;
            }

            private static Rect GetReviewDataShotCellBounds(
                Rect mapRect,
                int groupColumn,
                int groupRow,
                double cellWidth,
                double cellHeight,
                double inset)
            {
                return new Rect(
                    mapRect.Left + (groupColumn * cellWidth) + inset,
                    mapRect.Top + (groupRow * cellHeight) + inset,
                    Math.Max(0.1, cellWidth - (inset * 2.0)),
                    Math.Max(0.1, cellHeight - (inset * 2.0)));
            }

            private static void AddReviewDataShotCell(StreamGeometryContext context, Rect bounds)
            {
                context.BeginFigure(bounds.TopLeft, true, true);
                context.LineTo(bounds.TopRight, true, false);
                context.LineTo(bounds.BottomRight, true, false);
                context.LineTo(bounds.BottomLeft, true, false);
            }

            private static Size GetReviewDataShotMapSize(IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION> shots)
            {
                if (shots.Count == 0)
                {
                    return new Size(680.0, 420.0);
                }

                return shots[0].GroupCountX > shots[0].GroupCountY
                    ? new Size(680.0, 420.0)
                    : new Size(420.0, 680.0);
            }

            private static Rect GetReviewDataShotMapRect(int groupCountX, int groupCountY)
            {
                var size = groupCountX > groupCountY ? new Size(680.0, 420.0) : new Size(420.0, 680.0);
                return new Rect(28.0, 28.0, size.Width - 56.0, size.Height - 56.0);
            }

            private static bool IsBlockedStatus(string status)
            {
                return status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("INVALID", StringComparison.OrdinalIgnoreCase);
            }

            private void ClearLoadedDisplay()
            {
                ResetReinspectionState();
                ResetResultEditState();
                Interlocked.Increment(ref _shotMapRenderVersion);
                _hasLoadedData = false;
                _loadedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _reviewLevelSettings = CReviewResultLevelSettings.Default;
                _cellLevels = new Dictionary<int, EN_REVIEW_RESULT_LEVEL>();
                _reviewLevel1Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel1Color);
                _reviewLevel2Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel2Color);
                _reviewLevel3Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel3Color);
                _reviewLevel4Brush = CreateReviewLevelBrush(CReviewResultLevelSettings.DefaultLevel4Color);
                _resultCellNos = new HashSet<int>();
                _shotsByCell = new Dictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>>();
                _allRows = [];
                _loadedResultFile = null;
                _loadedCatalog = null;
                _loadedRecipe = null;
                _loadedCellCount = 0;
                _selectedCellNo = 0;
                _selectedShotKey = "";
                GlassPreviewImage = null;
                ShotMapImage = null;
                ScatterPlotImage = null;
                ScatterPlotSummary = "NO RESULT DATA";
                _scatterPlotHits = [];
                Camera.Clear();
                CellLabels = [];
                Rows = [];
                SelectedRow = null;
                GlassSummary = "0 Result Cells / 0 Result Rows";
                SelectedCellTitle = "NO CELL SELECTED";
                SelectedShotTitle = "NO SHOT SELECTED";
                SelectedShotDetail = "-";
                NotifyDisplayState();
            }

            private void SetDataLoading(bool value)
            {
                if (_isDataLoading == value)
                {
                    return;
                }

                _isDataLoading = value;
                Notify(nameof(IsDataLoading));
                Notify(nameof(ShowDataPlaceholder));
                NotifyLoadCommandState();
                NotifyReinspectionCommandState();
            }

            private void SetLoadProgress(double value, string text)
            {
                _loadProgress = Math.Clamp(value, 0.0, 100.0);
                _loadProgressText = text;
                Notify(nameof(LoadProgress));
                Notify(nameof(LoadProgressText));
            }

            private void SetDataLoadHint(string message, string state)
            {
                _dataLoadHint = message;
                _dataLoadHintBrush = CStatusBrush.ForDisplayState(state);
                Notify(nameof(DataLoadHint));
                Notify(nameof(DataLoadHintBrush));
            }

            private void NotifyLoadCommandState()
            {
                Notify(nameof(CanLoadData));
                Notify(nameof(CanApplyOffset));
                Notify(nameof(CanEditOffsets));
                Notify(nameof(OffsetActionHint));
                LoadDataCommand.NotifyCanExecuteChanged();
                OffsetApplyCommand.NotifyCanExecuteChanged();
                OffsetRollbackCommand.NotifyCanExecuteChanged();
                OffsetInitCommand.NotifyCanExecuteChanged();
                OffsetViewCommand.NotifyCanExecuteChanged();
            }

            private void NotifyDisplayState()
            {
                Notify(nameof(HasLoadedData));
                Notify(nameof(ShowDataPlaceholder));
                Notify(nameof(GlassPreviewImage));
                Notify(nameof(ShotMapImage));
                Notify(nameof(ScatterPlotImage));
                Notify(nameof(ScatterPlotSummary));
                Notify(nameof(ReviewLevel1Brush));
                Notify(nameof(ReviewLevel2Brush));
                Notify(nameof(ReviewLevel3Brush));
                Notify(nameof(ReviewLevel4Brush));
                Notify(nameof(IsResultEditMode));
                Notify(nameof(CanToggleResultEditMode));
                Notify(nameof(CellLabels));
                Notify(nameof(Rows));
                Notify(nameof(SelectedRow));
                Notify(nameof(GlassSummary));
                Notify(nameof(SelectedCellTitle));
                Notify(nameof(SelectedShotTitle));
                Notify(nameof(SelectedShotDetail));
                Notify(nameof(GlassSurfaceWidth));
                Notify(nameof(GlassSurfaceHeight));
                Notify(nameof(ShotMapSurfaceWidth));
                Notify(nameof(ShotMapSurfaceHeight));
                NotifyLoadCommandState();
                NotifyReinspectionCommandState();
                NotifyResultEditCommandState();
                ToggleResultEditModeCommand.NotifyCanExecuteChanged();
            }
        }

        private sealed record ST_REVIEW_DATA_DISPLAY_MODEL(
            IReadOnlyDictionary<string, string> Parameters,
            int CellCount,
            IReadOnlySet<int> ResultCellNos,
            IReadOnlyDictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>> ShotsByCell,
            IReadOnlyList<ST_REVIEW_DATA_GRID_ROW> Rows,
            ST_REVIEW_RESULT_LEVEL_SETTINGS LevelSettings,
            IReadOnlyDictionary<int, EN_REVIEW_RESULT_LEVEL> CellLevels);

        private sealed record ST_REVIEW_DATA_SCATTER_HIT(
            ST_REVIEW_DATA_GRID_ROW Row,
            Point Point);

        private sealed record ST_REVIEW_DATA_GLASS_PREVIEW(
            ImageSource Image,
            IReadOnlyList<ST_REVIEW_DATA_CELL_LABEL> CellLabels,
            double Width,
            double Height,
            Rect GlassBounds);

        public sealed record ST_REVIEW_DATA_CELL_LABEL(
            int CellNo,
            double CenterX,
            double CenterY,
            double Width,
            bool IsIncluded,
            bool IsSelected)
        {
            public double Height => 16.0;
            public double Left => CenterX - Width / 2.0;
            public double Top => CenterY - Height / 2.0;
        }

        internal sealed record ST_REVIEW_DATA_SHOT_POSITION(
            string ShotKey,
            int CellNo,
            int ShotNo,
            int GroupColumn,
            int GroupRow,
            int GroupCountX,
            int GroupCountY,
            bool IsResolved)
        {
            public int HeadNo { get; init; }
            public bool IsResult { get; init; }
            public bool IsMasked { get; init; }
            public bool IsChessSkipped { get; init; }
            public EN_REVIEW_RESULT_LEVEL? ResultLevel { get; init; }
            public Brush? ResultLevelBrush { get; init; }
        }

        public sealed record ST_REVIEW_DATA_GRID_ROW(
            int HeadNo,
            int CellNo,
            string ShotName,
            int BeamNo,
            double ResultX,
            double ResultY,
            string Judge,
            string ShotKey,
            int GroupColumn,
            int GroupRow,
            int GroupCountX,
            int GroupCountY,
            bool IsResolved,
            EN_REVIEW_RESULT_LEVEL Level,
            Brush LevelBrush)
        {
            public bool IsReinspectionSelected { get; set; }
            public bool IsResultEditSelected { get; set; }
            public string Head => $"H{HeadNo:00}";
            public string Cell => $"CELL{CellNo:00}";
            public string Beam => BeamNo > 0 ? BeamNo.ToString(CultureInfo.InvariantCulture) : "-";
            public string ResultXText => ResultX.ToString("F3", CultureInfo.InvariantCulture);
            public string ResultYText => ResultY.ToString("F3", CultureInfo.InvariantCulture);
            public int LevelNo => (int)Level;
            public string LevelText => $"LEVEL {LevelNo}";
            public Brush JudgeBrush => CStatusBrush.ForDisplayState(Judge);
        }
    }
}
