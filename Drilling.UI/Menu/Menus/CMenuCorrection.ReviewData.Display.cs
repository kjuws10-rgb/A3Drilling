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
            private IReadOnlySet<int> _resultCellNos = new HashSet<int>();
            private IReadOnlyDictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>> _shotsByCell = new Dictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>>();
            private IReadOnlyList<ST_REVIEW_DATA_GRID_ROW> _allRows = [];
            private ST_REVIEW_RESULT_FILE_DATA? _loadedResultFile;
            private ST_REVIEW_RESULT_CATALOG_ITEM? _loadedCatalog;
            private ST_RECIPE_DEFINITION? _loadedRecipe;
            private int _loadedCellCount;
            private int _selectedCellNo;
            private string _selectedShotKey = "";

            public bool IsDataLoading => _isDataLoading;
            public bool HasLoadedData => _hasLoadedData;
            public bool ShowDataPlaceholder => !_hasLoadedData && !_isDataLoading;
            public bool CanLoadData => _selectedNode?.Result is { } result && !IsBlockedStatus(result.ResultStatus) && !IsBusy && !_isDataLoading;
            public double LoadProgress => _loadProgress;
            public string LoadProgressText => _loadProgressText;
            public string DataLoadHint => _dataLoadHint;
            public Brush DataLoadHintBrush => _dataLoadHintBrush;
            public ImageSource? GlassPreviewImage { get; private set; }
            public ImageSource? ShotMapImage { get; private set; }
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
                        position.IsResolved);
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
                                    IsChessSkipped = state.IsChessSkipped
                                };
                            })
                            .ToArray());
                return new ST_REVIEW_DATA_DISPLAY_MODEL(
                    new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
                    cellCount,
                    resultCellSet,
                    shotsByCell,
                    orderedRows);
            }

            private static ST_CELL_PATTERN_LAYOUT CalculateReviewDataCellLayout(
                IReadOnlyDictionary<string, string> parameters,
                int cellNo)
            {
                var pixelSize = ReadOutputViewerDouble(parameters, 0.09, "PIXEL_SIZE");
                var splitBeamCount = ReadOutputViewerInt(parameters, 1, "SPLITED_BEAM_COUNT");
                var akMarginX = ReadOutputViewerDouble(parameters, 0.0, "AK_MARGIN_X");
                var akMarginY = ReadOutputViewerDouble(parameters, 0.0, "AK_MARGIN_Y");
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
                        akMarginX,
                        akMarginY),
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
                RefreshSelectionText();
                Notify(nameof(SelectedRow));
                Notify(nameof(SelectedShotTitle));
                Notify(nameof(SelectedShotDetail));
                NotifyReinspectionCommandState();
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

                var preview = BuildReviewDataGlassPreview(_loadedParameters, _loadedCellCount, _resultCellNos, _selectedCellNo);
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
                ShotMapImage = BuildReviewDataShotMap(shots, _selectedShotKey);
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
                try
                {
                    var render = await Task.Run(() =>
                    {
                        var size = GetReviewDataShotMapSize(shots);
                        var image = BuildReviewDataShotMap(shots, selectedShotKey);
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
                        + (SelectedRow is null ? " / NO RESULT" : "");
            }

            private static ST_REVIEW_DATA_GLASS_PREVIEW BuildReviewDataGlassPreview(
                IReadOnlyDictionary<string, string> parameters,
                int cellCount,
                IReadOnlySet<int> resultCellNos,
                int selectedCellNo)
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
                var akMarginX = ReadOutputViewerDouble(parameters, 0.0, "AK_MARGIN_X");
                var akMarginY = ReadOutputViewerDouble(parameters, 0.0, "AK_MARGIN_Y");
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
                        var fill = isIncluded
                            ? new SolidColorBrush(Color.FromArgb(92, 74, 222, 128))
                            : new SolidColorBrush(Color.FromArgb(20, 139, 157, 174));
                        var border = isIncluded
                            ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                            : new SolidColorBrush(Color.FromArgb(110, 139, 157, 174));
                        fill.Freeze();
                        border.Freeze();
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
                string selectedShotKey)
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
                var pointRadius = Math.Clamp(Math.Min(cellWidth, cellHeight) * 0.30, 1.5, 4.2);
                var drawing = new DrawingGroup();
                using (var context = drawing.Open())
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0.0, 0.0, size.Width, size.Height));
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(12, 102, 136, 164)), null, mapRect);
                    var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 100, 116, 139)), 0.8);
                    gridPen.Freeze();
                    var columnStep = Math.Max(1, first.GroupCountX / 6);
                    var rowStep = Math.Max(1, first.GroupCountY / 8);
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
                    var resultGeometry = new StreamGeometry();
                    var maskingGeometry = new StreamGeometry();
                    var chessGeometry = new StreamGeometry();
                    using (var normalContext = normalGeometry.Open())
                    using (var resultContext = resultGeometry.Open())
                    using (var maskingContext = maskingGeometry.Open())
                    using (var chessContext = chessGeometry.Open())
                    {
                        foreach (var shot in shots)
                        {
                            var x = mapRect.Left + (shot.GroupColumn + 0.5) * cellWidth;
                            var y = mapRect.Top + (shot.GroupRow + 0.5) * cellHeight;
                            if (shot.IsMasked)
                            {
                                AddOutputViewerMaskCross(maskingContext, x, y, Math.Max(2.2, pointRadius * 1.8));
                            }
                            else if (shot.IsChessSkipped)
                            {
                                AddOutputViewerChessDiamond(chessContext, x, y, Math.Max(2.2, pointRadius * 1.8));
                            }
                            else if (shot.IsResult)
                            {
                                AddOutputViewerShotCircle(resultContext, x, y, pointRadius);
                            }
                            else
                            {
                                AddOutputViewerShotCircle(normalContext, x, y, pointRadius);
                            }
                        }
                    }

                    normalGeometry.Freeze();
                    resultGeometry.Freeze();
                    maskingGeometry.Freeze();
                    chessGeometry.Freeze();
                    var normalBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                    var resultBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    var maskingBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    var chessBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247));
                    normalBrush.Freeze();
                    resultBrush.Freeze();
                    maskingBrush.Freeze();
                    chessBrush.Freeze();
                    var maskingPen = new Pen(maskingBrush, 1.8);
                    var chessPen = new Pen(chessBrush, 1.5);
                    maskingPen.Freeze();
                    chessPen.Freeze();
                    context.DrawGeometry(normalBrush, null, normalGeometry);
                    context.DrawGeometry(resultBrush, null, resultGeometry);
                    context.DrawGeometry(null, maskingPen, maskingGeometry);
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(55, 168, 85, 247)), chessPen, chessGeometry);
                    var selected = shots.FirstOrDefault(shot => shot.ShotKey.Equals(selectedShotKey, StringComparison.OrdinalIgnoreCase));
                    if (selected is not null)
                    {
                        var x = mapRect.Left + (selected.GroupColumn + 0.5) * cellWidth;
                        var y = mapRect.Top + (selected.GroupRow + 0.5) * cellHeight;
                        var selectedBrush = new SolidColorBrush(Color.FromRgb(246, 196, 83));
                        selectedBrush.Freeze();
                        var selectedPen = new Pen(selectedBrush, 2.2);
                        selectedPen.Freeze();
                        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(52, 246, 196, 83)), selectedPen, new Point(x, y), 8.0, 8.0);
                        context.DrawLine(selectedPen, new Point(x - 12.0, y), new Point(x + 12.0, y));
                        context.DrawLine(selectedPen, new Point(x, y - 12.0), new Point(x, y + 12.0));
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
                Interlocked.Increment(ref _shotMapRenderVersion);
                _hasLoadedData = false;
                _loadedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            }
        }

        private sealed record ST_REVIEW_DATA_DISPLAY_MODEL(
            IReadOnlyDictionary<string, string> Parameters,
            int CellCount,
            IReadOnlySet<int> ResultCellNos,
            IReadOnlyDictionary<int, IReadOnlyList<ST_REVIEW_DATA_SHOT_POSITION>> ShotsByCell,
            IReadOnlyList<ST_REVIEW_DATA_GRID_ROW> Rows);

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
            bool IsResolved)
        {
            public bool IsReinspectionSelected { get; set; }
            public string Head => $"H{HeadNo:00}";
            public string Cell => $"CELL{CellNo:00}";
            public string Beam => BeamNo > 0 ? BeamNo.ToString(CultureInfo.InvariantCulture) : "-";
            public string ResultXText => ResultX.ToString("F3", CultureInfo.InvariantCulture);
            public string ResultYText => ResultY.ToString("F3", CultureInfo.InvariantCulture);
            public Brush JudgeBrush => CStatusBrush.ForDisplayState(Judge);
        }
    }
}
