namespace Drilling.UI.Menu.Menus
{
    using System.ComponentModel;
    using System.Globalization;
    using System.Windows.Media;
    using Drilling.Common.Managers;
    using System.Windows;
    using Drilling.Common.Correction;
    using Drilling.Common.Interface;
    using Drilling.Common.Recipe;
    using Drilling.Common.Review;
    using Drilling.UI.Popup;
    using System.IO;
    using Drilling.UI.Menu;
    using Microsoft.Win32;
    using Drilling.Common.Interface.Vision;

    public sealed partial class CMenuCorrection : CMenuBase
    {
#region 공유 상태·초기값
        private static readonly string[] CorrectionTabs = ["ALIGN/DISTORTION", "REVIEW", "APC / ICR", "VIEWER / HISTORY"];
        private readonly CReviewResultFileBase _reviewResultFile;
        private readonly CReviewOffsetFileBase _reviewOffsetFile;
        private readonly CReviewManager _reviewManager;
        private readonly CRecipeManager _recipeManager;
        private readonly CSettingManager _settingManager;
        private readonly Func<string> _selectedRecipeIdProvider;
        private readonly Action<string> _statusReporter;
        private readonly Func<Task> _refreshCurrentScreen;
        private readonly CAlignLastLoadService _alignLastLoad;
        private readonly CAlignOffsetLogFileBase? _alignOffsetLogFile;
        private readonly List<ST_CORRECTION_HISTORY_ROW> _reviewDataHistory = [];
        private ST_REVIEW_RESULT_FILE_DATA? _loadedReviewResult;
        private bool _hasPendingReviewOffsetApply;
        private string _reviewLoadStatus = "Select a Review Result CSV.";
        private string _reviewLoadState = "WAIT";
        private string _currentSettingState = "Vision: - | Scanner: -";
        private string _currentVisionSettingState = "-";
        private string _currentScannerSettingState = "-";
        private string _currentSettingDetail = "Vision Flip: -\nScanner Flip: -";
        private string _calculatedSettingDetail = "";
        private string _currentReviewOffsetRecipeName = "Recipe: -";
        private bool _outputViewerIsCellDetailVisible;
        private int _outputViewerSelectedCellNo;
        private string _selectedTab = "ALIGN/DISTORTION";
        private string _simpleOffsetRecipeId = "";
        private Task<ST_REVIEW_OFFSET_FILE_DATA>? _simpleOffsetLoad;
        private bool _isReviewOperationRunning;
        public IReadOnlyList<ST_CORRECTION_TAB> Tabs { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> SummaryItems { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_SOURCE_ROW> SourceRows { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_VALUE_ROW> CandidateRows { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_VALUE_ROW> ApplyRows { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_HISTORY_ROW> HistoryRows { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> DetailItems { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_REVIEW_RESULT_ROW> ReviewResultRows { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_REVIEW_OFFSET_ROW> CalculatedOffsetRows { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_REVIEW_OFFSET_ROW> CurrentOffsetRows { get; private set; } = [];
        public IReadOnlyList<ST_CORRECTION_REVIEW_OFFSET_ROW> ApplyPreviewRows { get; private set; } = [];
        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> OutputViewerCellPreviewLabels { get; private set; } = [];
        public COutputOffsetViewerPanel OutputOffsets { get; } = new();
        public string OutputViewerSummary { get; private set; } = "0 Cells / 0 Shots";
        public ST_CORRECTION_AXIS_FLIP_ROW CurrentVisionFlipRow { get; private set; } = new("VISION", false, false, false);
        public IReadOnlyList<ST_CORRECTION_AXIS_FLIP_ROW> CurrentScannerFlipRows { get; private set; } = [];

        private CancellationTokenSource? _fineCancellation;
        private string _lastReviewOffsetWarning = "";
        public Action<string, string> ReviewOffsetWarning { get; set; } = (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);

        private CancellationTokenSource? _roughCancellation;
        internal bool BlocksMenuNavigation => _fineCancellation is { IsCancellationRequested: false }
            || _roughCancellation is { IsCancellationRequested: false };
#endregion
#region 생성·초기화
        public CMenuCorrection(
            CReviewResultFileBase reviewResultFile, 
            CReviewOffsetFileBase reviewOffsetFile, 
            CReviewManager reviewManager, 
            CRecipeManager recipeManager, 
            CSettingManager settingManager, 
            Func<string> selectedRecipeIdProvider, 
            Action<string> statusReporter, 
            Func<Task> refreshCurrentScreen, 
            CInterfaceManager? interfaceManager = null, 
            CAlignOffsetLogFileBase? alignOffsetLogFile = null
        )
        {
            _reviewResultFile = reviewResultFile;
            _reviewOffsetFile = reviewOffsetFile;
            _reviewManager = reviewManager;
            _recipeManager = recipeManager;
            _settingManager = settingManager;
            _selectedRecipeIdProvider = selectedRecipeIdProvider;
            _statusReporter = statusReporter;
            _refreshCurrentScreen = refreshCurrentScreen;
            _alignLastLoad = new CAlignLastLoadService(interfaceManager);
            _alignOffsetLogFile = alignOffsetLogFile;
            AlignDistortion = new CAlignDistortionPanel(ExecuteAlignBaseAction);
            Simple = new CSimpleCorrectionPanel(action => ExecuteSimpleOffsetAction(action));
            Rough = new CSimpleCorrectionPanel(action => ExecuteRoughAction(action), isRough: true);
            Fine = new CFineCorrectionPanel(action => _ = ExecuteFineAction(action), CRecipeConfirmDialog.ShowFineRetryChoice)
            {
                ShowLineDetails = CFineLineDetailsDialog.Show
            };
            Fine.SourceInvalidated += () => _fineCancellation?.Cancel();
            InitializeRough(interfaceManager);
            SelectTabCommand = new CButtonCommand(SelectTab);
            ExecuteCommand = new CButtonCommand(Execute, _ => !_isReviewOperationRunning);
            async void SelectOutputViewerCellCallback(object? parameter)
            {
                await SelectOutputViewerCell(parameter);
            }

            SelectOutputViewerCellCommand = new CButtonCommand(SelectOutputViewerCellCallback);
            async void BackToOutputViewerGlassCallback(object? parameter)
            {
                await BackToOutputViewerGlass(parameter);
            }

            BackToOutputViewerGlassCommand = new CButtonCommand(BackToOutputViewerGlassCallback);
        }

#endregion
#region 기본 처리
        public override EN_MENU Menu
        {
            get
            {
                return EN_MENU.Correction;
            }
        }

        public string Title
        {
            get
            {
                return $"CORRECTION / {_selectedTab}";
            }
        }

        public string Subtitle
        {
            get
            {
                return GetSubtitle(_selectedTab);
            }
        }

        public string SelectedTab
        {
            get
            {
                return _selectedTab;
            }
        }

        public bool IsOtherCorrectionTab
        {
            get
            {
                return !IsOutputHistoryTab && !IsAlignDistortionTab && !IsReviewCorrectionTab;
            }
        }

        public bool IsAlignDistortionTab => _selectedTab == "ALIGN/DISTORTION";
        public bool IsReviewCorrectionTab => _selectedTab == "REVIEW";
        public CSimpleCorrectionPanel Simple { get; }
        public CSimpleCorrectionPanel Rough { get; }
        public CFineCorrectionPanel Fine { get; }
        public bool IsCorrectionCommandsVisible => !IsAlignDistortionTab && !IsReviewCorrectionTab;
        public CAlignDistortionPanel AlignDistortion { get; }

        public bool IsOutputHistoryTab
        {
            get
            {
                return _selectedTab.Equals("VIEWER / HISTORY", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsCorrectionHistoryVisible
        {
            get
            {
                return !IsOutputHistoryTab;
            }
        }

        public ImageSource? OutputViewerGlassPreviewImage { get; private set; }
        public string OutputViewerSelectedCellName => _outputViewerIsCellDetailVisible && _outputViewerSelectedCellNo > 0 ? $"CELL{_outputViewerSelectedCellNo:00}" : "GLASS";
        public bool OutputViewerIsGlassPreviewVisible => IsOutputHistoryTab && !_outputViewerIsCellDetailVisible;
        public bool OutputViewerIsCellDetailVisible => IsOutputHistoryTab && _outputViewerIsCellDetailVisible;

        public string LoadedReviewFileName
        {
            get
            {
                return _loadedReviewResult?.FileName ?? "Not loaded";
            }
        }

        public string LoadedReviewSettingSummary
        {
            get
            {
                var detail = FormatReviewSettingDetail(_loadedReviewResult);
                return string.IsNullOrWhiteSpace(detail) ? LoadedReviewRule : $"{LoadedReviewRule} · {detail}";
            }
        }

        public string LoadedReviewRule => string.IsNullOrWhiteSpace(_loadedReviewResult?.Rule) ? "-" : _loadedReviewResult.Rule.ToUpperInvariant();
        public string LoadedReviewSettingDetail => FormatReviewSettingDetail(_loadedReviewResult);

        public string ReviewLoadStatus
        {
            get
            {
                return _reviewLoadStatus;
            }
        }

        public Brush ReviewLoadStatusBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(_reviewLoadState);
            }
        }

        public string CurrentSettingState
        {
            get
            {
                return _currentSettingState;
            }
        }

        public string CurrentSettingDetail
        {
            get
            {
                return _currentSettingDetail;
            }
        }

        public string CurrentVisionSettingState
        {
            get
            {
                return _currentVisionSettingState;
            }
        }

        public string CurrentScannerSettingState
        {
            get
            {
                return _currentScannerSettingState;
            }
        }

        public string CurrentReviewOffsetRecipeName
        {
            get
            {
                return _currentReviewOffsetRecipeName;
            }
        }

        public CButtonCommand SelectTabCommand { get; }
        public CButtonCommand ExecuteCommand { get; }
        public CButtonCommand SelectOutputViewerCellCommand { get; }
        public CButtonCommand BackToOutputViewerGlassCommand { get; }

        public override async Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyTabData(_selectedTab);
            if (IsAlignDistortionTab)
            {
                try
                {
                    AlignDistortion.Update(await LoadSelectedOutputViewerRecipe(cancellationToken, fallbackToFirst: false));
                    RefreshAlignOffsetTolerance();
                }
                catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AlignDistortion.Clear($"Glass preview unavailable: {exception.Message}");
                }
            }

            if (IsReviewCorrectionTab)
            {
                try
                {
                    var recipe = await LoadSelectedOutputViewerRecipe(cancellationToken, fallbackToFirst: false);
                    var settings = _settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken).Concat(_settingManager.LoadSection(EN_SETTING_TAB.Motor, cancellationToken)).ToArray();
                    ST_REVIEW_OFFSET_FILE_DATA? current = null;
                    if (recipe is not null)
                    {
                        if (_simpleOffsetLoad is null || _simpleOffsetLoad.IsCanceled || _simpleOffsetRecipeId != recipe.Id || (_simpleOffsetLoad.IsFaulted && _simpleOffsetLoad.Exception?.GetBaseException()is not CReviewOffsetValidationException))
                        {
                            _lastReviewOffsetWarning = "";
                            _simpleOffsetRecipeId = recipe.Id;
                            _simpleOffsetLoad = _reviewOffsetFile is null ? Task.FromResult(ST_REVIEW_OFFSET_FILE_DATA.Empty(recipe.Id)) : Task.Run(() => _reviewOffsetFile.LoadLatest(recipe.Id, cancellationToken), cancellationToken);
                        }

                        current = await _simpleOffsetLoad.WaitAsync(cancellationToken);
                        _lastReviewOffsetWarning = "";
                    }

                    Simple.Update(recipe, settings, current);
                    Rough.Update(recipe, settings, current);
                    try
                    {
                        Fine.Update(recipe, settings);
                    }
                    catch (Exception exception)
                    {
                        Fine.Clear($"Glass preview unavailable: {exception.Message}");
                    }
                }
                catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportInvalidReviewOffset(exception);
                    Simple.Clear($"Glass preview unavailable: {exception.Message}");
                    Rough.Clear($"Glass preview unavailable: {exception.Message}");
                    Fine.Clear($"Glass preview unavailable: {exception.Message}");
                }
            }

            if (IsOutputHistoryTab)
            {
                await RefreshOutputViewerData(cancellationToken);
            }

            var screen = new CScreenViewModel(
                EN_MENU.Correction, 
                Title, 
                Subtitle, 
                [new("Selected", _selectedTab), new("Source", GetSourceName(_selectedTab)), new("State", GetStateName(_selectedTab))], 
                [new("Correction Source", []), new("Correction Candidate", []), new("Apply Preview", [])], 
                correction: this
            );
            return screen;
        }

        private void SelectTab(object? parameter)
        {
            var tabName = parameter?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(tabName))
            {
                return;
            }

            bool MatchesItem(string tab)
            {
                return tab.Equals(tabName, StringComparison.OrdinalIgnoreCase);
            }

            var normalizedTab = CorrectionTabs.FirstOrDefault(MatchesItem);
            if (normalizedTab is null)
            {
                return;
            }

            if (normalizedTab == "REVIEW" && (_selectedTab != "REVIEW" || _simpleOffsetLoad?.IsFaulted == true))
            {
                _simpleOffsetLoad = null;
                _lastReviewOffsetWarning = "";
            }

            _selectedTab = normalizedTab;
            _statusReporter($"Correction tab selected: {_selectedTab}.");
            _ = _refreshCurrentScreen();
        }

        private async Task SelectOutputViewerCell(object? parameter)
        {
            if (!int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo))
            {
                return;
            }

            _outputViewerSelectedCellNo = Math.Max(1, cellNo);
            _outputViewerIsCellDetailVisible = true;
            _statusReporter($"Offset Viewer cell selected: CELL{_outputViewerSelectedCellNo:00}.");
            await _refreshCurrentScreen();
        }

        private async Task BackToOutputViewerGlass(object? parameter)
        {
            _outputViewerIsCellDetailVisible = false;
            _outputViewerSelectedCellNo = 0;
            _statusReporter("Offset Viewer glass preview opened.");
            await _refreshCurrentScreen();
        }

        private async Task RefreshOutputViewerData(CancellationToken cancellationToken)
        {
            try
            {
                var recipe = await LoadSelectedOutputViewerRecipe(cancellationToken);
                if (recipe is null)
                {
                    OutputViewerGlassPreviewImage = null;
                    OutputViewerCellPreviewLabels = [];
                    OutputOffsets.Clear();
                    OutputViewerSummary = "Recipe not loaded";
                    return;
                }

                var settings = new List<ST_SYSTEM_PARAMETER>();
                settings.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken));
                settings.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Motor, cancellationToken));
                var parameters = BuildOutputViewerParameterMap(recipe, settings);
                var reviewOffsetFile = await _reviewOffsetFile.LoadLatest(recipe.Id, cancellationToken);
                _lastReviewOffsetWarning = "";
                ApplyReviewOffsetsToOutputViewerParameters(parameters, reviewOffsetFile);
                var cellCount = Math.Max(CCellPatternCalculator.MinCellCount, ReadOutputViewerInt(parameters, CCellPatternCalculator.MinCellCount, "MAX_CELL_NUMBER"));
                if (_outputViewerIsCellDetailVisible)
                {
                    _outputViewerSelectedCellNo = Math.Clamp(Math.Max(1, _outputViewerSelectedCellNo), 1, cellCount);
                }
                else
                {
                    _outputViewerSelectedCellNo = 0;
                }

                var selectedCellNo = _outputViewerIsCellDetailVisible ? _outputViewerSelectedCellNo : 0;
                var preview = BuildOutputViewerGlassPreview(parameters, cellCount, selectedCellNo);
                OutputViewerGlassPreviewImage = preview.Image;
                OutputViewerCellPreviewLabels = preview.CellLabels;
                OutputViewerSummary = preview.Summary;
                if (_outputViewerIsCellDetailVisible)
                {
                    var coordinateParameters = BuildOutputViewerCoordinateParameterMap(parameters);
                    var plan = CShotCoordinatePlanBuilder.Build(coordinateParameters);
                    OutputOffsets.Update(coordinateParameters, plan.Shots.Where(shot => shot.CellNo == _outputViewerSelectedCellNo).ToArray());
                }
                else
                    OutputOffsets.Clear();
            }
            catch (Exception exception)
            {
                ReportInvalidReviewOffset(exception);
                OutputViewerGlassPreviewImage = null;
                OutputViewerCellPreviewLabels = [];
                OutputOffsets.Clear();
                OutputViewerSummary = $"Offset Viewer unavailable: {exception.Message}";
            }
        }

        private async Task<ST_RECIPE_DEFINITION?> LoadSelectedOutputViewerRecipe(CancellationToken cancellationToken, bool fallbackToFirst = true)
        {
            var recipes = await _recipeManager.LoadRecipes(cancellationToken);
            var selectedRecipeId = NormalizeRecipeId(_selectedRecipeIdProvider());
            bool MatchById(ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Id.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            bool MatchByName(ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Name.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            return string.IsNullOrWhiteSpace(selectedRecipeId) ? (fallbackToFirst ? recipes.FirstOrDefault() : null) : recipes.FirstOrDefault(MatchById) ?? recipes.FirstOrDefault(MatchByName) ?? (fallbackToFirst ? recipes.FirstOrDefault() : null);
        }

        private static Dictionary<string, string> BuildOutputViewerParameterMap(ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in recipe.Parameters)
            {
                SetOutputViewerParameter(parameters, parameter.Key, parameter.Value);
                SetOutputViewerParameter(parameters, parameter.Name, parameter.Value);
            }

            foreach (var setting in settings)
            {
                SetOutputViewerParameter(parameters, setting.Key, setting.Value);
                SetOutputViewerParameter(parameters, setting.Name, setting.Value);
            }

            parameters.TryAdd("HEAD_COUNT", "8");
            return parameters;
        }

        private static void SetOutputViewerParameter(IDictionary<string, string> parameters, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                parameters[key.Trim()] = value;
            }
        }

        internal static void ApplyReviewOffsetsToOutputViewerParameters(IDictionary<string, string> parameters, ST_REVIEW_OFFSET_FILE_DATA reviewOffsetFile)
        {
            CReviewOffsetData.ApplyToParameters(parameters, reviewOffsetFile);
        }

        private static ST_CORRECTION_OFFSET_VIEWER_PREVIEW BuildOutputViewerGlassPreview(IReadOnlyDictionary<string, string> parameters, int cellCount, int selectedCellNo)
        {
            const double canvasWidth = 860.0;
            const double canvasHeight = 430.0;
            const double maxLeft = 44.0;
            const double maxTop = 50.0;
            const double maxWidth = 772.0;
            const double maxHeight = 340.0;
            var glassWidth = ReadOutputViewerDouble(parameters, 500.0, "GLASS_SIZE_X");
            var glassHeight = ReadOutputViewerDouble(parameters, 300.0, "GLASS_SIZE_Y");
            var akMarginX = ReadOutputViewerDouble(parameters, 55.0, "AK_MARGIN_X");
            var akMarginY = ReadOutputViewerDouble(parameters, 45.0, "AK_MARGIN_Y");
            var pixelSize = ReadOutputViewerDouble(parameters, 0.09, "PIXEL_SIZE");
            var splitBeamCount = ReadOutputViewerInt(parameters, 1, "SPLITED_BEAM_COUNT");
            var modelTypeCount = CRecipeModelPixelCountReader.ReadModelTypeCount(parameters);
            if (glassWidth <= 0.0 || glassHeight <= 0.0)
            {
                return new ST_CORRECTION_OFFSET_VIEWER_PREVIEW(null, [], "Glass size is not valid.");
            }

            double? ReadDistortionValue(string key)
            {
                return ReadOutputViewerNullableDouble(parameters, key);
            }

            var distortionKeys = CCellPreviewDrawing.CreateDistortionKeyPreviews(glassWidth, glassHeight, akMarginX, akMarginY, ReadDistortionValue);
            var scale = Math.Min(maxWidth / glassWidth, maxHeight / glassHeight);
            var frameWidth = glassWidth * scale;
            var frameHeight = glassHeight * scale;
            var frameLeft = maxLeft + (maxWidth - frameWidth) / 2.0;
            var frameTop = maxTop + (maxHeight - frameHeight) / 2.0;
            var frame = new ST_GLASS_PREVIEW_FRAME(frameLeft, frameTop, frameWidth, frameHeight);
            var frameBounds = new Rect(frameLeft, frameTop, frameWidth, frameHeight);
            var drawing = new DrawingGroup();
            var cellLabels = new List<ST_CELL_PREVIEW_LABEL>();
            var drawnCellBoundaries = new List<(StreamGeometry Boundary, Pen Pen)>();
            long totalShotGroupCount = 0;
            var invalidCellCount = 0;
            using (var context = drawing.Open())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, canvasWidth, canvasHeight));
                context.PushClip(new RectangleGeometry(frameBounds));
                for (var cellNo = 1; cellNo <= cellCount; cellNo++)
                {
                    var firstPixelX = ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X");
                    var firstPixelY = ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y");
                    var rotation = ReadOutputViewerDouble(parameters, 0.0, $"CELL{cellNo}_ROTATION");
                    var modelType = CRecipeModelPixelCountReader.ReadCellModelType(parameters, cellNo, modelTypeCount);
                    var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameters, modelType);
                    var layout = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(cellNo, firstPixelX, firstPixelY, rotation, pixelSize, modelPattern.PixelCountX, modelPattern.PixelCountY, modelPattern.ProcessPitch, splitBeamCount, akMarginX, akMarginY));
                    if (!layout.IsValid)
                    {
                        invalidCellCount++;
                    }

                    totalShotGroupCount += layout.ShotCenters.Count;
                    var boundary = BuildOutputViewerCellBoundaryGeometry(frameLeft, frameTop, scale, pixelSize, modelPattern.PixelCountX, modelPattern.PixelCountY, akMarginX + firstPixelX, akMarginY + firstPixelY, rotation);
                    if (boundary is null)
                    {
                        continue;
                    }

                    var isSelectedCell = selectedCellNo > 0 && cellNo == selectedCellNo;
                    var cellBrush = CMenuMain.CreateHeadBrush(((cellNo - 1) % 8) + 1, (byte)(selectedCellNo <= 0 || isSelectedCell ? 230 : 105));
                    var boundaryBrush = CMenuMain.CreateHeadBrush(((cellNo - 1) % 8) + 1, (byte)(isSelectedCell ? 255 : 150));
                    var boundaryPen = new Pen(boundaryBrush, isSelectedCell ? 2.4 : 1.2);
                    boundaryPen.Freeze();
                    context.DrawGeometry(cellBrush, null, boundary);
                    drawnCellBoundaries.Add((boundary, boundaryPen));
                    var label = CCellPreviewDrawing.CreateCellLabel(cellNo, boundary.Bounds, canvasWidth, canvasHeight, isSelectedCell);
                    if (label is not null)
                    {
                        cellLabels.Add(label);
                    }
                }

                foreach (var drawnCell in drawnCellBoundaries)
                {
                    context.DrawGeometry(null, drawnCell.Pen, drawnCell.Boundary);
                }

                context.Pop();
                CCellPreviewDrawing.DrawAlignKeys(context, frame, glassWidth, glassHeight, akMarginX, akMarginY);
                CCellPreviewDrawing.DrawDistortionKeys(context, frame, glassWidth, glassHeight, distortionKeys);
            }

            drawing.Freeze();
            var paddingX = frameWidth * 0.03;
            var paddingY = Math.Max(22.0, frameHeight * 0.03);
            var cellRect = new Rect(0, 0, frameWidth + (paddingX * 2.0), frameHeight + (paddingY * 2.0));
            var glassRect = new Rect(paddingX, paddingY, frameWidth, frameHeight);
            var translatedCellLabels = cellLabels.Select(label => label with { CanvasCenterX = label.CanvasCenterX - frameLeft + paddingX, CanvasCenterY = label.CanvasCenterY - frameTop + paddingY, DesignWidth = cellRect.Width, DesignHeight = cellRect.Height }).ToArray();
            var cellDrawing = new DrawingGroup();
            using (var cellContext = cellDrawing.Open())
            {
                cellContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, cellRect);
                cellContext.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(102, 136, 164)), 1.8), glassRect);
                cellContext.PushClip(new RectangleGeometry(cellRect));
                cellContext.PushTransform(new TranslateTransform(-(frameLeft - paddingX), -(frameTop - paddingY)));
                cellContext.DrawDrawing(drawing);
                cellContext.Pop();
                cellContext.Pop();
                DrawOutputViewerPreviewAxisIndicator(cellContext, glassRect);
            }

            cellDrawing.Freeze();
            var cellImage = new DrawingImage(cellDrawing);
            cellImage.Freeze();
            var summary = $"{cellCount:N0} Cells / {totalShotGroupCount:N0} Shot Centers / Glass {FormatOutputViewerPreviewDouble(glassWidth)} x {FormatOutputViewerPreviewDouble(glassHeight)} mm";
            if (invalidCellCount > 0)
            {
                summary += $" / {invalidCellCount:N0} Invalid Cells";
            }

            return new ST_CORRECTION_OFFSET_VIEWER_PREVIEW(cellImage, translatedCellLabels, summary);
        }

        private static StreamGeometry? BuildOutputViewerCellBoundaryGeometry(double frameLeft, double frameTop, double scale, double pixelSize, int pixelCountX, int pixelCountY, double firstX, double firstY, double rotationDegrees)
        {
            if (pixelCountX <= 0 || pixelCountY <= 0 || !double.IsFinite(pixelSize) || pixelSize <= 0.0)
            {
                return null;
            }

            var radians = rotationDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            var halfPixel = pixelSize / 2.0;
            var localMaxX = ((pixelCountX - 1) * pixelSize) + halfPixel;
            var localMaxY = ((pixelCountY - 1) * pixelSize) + halfPixel;
            var localCorners = new[]
            {
                new Point(-halfPixel, -halfPixel),
                new Point(localMaxX, -halfPixel),
                new Point(localMaxX, localMaxY),
                new Point(-halfPixel, localMaxY)
            };
            var canvasCorners = localCorners.Select(local =>
            {
                var x = firstX + (local.X * cos) - (local.Y * sin);
                var y = firstY + (local.X * sin) + (local.Y * cos);
                return new Point(frameLeft + (x * scale), frameTop + (y * scale));
            }).ToArray();
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(canvasCorners[0], false, true);
                geometryContext.PolyLineTo(canvasCorners.Skip(1).ToArray(), true, false);
            }

            geometry.Freeze();
            return geometry;
        }

        private static void DrawOutputViewerPreviewAxisIndicator(DrawingContext context, Rect glassRect)
        {
            var axisBrush = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            axisBrush.Freeze();
            var axisPen = new Pen(axisBrush, 2.0)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            axisPen.Freeze();
            var origin = new Point(Math.Max(4.0, glassRect.Left - 8.0), Math.Max(4.0, glassRect.Top - 12.0));
            var xEnd = new Point(origin.X + 46.0, origin.Y);
            var yEnd = new Point(origin.X, origin.Y + 42.0);
            context.DrawLine(axisPen, origin, xEnd);
            context.DrawLine(axisPen, new Point(xEnd.X - 8.0, xEnd.Y - 4.0), xEnd);
            context.DrawLine(axisPen, new Point(xEnd.X - 8.0, xEnd.Y + 4.0), xEnd);
            context.DrawLine(axisPen, origin, yEnd);
            context.DrawLine(axisPen, new Point(yEnd.X - 4.0, yEnd.Y - 8.0), yEnd);
            context.DrawLine(axisPen, new Point(yEnd.X + 4.0, yEnd.Y - 8.0), yEnd);
            DrawOutputViewerPreviewAxisText(context, "X+", new Point(xEnd.X + 4.0, Math.Max(2.0, xEnd.Y - 10.0)), axisBrush);
            DrawOutputViewerPreviewAxisText(context, "Y+", new Point(yEnd.X + 4.0, yEnd.Y - 4.0), axisBrush);
        }

        private static void DrawOutputViewerPreviewAxisText(DrawingContext context, string text, Point point, Brush brush)
        {
            var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13.0, brush, 1.0);
            context.DrawText(label, point);
        }

        private static IReadOnlyDictionary<string, string> BuildOutputViewerCoordinateParameterMap(IReadOnlyDictionary<string, string> parameters)
        {
            var viewerParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            var headCount = Math.Clamp(ReadOutputViewerInt(viewerParameters, 8, "HEAD_COUNT"), 1, 8);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                viewerParameters[$"H{headNo:00}_USE"] = "ON";
            }

            return viewerParameters;
        }

        private static int ReadOutputViewerInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!parameters.TryGetValue(key, out var value))
                {
                    continue;
                }

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intResult))
                {
                    return intResult;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    return (int)Math.Round(doubleResult);
                }
            }

            return defaultValue;
        }

        private static double ReadOutputViewerDouble(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!parameters.TryGetValue(key, out var value))
                {
                    continue;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result))
                {
                    return result;
                }
            }

            return defaultValue;
        }

        private static double? ReadOutputViewerNullableDouble(IReadOnlyDictionary<string, string> parameters, string key)
        {
            return parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : null;
        }

        private static string FormatOutputViewerPreviewDouble(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatOffsetPair(double x, double y)
        {
            return $"{FormatOffsetValue(x)} / {FormatOffsetValue(y)}";
        }

        private static string FormatOffsetValue(double value)
        {
            return value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        }

        private void Execute(object? parameter)
        {
            var command = parameter?.ToString()?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            if (command == "CALCULATE")
            {
                if (!CRecipeConfirmDialog.ShowConfirmation("보정값 계산", "보정값을 계산하시겠습니까?"))
                {
                    return;
                }
            }

            if (command == "APPLY")
            {
                if (!CRecipeConfirmDialog.ShowConfirmation("보정값 적용", "계산된 보정값을 적용하시겠습니까?"))
                {
                    return;
                }
            }

            if (command == "SAVE")
            {
                var saveTitle = "보정값 저장";
                var saveMessage = "적용된 보정값을 저장하시겠습니까?";
                if (!CRecipeConfirmDialog.ShowConfirmation(saveTitle, saveMessage))
                {
                    return;
                }
            }

            _statusReporter($"Correction {command} requested. Tab={_selectedTab}.");
        }

        private void ApplyTabData(string selectedTab)
        {
            global::Drilling.UI.Menu.Menus.ST_CORRECTION_TAB BuildCORRECTION_TAB(string tab)
            {
                return new ST_CORRECTION_TAB(tab, tab.Equals(selectedTab, StringComparison.OrdinalIgnoreCase));
            }

            Tabs = CorrectionTabs.Select(BuildCORRECTION_TAB).ToArray();
            (SummaryItems, SourceRows, CandidateRows, ApplyRows, DetailItems, HistoryRows) = selectedTab switch
            {
                "ALIGN/DISTORTION" or "REVIEW" => ([], [], [], [], [], []),
                "APC / ICR" => CreateApcIcrData(),
                "VIEWER / HISTORY" => CreateOutputHistoryData(),
                _ => ([], [], [], [], [], [])};
            OnPropertyChanged(nameof(Tabs));
            OnPropertyChanged(nameof(IsAlignDistortionTab));
            OnPropertyChanged(nameof(IsReviewCorrectionTab));
            OnPropertyChanged(nameof(IsCorrectionCommandsVisible));
            OnPropertyChanged(nameof(IsOtherCorrectionTab));
            OnPropertyChanged(nameof(IsOutputHistoryTab));
            OnPropertyChanged(nameof(SelectedTab));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
        }

        private async Task LoadReviewResult()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Load Review Result",
                Filter = "Review Result CSV (*.csv)|*.csv|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Directory.Exists(_reviewResultFile.RootPath) ? _reviewResultFile.RootPath : AppContext.BaseDirectory
            };
            if (dialog.ShowDialog() != true)
            {
                _statusReporter("Review Result load canceled.");
                return;
            }

            await LoadReviewResultFile(dialog.FileName, showCompletionDialog: true, confirmRecipeMismatch: true);
        }

        private async Task<bool> LoadReviewResultFile(string filePath, bool showCompletionDialog = false, bool confirmRecipeMismatch = false)
        {
            try
            {
                var result = await _reviewResultFile.Load(filePath);
                var recipeMismatch = HasSelectedRecipeMismatch(result);
                if (recipeMismatch)
                {
                    if (!confirmRecipeMismatch)
                    {
                        throw CreateRecipeMismatchException(result);
                    }

                    var selectedRecipeId = NormalizeRecipeId(_selectedRecipeIdProvider());
                    if (!CRecipeConfirmDialog.ShowConfirmation("Recipe 불일치", $"현재 Recipe: {selectedRecipeId}\nReview Result Recipe: {NormalizeRecipeId(result.RecipeId)}\n\nRecipe가 서로 다릅니다. 조회하시겠습니까?", "조회"))
                    {
                        _statusReporter("Review Result load canceled because the Recipe does not match.");
                        return false;
                    }
                }

                _loadedReviewResult = result;
                var settingState = await LoadCurrentSettingState(GetReviewResultHeadNos(result));
                _currentSettingState = settingState.Summary;
                _currentSettingDetail = settingState.Detail;
                _currentVisionSettingState = settingState.Vision;
                _currentScannerSettingState = settingState.Scanner;
                CurrentVisionFlipRow = settingState.VisionRow;
                CurrentScannerFlipRows = settingState.ScannerRows;
                _currentReviewOffsetRecipeName = "Recipe: -";
                ReviewResultRows = result.Rows.Select(row => new ST_CORRECTION_REVIEW_RESULT_ROW(row.ShotKey, $"H{row.HeadNo:00}", row.CellNo, row.ErrorX, row.ErrorY, row.Judge)).ToArray();
                string? offsetLoadWarning = null;
                try
                {
                    CurrentOffsetRows = await LoadCurrentReviewOffsets(result);
                }
                catch (Exception exception)
                {
                    CurrentOffsetRows = [];
                    offsetLoadWarning = exception.Message;
                }

                CalculatedOffsetRows = [];
                ApplyPreviewRows = [];
                _calculatedSettingDetail = "";
                _hasPendingReviewOffsetApply = false;
                _reviewLoadStatus = recipeMismatch ? $"{result.Rows.Count} rows loaded. Recipe mismatch: view only." : offsetLoadWarning is null ? $"{result.Rows.Count} rows loaded." : $"{result.Rows.Count} rows loaded. Current Review Offset unavailable: {offsetLoadWarning}";
                _reviewLoadState = recipeMismatch || offsetLoadWarning is not null ? "WARN" : "OK";
                _reviewDataHistory.Insert(0, new ST_CORRECTION_HISTORY_ROW(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Environment.UserName, "REVIEW DATA", result.FileName, "-", $"{result.Rows.Count} Rows", "Loaded"));
                ApplyTabData(_selectedTab);
                _statusReporter($"Review Result loaded: {result.FileName} / {result.Rows.Count} rows.");
                await _refreshCurrentScreen();
                if (showCompletionDialog)
                {
                    CRecipeConfirmDialog.ShowCompletion("검사 결과 불러오기 완료", $"{result.FileName} 파일을 불러왔습니다.");
                }

                return true;
            }
            catch (Exception exception)
            {
                var message = $"Review Result load failed: {exception.Message}";
                _reviewLoadStatus = message;
                _reviewLoadState = "NG";
                _reviewDataHistory.Insert(0, new ST_CORRECTION_HISTORY_ROW(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Environment.UserName, "REVIEW DATA", Path.GetFileName(filePath), "-", "Not Loaded", "NG"));
                ApplyTabData(_selectedTab);
                _statusReporter(message);
                await _refreshCurrentScreen();
                return false;
            }
        }

        private async Task CalculateReviewOffsets(bool replacePendingApply = false)
        {
            if (_loadedReviewResult is null)
            {
                await ReportReviewDataCommand("Calculate", "Load a Review Result CSV before calculating.", "WARN");
                return;
            }

            if (HasSelectedRecipeMismatch(_loadedReviewResult))
            {
                await ReportReviewDataCommand("Calculate", "Review Result Recipe와 현재 Recipe가 다릅니다. 조회만 가능하며 계산하려면 같은 Recipe를 선택해 주세요.", "WARN");
                return;
            }

            if (_hasPendingReviewOffsetApply)
            {
                if (!replacePendingApply)
                {
                    await ReportReviewDataCommand("Calculate", "Review Offsets are pending save. Save or load the Review Result again before recalculating.", "WARN");
                    return;
                }

                _hasPendingReviewOffsetApply = false;
                ApplyPreviewRows = [];
            }

            _isReviewOperationRunning = true;
            ExecuteCommand.NotifyCanExecuteChanged();
            _reviewLoadStatus = "Calculating Review Offsets...";
            _reviewLoadState = "WAIT";
            ApplyTabData(_selectedTab);
            NotifyReviewDataPropertiesChanged();
            _statusReporter(_reviewLoadStatus);
            try
            {
                var result = _loadedReviewResult;
                var recipe = await LoadReviewResultRecipe(_loadedReviewResult);
                // Plan construction materializes the process coordinate plan and can
                // project thousands of Review points. Keep it off the dispatcher so
                // the Correction screen remains responsive while Calculate runs.
                var correctionRows = await Task.Run(() => CreateCorrectionRows(result, recipe));
                var flipType = await LoadCurrentVisionFlipType();
                var scannerAxisTransforms = LoadCurrentScannerAxisTransforms(GetCorrectionHeadNos(correctionRows));
                _currentVisionSettingState = FormatVisionFlipSummary(flipType);
                _currentScannerSettingState = FormatScannerSettingSummary(scannerAxisTransforms);
                _currentSettingState = FormatAxisSettingSummary(_currentVisionSettingState, _currentScannerSettingState);
                _currentSettingDetail = FormatAxisSettingDetail(flipType, scannerAxisTransforms);
                CurrentVisionFlipRow = CreateVisionFlipRow(flipType);
                CurrentScannerFlipRows = CreateScannerFlipRows(scannerAxisTransforms);
                var currentReviewOffsetFile = await _reviewOffsetFile.LoadLatest(recipe.Id);
                _currentReviewOffsetRecipeName = FormatReviewOffsetSource(recipe.Id, currentReviewOffsetFile);
                var offsetParameters = BuildOutputViewerParameterMap(recipe, LoadSimpleOffsetSettings());
                var calculation = await Task.Run(() =>
                {
                    var currentRows = CreateCurrentReviewOffsets(currentReviewOffsetFile, correctionRows, offsetParameters);
                    var calculatedRows = new List<ST_CORRECTION_REVIEW_OFFSET_ROW>(correctionRows.Count);
                    foreach (var row in correctionRows)
                    {
                        var shotKey = NormalizeReviewShotKey(row.ShotKey, row.CellNo);
                        var calculated = CReviewManager.VisionErrorToScannerOffset(row.ErrorX, row.ErrorY, flipType, scannerAxisTransforms[row.HeadNo]);
                        var judge = row.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase) ? "NG" : "OK";
                        calculatedRows.Add(new ST_CORRECTION_REVIEW_OFFSET_ROW(shotKey, $"H{row.HeadNo:00}", calculated.X, calculated.Y, judge));
                    }

                    return (CurrentRows: currentRows, CalculatedRows: calculatedRows);
                });
                CurrentOffsetRows = calculation.CurrentRows;
                CalculatedOffsetRows = calculation.CalculatedRows;
                ApplyPreviewRows = [];
                _calculatedSettingDetail = _currentSettingDetail;
                await ReportReviewDataCommand("Calculate", $"{calculation.CalculatedRows.Count} Review Offsets calculated. Select Apply to create Apply Preview.", "OK", $"{_currentSettingState} / {calculation.CalculatedRows.Count} Rows");
            }
            catch (Exception exception)
            {
                CalculatedOffsetRows = [];
                ApplyPreviewRows = [];
                _calculatedSettingDetail = "";
                await ReportReviewDataCommand("Calculate", $"Review Offset calculation failed: {exception.Message}", "NG");
            }
            finally
            {
                _isReviewOperationRunning = false;
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }

        private async Task ApplyReviewOffsets()
        {
            if (_loadedReviewResult is null)
            {
                await ReportReviewDataCommand("Apply", "Load and calculate a Review Result before applying.", "WARN");
                return;
            }

            if (CalculatedOffsetRows.Count == 0)
            {
                await ReportReviewDataCommand("Apply", "Calculate the Review Offsets before applying.", "WARN");
                return;
            }

            if (HasSelectedRecipeMismatch(_loadedReviewResult))
            {
                await ReportReviewDataCommand("Apply", "Review Result Recipe와 현재 Recipe가 다릅니다. 보정값을 적용할 수 없습니다.", "WARN");
                return;
            }

            ST_CORRECTION_AXIS_SETTING_STATE currentSettingState;
            try
            {
                currentSettingState = await LoadCurrentSettingState(GetReviewResultHeadNos(_loadedReviewResult));
            }
            catch (Exception exception)
            {
                await ReportReviewDataCommand("Apply", $"현재 Flip 설정을 확인하지 못했습니다: {exception.Message}", "NG");
                return;
            }

            var flipSettingsChanged = !_calculatedSettingDetail.Equals(currentSettingState.Detail, StringComparison.Ordinal);
            if (flipSettingsChanged)
            {
                ApplyCurrentSettingState(currentSettingState);
                if (!CRecipeConfirmDialog.ShowConfirmation("Flip 설정 변경 확인", "Calculate 이후 Vision 또는 Scanner Flip 설정이 변경되었습니다.\n변경된 Flip 설정으로 다시 계산한 뒤 적용하시겠습니까?", "재계산 후 적용"))
                {
                    await ReportReviewDataCommand("Apply", "Flip 설정 변경으로 적용을 취소했습니다. 기존 계산값은 적용되지 않았습니다.", "WARN");
                    return;
                }

                await CalculateReviewOffsets(replacePendingApply: true);
                if (CalculatedOffsetRows.Count == 0 || !_calculatedSettingDetail.Equals(currentSettingState.Detail, StringComparison.Ordinal))
                {
                    return;
                }
            }
            else if (!CRecipeConfirmDialog.ShowConfirmation("보정값 적용", "계산된 보정값을 적용하시겠습니까?"))
            {
                return;
            }

            if (CalculatedOffsetRows.Count != CurrentOffsetRows.Count)
            {
                await ReportReviewDataCommand("Apply", $"Current Review Offset is incomplete: {CurrentOffsetRows.Count}/{CalculatedOffsetRows.Count} rows.", "NG");
                return;
            }

            string SelectShotKey(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row.ShotKey;
            }

            var currentKeys = CurrentOffsetRows.Select(SelectShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return !currentKeys.Contains(row.ShotKey);
            }

            if (CalculatedOffsetRows.Any(MatchesItem))
            {
                await ReportReviewDataCommand("Apply", "Calculated Review Offset contains a Shot that is not present in the current Review Offsets.", "NG");
                return;
            }

            string SelectShotKey2(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row.ShotKey;
            }

            string SelectKey(global::System.Linq.IGrouping<string, global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW> group)
            {
                return group.Key;
            }

            global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW SelectValue(global::System.Linq.IGrouping<string, global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW> group)
            {
                return group.Last();
            }

            var currentOffsets = CurrentOffsetRows.GroupBy(SelectShotKey2, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
            global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW SelectItem(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                var current = currentOffsets[row.ShotKey];
                var preview = row with
                {
                    OffsetXValue = current.OffsetXValue + row.OffsetXValue,
                    OffsetYValue = current.OffsetYValue + row.OffsetYValue
                };
                return preview with
                {
                    State = HasOffsetChanged(current, preview) ? "PENDING" : "CURRENT"
                };
            }

            ApplyPreviewRows = CalculatedOffsetRows.Select(SelectItem).ToArray();
            bool MatchesItem2(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row.State.Equals("PENDING", StringComparison.OrdinalIgnoreCase);
            }

            _hasPendingReviewOffsetApply = ApplyPreviewRows.Any(MatchesItem2);
            if (!_hasPendingReviewOffsetApply)
            {
                await ReportReviewDataCommand("Apply", "There are no Review Offset changes to apply.", "OK", "0 Rows / No Change");
                return;
            }

            bool MatchesState(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row.State.Equals("PENDING", StringComparison.OrdinalIgnoreCase);
            }

            bool MatchesState2(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row.State.Equals("PENDING", StringComparison.OrdinalIgnoreCase);
            }

            await ReportReviewDataCommand("Apply", $"{ApplyPreviewRows.Count(MatchesState)} calculated values are shown in Apply Preview. Save is required.", "WARN", $"{ApplyPreviewRows.Count(MatchesState2)} Rows / Pending Save");
        }

        private async Task SaveAppliedReviewOffsets()
        {
            if (_loadedReviewResult is null)
            {
                await ReportReviewDataCommand("Save", "Apply the calculated Review Offsets before saving.", "WARN");
                return;
            }

            if (HasSelectedRecipeMismatch(_loadedReviewResult))
            {
                await ReportReviewDataCommand("Save", "Review Result Recipe와 현재 Recipe가 다릅니다. 보정값을 저장할 수 없습니다.", "WARN");
                return;
            }

            if (!_hasPendingReviewOffsetApply)
            {
                await ReportReviewDataCommand("Save", "Apply the calculated Review Offsets before saving.", "WARN");
                return;
            }

            bool MatchesState(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row.State.Equals("PENDING", StringComparison.OrdinalIgnoreCase);
            }

            global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW SelectValue(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row with
                {
                    State = row.State.Equals("PENDING", StringComparison.OrdinalIgnoreCase) ? "SAVED" : row.State
                };
            }

            global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW SelectCORRECTION_REVIEW_OFFSET_ROW(global::Drilling.UI.Menu.Menus.ST_CORRECTION_REVIEW_OFFSET_ROW row)
            {
                return row with
                {
                    State = "CURRENT"
                };
            }

            var pendingRows = ApplyPreviewRows.Where(MatchesState).ToArray();
            if (pendingRows.Length == 0)
            {
                _hasPendingReviewOffsetApply = false;
                await ReportReviewDataCommand("Save", "There are no pending Review Offsets to save.", "OK", "0 Rows / No Change");
                return;
            }

            _isReviewOperationRunning = true;
            ExecuteCommand.NotifyCanExecuteChanged();
            _reviewLoadStatus = "Saving Review Offsets...";
            _reviewLoadState = "WAIT";
            ApplyTabData(_selectedTab);
            NotifyReviewDataPropertiesChanged();
            _statusReporter(_reviewLoadStatus);
            try
            {
                var selectedRecipeId = NormalizeRecipeId(_selectedRecipeIdProvider());
                if (string.IsNullOrWhiteSpace(selectedRecipeId))
                {
                    selectedRecipeId = NormalizeRecipeId(_loadedReviewResult.RecipeId);
                }

                var latestOffsets = await _reviewOffsetFile.LoadLatest(selectedRecipeId);
                var offsetRecipe = await LoadReviewResultRecipe(_loadedReviewResult);
                var offsetParameters = BuildOutputViewerParameterMap(offsetRecipe, LoadSimpleOffsetSettings());
                var savedOffsetFile = await Task.Run(() =>
                {
                    var mergedRows = MergeReviewOffsetRows(latestOffsets.Rows, ApplyPreviewRows, offsetParameters);
                    return _reviewOffsetFile.SaveNew(selectedRecipeId, mergedRows).GetAwaiter().GetResult();
                });
                ApplyPreviewRows = ApplyPreviewRows.Select(SelectValue).ToArray();
                CurrentOffsetRows = ApplyPreviewRows.Select(SelectCORRECTION_REVIEW_OFFSET_ROW).ToArray();
                _hasPendingReviewOffsetApply = false;
                _currentReviewOffsetRecipeName = FormatReviewOffsetSource(selectedRecipeId, savedOffsetFile);
                await ReportReviewDataCommand("Save", $"{pendingRows.Length} Review Offsets saved to {savedOffsetFile.FileName}.", "OK", $"{savedOffsetFile.FileName} / {savedOffsetFile.Rows.Count} Rows");
                CRecipeConfirmDialog.ShowCompletion("Review Offset 저장 완료", $"{savedOffsetFile.FileName} 파일에 Review Offset을 저장하였습니다.");
            }
            catch (Exception exception)
            {
                await ReportReviewDataCommand("Save", $"Review Offset save failed: {exception.Message}", "NG");
            }
            finally
            {
                _isReviewOperationRunning = false;
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }

        private static bool HasOffsetChanged(ST_CORRECTION_REVIEW_OFFSET_ROW before, ST_CORRECTION_REVIEW_OFFSET_ROW after)
        {
            const double comparisonTolerance = 0.0000005;
            return Math.Abs(before.OffsetXValue - after.OffsetXValue) >= comparisonTolerance || Math.Abs(before.OffsetYValue - after.OffsetYValue) >= comparisonTolerance;
        }

        private Task ReportReviewDataCommand(string item, string message, string result, string? historyAfter = null)
        {
            _reviewLoadStatus = message;
            _reviewLoadState = result;
            _reviewDataHistory.Insert(0, new ST_CORRECTION_HISTORY_ROW(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Environment.UserName, "REVIEW DATA", item, LoadedReviewFileName, historyAfter ?? message, result));
            ApplyTabData(_selectedTab);
            _statusReporter(message);
            NotifyReviewDataPropertiesChanged();
            return Task.CompletedTask;
        }

        private void NotifyReviewDataPropertiesChanged()
        {
            OnPropertyChanged(nameof(SummaryItems));
            OnPropertyChanged(nameof(SourceRows));
            OnPropertyChanged(nameof(CandidateRows));
            OnPropertyChanged(nameof(ApplyRows));
            OnPropertyChanged(nameof(DetailItems));
            OnPropertyChanged(nameof(HistoryRows));
            OnPropertyChanged(nameof(ReviewResultRows));
            OnPropertyChanged(nameof(CalculatedOffsetRows));
            OnPropertyChanged(nameof(CurrentOffsetRows));
            OnPropertyChanged(nameof(ApplyPreviewRows));
            OnPropertyChanged(nameof(CurrentVisionFlipRow));
            OnPropertyChanged(nameof(CurrentScannerFlipRows));
            OnPropertyChanged(nameof(LoadedReviewFileName));
            OnPropertyChanged(nameof(LoadedReviewSettingSummary));
            OnPropertyChanged(nameof(LoadedReviewRule));
            OnPropertyChanged(nameof(LoadedReviewSettingDetail));
            OnPropertyChanged(nameof(ReviewLoadStatus));
            OnPropertyChanged(nameof(ReviewLoadStatusBrush));
            OnPropertyChanged(nameof(CurrentSettingState));
            OnPropertyChanged(nameof(CurrentSettingDetail));
            OnPropertyChanged(nameof(CurrentVisionSettingState));
            OnPropertyChanged(nameof(CurrentScannerSettingState));
            OnPropertyChanged(nameof(CurrentReviewOffsetRecipeName));
        }

        private async Task<IReadOnlyList<ST_CORRECTION_REVIEW_OFFSET_ROW>> LoadCurrentReviewOffsets(ST_REVIEW_RESULT_FILE_DATA result)
        {
            var recipe = await LoadReviewResultRecipe(result);
            var reviewOffsetFile = await _reviewOffsetFile.LoadLatest(recipe.Id);
            _currentReviewOffsetRecipeName = FormatReviewOffsetSource(recipe.Id, reviewOffsetFile);
            var offsetValues = CreateReviewOffsetLookup(reviewOffsetFile.Rows);
            var parameters = BuildOutputViewerParameterMap(recipe, LoadSimpleOffsetSettings());
            var rows = new List<ST_CORRECTION_REVIEW_OFFSET_ROW>(result.Rows.Count);
            foreach (var row in result.Rows)
            {
                var shotKey = NormalizeReviewShotKey(row.ShotKey, row.CellNo);
                var saved = offsetValues.GetValueOrDefault(shotKey);
                var offset = saved is null ? (0.0, 0.0) : CReviewOffsetData.ToScanner(parameters, row.HeadNo, saved.ReviewOffsetX, saved.ReviewOffsetY, saved.IsDesignCoordinate);
                rows.Add(new ST_CORRECTION_REVIEW_OFFSET_ROW(shotKey, $"H{row.HeadNo:00}", offset.Item1, offset.Item2, "CURRENT"));
            }

            return rows;
        }

        private async Task<ST_RECIPE_DEFINITION> LoadReviewResultRecipe(ST_REVIEW_RESULT_FILE_DATA result)
        {
            var reviewRecipeId = NormalizeRecipeId(result.RecipeId);
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION item)
            {
                return NormalizeRecipeId(item.Id).Equals(reviewRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            var recipe = (await _recipeManager.LoadRecipes()).FirstOrDefault(MatchesItem);
            if (recipe is null)
            {
                throw new InvalidOperationException($"Review Result recipe could not be loaded: {reviewRecipeId}.");
            }

            return recipe;
        }

        private IReadOnlyList<ST_REVIEW_PLAN_POINT> CreateCorrectionRows(ST_REVIEW_RESULT_FILE_DATA result, ST_RECIPE_DEFINITION recipe)
        {
            var beamNo = Math.Max(1, result.BeamNo);
            var requestedShotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in result.Rows)
            {
                requestedShotKeys.Add(NormalizeReviewShotKey(row.ShotKey, row.CellNo));
            }

            var allPlan = _reviewManager.CreateAllPlan(recipe, beamNo);
            var sourceByShotKey = new Dictionary<string, ST_REVIEW_PLAN_POINT>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in allPlan.Points)
            {
                var numericShotKey = NormalizeReviewShotKey(point.ShotKey, point.CellNo);
                var displayShotKey = NormalizeReviewShotKey($"CELL{point.CellNo}_{FormatReviewShotName(point)}", point.CellNo);
                if (requestedShotKeys.Contains(numericShotKey))
                {
                    sourceByShotKey[numericShotKey] = point;
                }

                if (requestedShotKeys.Contains(displayShotKey))
                {
                    sourceByShotKey[displayShotKey] = point;
                }

                if (sourceByShotKey.Count == requestedShotKeys.Count)
                {
                    break;
                }
            }

            var measuredRows = new List<ST_REVIEW_PLAN_POINT>(result.Rows.Count);
            foreach (var row in result.Rows)
            {
                var shotKey = NormalizeReviewShotKey(row.ShotKey, row.CellNo);
                if (!sourceByShotKey.TryGetValue(shotKey, out var sourcePoint))
                {
                    throw new InvalidDataException($"Review Result Shot is not present in the selected Recipe: {shotKey}.");
                }

                if (sourcePoint.HeadNo != row.HeadNo)
                {
                    throw new InvalidDataException($"Review Result Head does not match the selected Recipe: {shotKey} / File=H{row.HeadNo:00}, Recipe=H{sourcePoint.HeadNo:00}.");
                }

                var isNg = row.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase);
                measuredRows.Add(sourcePoint with { Use = true, BeamNo = beamNo, ErrorX = row.ErrorX, ErrorY = row.ErrorY, State = isNg ? EN_REVIEW_POINT_STATE.Ng : EN_REVIEW_POINT_STATE.Ok, Judge = isNg ? "NG" : "OK" });
            }

            var rule = ParseCorrectionRule(result.Rule);
            if (rule is null)
            {
                return UseDisplayShotKeys(measuredRows);
            }

            var scope = result.RuleScope.Equals("HEAD_CELL", StringComparison.OrdinalIgnoreCase) ? EN_REVIEW_CORRECTION_SCOPE.HeadCell : EN_REVIEW_CORRECTION_SCOPE.Head;
            var roughReference = ParseRoughReference(result.RoughReference);
            var reviewCorrectionPlan = new ST_REVIEW_CORRECTION_PLAN(rule.Value, scope, allPlan.Points, roughReference, result.FineLineCount);
            var correctionPlan = allPlan with
            {
                Points = measuredRows,
                CorrectionPlan = reviewCorrectionPlan
            };
            return UseDisplayShotKeys(CReviewManager.ProjectCorrectionRows(correctionPlan, measuredRows));
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> UseDisplayShotKeys(IReadOnlyList<ST_REVIEW_PLAN_POINT> rows)
        {
            var displayRows = new List<ST_REVIEW_PLAN_POINT>(rows.Count);
            foreach (var row in rows)
            {
                displayRows.Add(row with { ShotKey = $"CELL{row.CellNo}_{FormatReviewShotName(row)}" });
            }

            return displayRows;
        }

        private static EN_REVIEW_CORRECTION_RULE? ParseCorrectionRule(string rule)
        {
            if (rule.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
            {
                return EN_REVIEW_CORRECTION_RULE.Simple;
            }

            if (rule.Equals("ROUGH", StringComparison.OrdinalIgnoreCase))
            {
                return EN_REVIEW_CORRECTION_RULE.Rough;
            }

            if (rule.Equals("FINE", StringComparison.OrdinalIgnoreCase))
            {
                return EN_REVIEW_CORRECTION_RULE.Fine;
            }

            if (rule.Equals("EDGE", StringComparison.OrdinalIgnoreCase))
            {
                return EN_REVIEW_CORRECTION_RULE.Edge;
            }

            return null;
        }

        private static EN_REVIEW_REFERENCE_LINE ParseRoughReference(string reference)
        {
            if (reference.Equals("CENTER", StringComparison.OrdinalIgnoreCase))
            {
                return EN_REVIEW_REFERENCE_LINE.Center;
            }

            if (reference.Equals("LAST", StringComparison.OrdinalIgnoreCase))
            {
                return EN_REVIEW_REFERENCE_LINE.Last;
            }

            return EN_REVIEW_REFERENCE_LINE.First;
        }

        private static IReadOnlyList<ST_CORRECTION_REVIEW_OFFSET_ROW> CreateCurrentReviewOffsets(ST_REVIEW_OFFSET_FILE_DATA reviewOffsetFile, IReadOnlyList<ST_REVIEW_PLAN_POINT> correctionRows, IReadOnlyDictionary<string, string> parameters)
        {
            var offsetValues = CreateReviewOffsetLookup(reviewOffsetFile.Rows);
            var rows = new List<ST_CORRECTION_REVIEW_OFFSET_ROW>(correctionRows.Count);
            foreach (var point in correctionRows)
            {
                var shotKey = NormalizeReviewShotKey(point.ShotKey, point.CellNo);
                var saved = offsetValues.GetValueOrDefault(shotKey);
                var offset = saved is null ? (0.0, 0.0) : CReviewOffsetData.ToScanner(parameters, point.HeadNo, saved.ReviewOffsetX, saved.ReviewOffsetY, saved.IsDesignCoordinate);
                rows.Add(new ST_CORRECTION_REVIEW_OFFSET_ROW(shotKey, $"H{point.HeadNo:00}", offset.Item1, offset.Item2, "CURRENT"));
            }

            return rows;
        }

        private static IEnumerable<int> GetCorrectionHeadNos(IReadOnlyList<ST_REVIEW_PLAN_POINT> correctionRows)
        {
            foreach (var row in correctionRows)
            {
                yield return row.HeadNo;
            }
        }

        private async Task<ST_CORRECTION_AXIS_SETTING_STATE> LoadCurrentSettingState(IEnumerable<int> headNos)
        {
            var flipType = await LoadCurrentVisionFlipType();
            var scannerAxisTransforms = LoadCurrentScannerAxisTransforms(headNos);
            var vision = FormatVisionFlipSummary(flipType);
            var scanner = FormatScannerSettingSummary(scannerAxisTransforms);
            return new ST_CORRECTION_AXIS_SETTING_STATE(FormatAxisSettingSummary(vision, scanner), FormatAxisSettingDetail(flipType, scannerAxisTransforms), vision, scanner, CreateVisionFlipRow(flipType), CreateScannerFlipRows(scannerAxisTransforms));
        }

        private void ApplyCurrentSettingState(ST_CORRECTION_AXIS_SETTING_STATE settingState)
        {
            _currentSettingState = settingState.Summary;
            _currentSettingDetail = settingState.Detail;
            _currentVisionSettingState = settingState.Vision;
            _currentScannerSettingState = settingState.Scanner;
            CurrentVisionFlipRow = settingState.VisionRow;
            CurrentScannerFlipRows = settingState.ScannerRows;
        }

        private static ST_CORRECTION_AXIS_FLIP_ROW CreateVisionFlipRow(EN_VISION_FLIP_TYPE flipType)
        {
            return new ST_CORRECTION_AXIS_FLIP_ROW("VISION", flipType.HasFlag(EN_VISION_FLIP_TYPE.XFlip), flipType.HasFlag(EN_VISION_FLIP_TYPE.YFlip), flipType.HasFlag(EN_VISION_FLIP_TYPE.XyFlip));
        }

        private static string FormatReviewSettingDetail(ST_REVIEW_RESULT_FILE_DATA? result)
        {
            if (result is null)
            {
                return "";
            }

            var rule = string.IsNullOrWhiteSpace(result.Rule) ? "ALL" : result.Rule.ToUpperInvariant();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.RuleScope))
            {
                parts.Add(result.RuleScope.Replace("_", " + ", StringComparison.Ordinal));
            }

            if (rule == "SIMPLE")
            {
                var cell = result.ReferenceCellNo is null ? "Cell-" : $"Cell{result.ReferenceCellNo.Value}";
                var shot = string.IsNullOrWhiteSpace(result.ReferenceShot) ? "-" : result.ReferenceShot;
                parts.Add($"{cell} / {shot}");
            }
            else if (rule == "ROUGH" && !string.IsNullOrWhiteSpace(result.RoughReference))
            {
                parts.Add(result.RoughReference.ToUpperInvariant());
            }
            else if (rule == "FINE" && result.FineLineCount > 0)
            {
                parts.Add(result.FineLineCount == 1 ? "1 Line" : $"{result.FineLineCount} Lines");
            }

            parts.Add($"Beam{Math.Max(1, result.BeamNo)}");
            if (!string.IsNullOrWhiteSpace(result.InspectionMethod))
            {
                parts.Add(result.InspectionMethod.ToUpperInvariant());
            }

            return string.Join(" · ", parts);
        }

        private static IReadOnlyList<ST_CORRECTION_AXIS_FLIP_ROW> CreateScannerFlipRows(IReadOnlyDictionary<int, ST_SCANNER_AXIS_TRANSFORM> scannerAxisTransforms)
        {
            static int GetHeadNo(KeyValuePair<int, ST_SCANNER_AXIS_TRANSFORM> item)
            {
                return item.Key;
            }

            var rows = new List<ST_CORRECTION_AXIS_FLIP_ROW>(scannerAxisTransforms.Count);
            foreach (var item in scannerAxisTransforms.OrderBy(GetHeadNo))
            {
                rows.Add(new ST_CORRECTION_AXIS_FLIP_ROW($"H{item.Key:00}", item.Value.XFlip, item.Value.YFlip, item.Value.XyFlip));
            }

            return rows;
        }

        private static IEnumerable<int> GetReviewResultHeadNos(ST_REVIEW_RESULT_FILE_DATA result)
        {
            foreach (var row in result.Rows)
            {
                yield return row.HeadNo;
            }
        }

        private static string FormatAxisSettingSummary(string vision, string scanner)
        {
            return $"Vision: {vision} | Scanner: {scanner}";
        }

        private static string FormatScannerSettingSummary(IReadOnlyDictionary<int, ST_SCANNER_AXIS_TRANSFORM> scannerAxisTransforms)
        {
            static int GetHeadNo(KeyValuePair<int, ST_SCANNER_AXIS_TRANSFORM> item)
            {
                return item.Key;
            }

            if (scannerAxisTransforms.Count == 0)
            {
                return "-";
            }

            var orderedTransforms = scannerAxisTransforms.OrderBy(GetHeadNo).ToArray();
            var groupedHeadNos = new Dictionary<ST_SCANNER_AXIS_TRANSFORM, List<int>>();
            foreach (var item in orderedTransforms)
            {
                if (!groupedHeadNos.TryGetValue(item.Value, out var headNos))
                {
                    headNos = [];
                    groupedHeadNos.Add(item.Value, headNos);
                }

                headNos.Add(item.Key);
            }

            var settingGroups = new List<string>();
            foreach (var group in groupedHeadNos)
            {
                settingGroups.Add($"{FormatHeadRange(group.Value)} · {FormatScannerFlipSummary(group.Key)}");
            }

            return string.Join("  /  ", settingGroups);
        }

        private static string FormatHeadRange(IReadOnlyList<int> headNos)
        {
            if (headNos.Count == 0)
            {
                return "-";
            }

            var ranges = new List<string>();
            var rangeStart = headNos[0];
            var rangeEnd = rangeStart;
            for (var index = 1; index < headNos.Count; index++)
            {
                var headNo = headNos[index];
                if (headNo == rangeEnd + 1)
                {
                    rangeEnd = headNo;
                    continue;
                }

                ranges.Add(FormatHeadRangePart(rangeStart, rangeEnd));
                rangeStart = headNo;
                rangeEnd = headNo;
            }

            ranges.Add(FormatHeadRangePart(rangeStart, rangeEnd));
            return string.Join(", ", ranges);
        }

        private static string FormatHeadRangePart(int rangeStart, int rangeEnd)
        {
            return rangeStart == rangeEnd ? $"H{rangeStart:00}" : $"H{rangeStart:00}-H{rangeEnd:00}";
        }

        private static string FormatVisionFlipSummary(EN_VISION_FLIP_TYPE flipType)
        {
            var items = new List<string>();
            if (flipType.HasFlag(EN_VISION_FLIP_TYPE.XFlip))
            {
                items.Add("X");
            }

            if (flipType.HasFlag(EN_VISION_FLIP_TYPE.YFlip))
            {
                items.Add("Y");
            }

            if (flipType.HasFlag(EN_VISION_FLIP_TYPE.XyFlip))
            {
                items.Add("XY");
            }

            return items.Count == 0 ? "OFF" : string.Join("+", items);
        }

        private static string FormatScannerFlipSummary(ST_SCANNER_AXIS_TRANSFORM transform)
        {
            var items = new List<string>();
            if (transform.XFlip)
            {
                items.Add("X");
            }

            if (transform.YFlip)
            {
                items.Add("Y");
            }

            if (transform.XyFlip)
            {
                items.Add("XY");
            }

            return items.Count == 0 ? "OFF" : string.Join("+", items);
        }

        private static string FormatAxisSettingDetail(EN_VISION_FLIP_TYPE visionFlipType, IReadOnlyDictionary<int, ST_SCANNER_AXIS_TRANSFORM> scannerAxisTransforms)
        {
            static int GetHeadNo(KeyValuePair<int, ST_SCANNER_AXIS_TRANSFORM> item)
            {
                return item.Key;
            }

            var lines = new List<string>
            {
                $"Vision Flip: {CReviewManager.FormatVisionFlipType(visionFlipType)}",
                "Scanner Flip:"
            };
            foreach (var item in scannerAxisTransforms.OrderBy(GetHeadNo))
            {
                lines.Add($"H{item.Key:00}: {CScannerAxisTransform.Format(item.Value)}");
            }

            if (scannerAxisTransforms.Count == 0)
            {
                lines.Add("-");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private Task<EN_VISION_FLIP_TYPE> LoadCurrentVisionFlipType()
        {
            var xFlipValue = _settingManager.GetValue(EN_SETTING_TAB.Option, "VisionXFlip", "");
            var yFlipValue = _settingManager.GetValue(EN_SETTING_TAB.Option, "VisionYFlip", "");
            var xyFlipValue = _settingManager.GetValue(EN_SETTING_TAB.Option, "VisionXyFlip", "");
            return Task.FromResult(CReviewManager.ParseVisionFlipType(xFlipValue, yFlipValue, xyFlipValue));
        }

        private IReadOnlyDictionary<int, ST_SCANNER_AXIS_TRANSFORM> LoadCurrentScannerAxisTransforms(IEnumerable<int> headNos)
        {
            static int GetHeadNo(int headNo)
            {
                return headNo;
            }

            static bool IsInvalidHeadNo(int headNo)
            {
                return headNo is < 1 or > 8;
            }

            var distinctHeadNos = headNos.Distinct().OrderBy(GetHeadNo).ToArray();
            var invalidHeadNo = distinctHeadNos.FirstOrDefault(IsInvalidHeadNo);
            if (invalidHeadNo != 0)
            {
                throw new InvalidOperationException($"Review Result Head number must be between 1 and 8. Current value: {invalidHeadNo}.");
            }

            var optionSettings = _settingManager.LoadSection(EN_SETTING_TAB.Option);
            const int firstHeadNo = 1;
            const int lastHeadNo = 8;
            var transforms = new Dictionary<int, ST_SCANNER_AXIS_TRANSFORM>(lastHeadNo);
            for (var headNo = firstHeadNo; headNo <= lastHeadNo; headNo++)
            {
                transforms[headNo] = CScannerAxisTransform.Read(optionSettings, headNo);
            }

            return transforms;
        }

        private bool HasSelectedRecipeMismatch(ST_REVIEW_RESULT_FILE_DATA result)
        {
            var selectedRecipeId = NormalizeRecipeId(_selectedRecipeIdProvider());
            if (string.IsNullOrWhiteSpace(selectedRecipeId))
            {
                return false;
            }

            return !selectedRecipeId.Equals(NormalizeRecipeId(result.RecipeId), StringComparison.OrdinalIgnoreCase);
        }

        private InvalidDataException CreateRecipeMismatchException(ST_REVIEW_RESULT_FILE_DATA result)
        {
            return new InvalidDataException($"Review Result recipe mismatch. Selected={NormalizeRecipeId(_selectedRecipeIdProvider())}, File={result.RecipeId}.");
        }

        private (IReadOnlyList<ST_DISPLAY_ITEM> Summary, IReadOnlyList<ST_CORRECTION_SOURCE_ROW> Source, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Candidate, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Apply, IReadOnlyList<ST_DISPLAY_ITEM> Detail, IReadOnlyList<ST_CORRECTION_HISTORY_ROW> History) CreateReviewData()
        {
            var result = _loadedReviewResult;
            bool MatchesJudge(global::Drilling.Common.Review.ST_REVIEW_RESULT_FILE_ROW row)
            {
                return row.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase);
            }

            var ngCount = result?.Rows.Count(MatchesJudge) ?? 0;
            return ([
                new("Review Result", result?.FileName ?? "Not Loaded"), 
                new("Shot Count", result?.Rows.Count.ToString(CultureInfo.InvariantCulture) ?? "0"), 
                new("NG Count", ngCount.ToString(CultureInfo.InvariantCulture), ngCount > 0 ? "WARN" : "OK"), 
                new("Recipe", result?.RecipeId ?? NormalizeRecipeId(_selectedRecipeIdProvider()))
            ], [], [], [], [], _reviewDataHistory.ToArray());
        }

        private static string NormalizeRecipeId(string value)
        {
            return Path.GetFileNameWithoutExtension(value?.Trim() ?? "");
        }

        private static string NormalizeReviewShotKey(string value, int cellNo)
        {
            var normalized = (value ?? "").Trim().Replace("-", "_", StringComparison.Ordinal).Replace(".", "_", StringComparison.Ordinal).ToUpperInvariant();
            return normalized.StartsWith("CELL", StringComparison.Ordinal) ? normalized : $"CELL{cellNo}_{normalized}";
        }

        private static string FormatReviewOffsetSource(string recipeId, ST_REVIEW_OFFSET_FILE_DATA reviewOffsetFile)
        {
            var normalizedRecipeId = NormalizeRecipeId(recipeId);
            return string.IsNullOrWhiteSpace(reviewOffsetFile.FileName) ? $"{normalizedRecipeId}_reviewoffset / NONE" : $"{normalizedRecipeId}_reviewoffset / {reviewOffsetFile.FileName}";
        }

        private static IReadOnlyDictionary<string, ST_REVIEW_OFFSET_ROW> CreateReviewOffsetLookup(IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows)
        {
            var values = new Dictionary<string, ST_REVIEW_OFFSET_ROW>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var shotId = NormalizeReviewOffsetShotId(row.ShotId);
                if (row.CellId <= 0 || string.IsNullOrWhiteSpace(shotId))
                {
                    continue;
                }

                values[$"CELL{row.CellId}_{shotId}"] = row;
            }

            return values;
        }

        private static IReadOnlyList<ST_REVIEW_OFFSET_ROW> MergeReviewOffsetRows(IReadOnlyList<ST_REVIEW_OFFSET_ROW> currentRows, IReadOnlyList<ST_CORRECTION_REVIEW_OFFSET_ROW> appliedRows, IReadOnlyDictionary<string, string> parameters)
        {
            var rows = new Dictionary<string, ST_REVIEW_OFFSET_ROW>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in currentRows)
            {
                var shotId = NormalizeReviewOffsetShotId(row.ShotId);
                if (row.CellId <= 0 || string.IsNullOrWhiteSpace(shotId))
                {
                    continue;
                }

                rows[$"CELL{row.CellId}_{shotId}"] = row with
                {
                    ShotId = shotId
                };
            }

            foreach (var row in appliedRows)
            {
                if (!TryParseReviewOffsetShotKey(row.ShotKey, out var cellId, out var shotId))
                {
                    continue;
                }

                if (!int.TryParse(row.Head.Trim().TrimStart('H', 'h'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var headNo) || headNo is < 1 or > 8)
                    throw new InvalidOperationException($"Review Offset Head를 확인할 수 없습니다: {row.Head} / {row.ShotKey}");
                var design = CScannerAxisTransform.ScannerOffsetToDesign(parameters, headNo, row.OffsetXValue, row.OffsetYValue);
                rows[$"CELL{cellId}_{shotId}"] = new ST_REVIEW_OFFSET_ROW(cellId, shotId, design.X, design.Y)
                {
                    IsDesignCoordinate = true
                };
            }

            return rows.Values.OrderBy(row => row.CellId).ThenBy(row => row.ShotId, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool TryParseReviewOffsetShotKey(string value, out int cellId, out string shotId)
        {
            cellId = 0;
            shotId = "";
            var normalized = (value ?? "").Trim().Replace("-", "_", StringComparison.Ordinal).Replace(".", "_", StringComparison.Ordinal).ToUpperInvariant();
            if (!normalized.StartsWith("CELL", StringComparison.Ordinal))
            {
                return false;
            }

            var separatorIndex = normalized.IndexOf('_');
            if (separatorIndex <= 4 || separatorIndex >= normalized.Length - 1)
            {
                return false;
            }

            if (!int.TryParse(normalized[4..separatorIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out cellId))
            {
                return false;
            }

            shotId = NormalizeReviewOffsetShotId(normalized[(separatorIndex + 1)..]);
            return cellId > 0 && !string.IsNullOrWhiteSpace(shotId);
        }

        private static string NormalizeReviewOffsetShotId(string value)
        {
            return (value ?? "").Trim().Replace("-", "_", StringComparison.Ordinal).Replace(".", "_", StringComparison.Ordinal).ToUpperInvariant();
        }

        private static string FormatReviewShotName(ST_REVIEW_PLAN_POINT point)
        {
            return CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX);
        }

        private static (IReadOnlyList<ST_DISPLAY_ITEM> Summary, IReadOnlyList<ST_CORRECTION_SOURCE_ROW> Source, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Candidate, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Apply, IReadOnlyList<ST_DISPLAY_ITEM> Detail, IReadOnlyList<ST_CORRECTION_HISTORY_ROW> History) CreateAlignCompData()
        {
            return ([new("Align Result", "Ready"), new("Distortion Key", "6"), new("Theta", "+0.002 deg"), new("Weight", "X 0.80 / Y 0.80")], [
                new("Align", "Front Key", "X +0.012 / Y -0.006", "OK", "Vision PC"), 
                new("Align", "Rear Key", "X +0.009 / Y -0.004", "OK", "Vision PC"), 
                new("Distortion", "KEY 01-06", "6 point", "Ready", "Vision Result")
            ], [
                new("Align X", "Weighted", "+0.010", "mm", "Ready"), 
                new("Align Y", "Weighted", "-0.005", "mm", "Ready"), 
                new("Align Theta", "Rotation", "+0.002", "deg", "Ready"), 
                new("Distortion", "Max DA", "42.0", "um", "OK")
            ], [
                new("X Start Weight", "Recipe", "0.80", "-", "Ready"), 
                new("X End Weight", "Recipe", "0.80", "-", "Ready"), 
                new("Y Start Weight", "Recipe", "0.80", "-", "Ready"), 
                new("Y End Weight", "Recipe", "0.80", "-", "Ready")
            ], [new("Target", "Align result compensation"), new("Apply To", "Process coordinate transform"), new("Distortion Key", "6 key display / result storage")], CreateCommonHistory());
        }

        private static (IReadOnlyList<ST_DISPLAY_ITEM> Summary, IReadOnlyList<ST_CORRECTION_SOURCE_ROW> Source, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Candidate, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Apply, IReadOnlyList<ST_DISPLAY_ITEM> Detail, IReadOnlyList<ST_CORRECTION_HISTORY_ROW> History) CreateOffsetCompData()
        {
            return ([new("Mode", "Recipe Offset"), new("Review Offset", "Ready"), new("Cell Shift", "50"), new("Default Offset", "Ready")], [
                new("Recipe", "Shot Offset", "Cell / Shot", "Loaded", "JHMI_RCP"), 
                new("Recipe", "Cell Shift", "CELL01-CELL50", "Loaded", "JHMI_RCP"), 
                new("Setup", "Scanner Default Offset", "8 scanner", "Ready", "Default Offset")
            ], [
                new("CELL1_A1_RECIPE_OFFSET_X", "Shot", "+0.003", "mm", "Ready"), 
                new("CELL7_A1_RECIPE_OFFSET_Y", "Shot", "+0.002", "mm", "Ready"), 
                new("CELL12_ALIGN_X", "Cell", "-0.010", "mm", "Ready"), 
                new("SCANNER_04_DEFAULT_X", "Default", "+0.006", "mm", "Ready")
            ], [
                new("Simple Offset", "Key-in", "Recipe value edit", "-", "Ready"), 
                new("Rough Offset", "Cell 1 Point", "Cell batch apply", "-", "Pending"), 
                new("Default Offset", "Scanner interval", "Head default apply", "-", "Ready")
            ], [new("Target", "Recipe offset parameter"), new("Simple Offset", "Direct key-in value"), new("Excluded", "Scanner Field Correction / Scan Comp")], CreateCommonHistory());
        }

        private static (IReadOnlyList<ST_DISPLAY_ITEM> Summary, IReadOnlyList<ST_CORRECTION_SOURCE_ROW> Source, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Candidate, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Apply, IReadOnlyList<ST_DISPLAY_ITEM> Detail, IReadOnlyList<ST_CORRECTION_HISTORY_ROW> History) CreateApcIcrData()
        {
            return ([new("APC", "OFF", "WAIT"), new("ICR", "OFF", "WAIT"), new("Precision", "+/-13 um"), new("Source", "CIM Share")], [
                new("APC", "APC File", "Not Loaded", "WAIT", "CIM PC Share"), 
                new("ICR", "ICR File", "Not Loaded", "WAIT", "CIM PC Share"), 
                new("Setting", "OFFSET_PRECISION", "+/-13", "Ready", "EC_LD")
            ], [new("APC_USE", "External", "0", "-", "WAIT"), new("ICR_USE", "External", "0", "-", "WAIT"), new("OFFSET_PRECISION", "Limit", "13", "um", "Ready")], [
                new("APC Apply", "Correction Table", "Standby", "-", "WAIT"), 
                new("ICR Apply", "Correction Table", "Standby", "-", "WAIT"), 
                new("Source Backup", "History", "Required", "-", "Ready")
            ], [new("Target", "External precision compensation"), new("Input", "CIM/shared folder file"), new("Apply To", "Correction table / process model")], CreateCommonHistory());
        }

        private static (IReadOnlyList<ST_DISPLAY_ITEM> Summary, IReadOnlyList<ST_CORRECTION_SOURCE_ROW> Source, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Candidate, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Apply, IReadOnlyList<ST_DISPLAY_ITEM> Detail, IReadOnlyList<ST_CORRECTION_HISTORY_ROW> History) CreateZeroDefenseData()
        {
            return ([new("0-Line Point", "5"), new("Mode", "Review"), new("Judge", "Ready"), new("Apply", "Standby")], [
                new("Recipe", "ZERO_DEFENCE_REVIEW_POINT", "5", "Loaded", "JHMI_RCP"), 
                new("Review", "0-Line Result", "5 point", "Ready", "Review Result"), 
                new("Stage", "Line Move", "Stage PC", "Ready", "Melsec / Stage PC")
            ], [new("Line Error X", "Average", "+0.008", "mm", "OK"), new("Line Error Y", "Average", "-0.006", "mm", "OK"), new("Max Deviation", "0-Line", "0.018", "mm", "OK")], [new("0-Line Offset X", "Recipe", "+0.008", "mm", "Ready"), new("0-Line Offset Y", "Recipe", "-0.006", "mm", "Ready"), new("Defense Result", "Interlock", "OK", "-", "Ready")], [new("Target", "0-line defense review"), new("Review Point", "Recipe ZERO_DEFENCE_REVIEW_POINT"), new("Apply To", "Recipe offset / process model")], CreateCommonHistory());
        }

        private static (IReadOnlyList<ST_DISPLAY_ITEM> Summary, IReadOnlyList<ST_CORRECTION_SOURCE_ROW> Source, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Candidate, IReadOnlyList<ST_CORRECTION_VALUE_ROW> Apply, IReadOnlyList<ST_DISPLAY_ITEM> Detail, IReadOnlyList<ST_CORRECTION_HISTORY_ROW> History) CreateOutputHistoryData()
        {
            return ([new("Output", "Correction Table"), new("Recipe", "DRILL_A01"), new("Last Apply", "10:24:36"), new("State", "Modified", "WARN")], [
                new("Output", "Recipe", "DRILL_A01.csv", "Modified", "Config\\RECIPE"), 
                new("Output", "Correction Table", "Current", "Ready", "Config\\Correction"), 
                new("Log", "Correction History", "Today", "Ready", "Data\\Log")
            ], [
                new("Review Offset", "Recipe", "+0.006 / -0.004", "mm", "Ready"), 
                new("Align Offset", "Transform", "+0.010 / -0.005", "mm", "Ready"), 
                new("Zero Defense", "Recipe", "+0.008 / -0.006", "mm", "Ready")
            ], [
                new("Recipe Save", "DRILL_A01.csv", "Required", "-", "Pending"), 
                new("Script Build", "Process Model", "Correction applied", "-", "Ready"), 
                new("History Write", "Correction Log", "Required", "-", "Ready")
            ], [new("Target", "Final correction output"), new("Apply Order", "Review / Align / Offset / APC-ICR / Zero"), new("History", "Before / After / User / Time")], [
                new("10:24:36", "ENG1", "ZERO DEFENSE", "Line Offset", "0.000 / 0.000", "+0.008 / -0.006", "Pending"), 
                new("10:18:11", "ENG1", "OFFSET COMP", "CELL1_A1_RECIPE_OFFSET_X", "0.000", "+0.003", "Saved"), 
                new("10:12:40", "ENG1", "ALIGN/DISTORTION", "Theta", "0.000", "+0.002", "Saved"), 
                new("10:04:29", "ENG1", "REVIEW DATA", "Review Result", "-", "Loaded", "OK")
            ]);
        }

        private static IReadOnlyList<ST_CORRECTION_HISTORY_ROW> CreateCommonHistory()
        {
            return[
                new("10:24:36", "ENG1", "REVIEW DATA", "Review Result", "-", "Loaded", "OK"), 
                new("10:18:11", "ENG1", "OFFSET COMP", "CELL1_A1_RECIPE_OFFSET_X", "0.000", "+0.003", "Saved"), 
                new("10:12:40", "ENG1", "ALIGN/DISTORTION", "Theta", "0.000", "+0.002", "Saved"), 
                new("10:05:09", "ENG1", "APC / ICR", "APC File", "-", "Standby", "WAIT")
            ];
        }

        private static string GetSubtitle(string selectedTab)
        {
            return selectedTab switch
            {
                "REVIEW DATA" => "Review result load, measurement error review and compensation point selection",
                "ALIGN/DISTORTION" => "Align Key / Distortion Key base coordinates",
                "REVIEW" => "User-entered correction / Glass, Cell and Shot preview",
                "APC / ICR" => "External APC / ICR precision correction file apply",
                "VIEWER / HISTORY" => "Final correction offset viewer and apply history",
                _ => "Correction operation"
            };
        }

        private static string GetSourceName(string selectedTab)
        {
            return selectedTab switch
            {
                "APC / ICR" => "External",
                "ALIGN/DISTORTION" => "Vision",
                "REVIEW" => "Recipe",
                "VIEWER / HISTORY" => "Mixed",
                _ => "Review"
            };
        }

        private static string GetStateName(string selectedTab)
        {
            return selectedTab is "APC / ICR" ? "WAIT" : "Ready";
        }

#endregion
#region Alarm
        public override Task StopForHeavyAlarm()
        {
            _roughCancellation?.Cancel();
            _fineCancellation?.Cancel();
            if (_isReviewOperationRunning)
                _reviewManager.Stop();
            return Task.CompletedTask;
        }

#endregion
#region AlignBase
        private async Task ExecuteAlignBaseAction(string action)
        {
            try
            {
                if (action is "OFFSET_APPLY" or "OFFSET_INIT" or "OFFSET_VIEW")
                {
                    await ExecuteAlignOffsetAction(action);
                    return;
                }

                if (action == "LOAD")
                {
                    await LoadAlignLastData();
                    return;
                }

                if (action == "REFRESH")
                {
                    var recipeId = NormalizeRecipeId(_selectedRecipeIdProvider());
                    if (string.IsNullOrWhiteSpace(recipeId))
                        throw new InvalidOperationException("현재 선택된 레시피가 없습니다.");
                    var refreshed = await _recipeManager.RefreshRecipe(recipeId);
                    if (!recipeId.Equals(NormalizeRecipeId(_selectedRecipeIdProvider()), StringComparison.OrdinalIgnoreCase))
                        return;
                    if (refreshed is null)
                    {
                        AlignDistortion.Clear("현재 레시피 파일을 찾을 수 없습니다.");
                        throw new InvalidOperationException("현재 레시피 파일을 찾을 수 없습니다.");
                    }

                    AlignDistortion.Update(refreshed);
                    _statusReporter($"{refreshed.Name} 레시피에서 Base 값을 다시 읽었습니다.");
                    return;
                }

                var recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 선택된 레시피가 없습니다.");
                AlignDistortion.Update(recipe);
                switch (action)
                {
                    case "INIT":
                        if (!CRecipeConfirmDialog.ShowConfirmation("Base 초기화", $"현재 레시피: {recipe.Name}\n\nAK1~AK4, DK1~DK6의 Base X/Y 20개 값을 모두 0.0으로 초기화하시겠습니까?\n레시피에 즉시 저장되며 되돌릴 수 없습니다.", "초기화", useDangerStyle: true))
                            return;
                        await SaveAlignBaseValues(recipe.Id, CAlignBaseData.ZeroValues());
                        CRecipeConfirmDialog.ShowCompletion("Base 초기화 완료", "Base X/Y 20개 값을 0.0으로 초기화하고 레시피에 저장했습니다.");
                        break;
                    case "EDIT":
                        var editor = new CAlignBaseEditPanel(recipe, AlignDistortion);
                        var dialog = new CAlignBaseEditDialog(editor, values => SaveAlignBaseValues(recipe.Id, values))
                        {
                            Owner = Application.Current?.MainWindow
                        };
                        if (dialog.ShowDialog() == true)
                            CRecipeConfirmDialog.ShowCompletion("Base 저장 완료", $"{recipe.Name} 레시피에 Base 값을 저장했습니다.");
                        break;
                    case "LOAD_TO_BASE":
                        var loaded = CAlignBaseData.ReadLastLoads(AlignDistortion);
                        CAlignBaseData.Apply(recipe, loaded);
                        if (!CRecipeConfirmDialog.ShowConfirmation(
                            "Load to Base", 
                            $"현재 레시피: {recipe.Name}\n\nAK 및 선택한 DK {AlignDistortion.LoadKeyNames.Count}개 키의 Base X/Y를 화면의 LastLoad 값으로 덮어쓰시겠습니까?\n선택하지 않은 DK의 Base는 유지됩니다.\n레시피에 즉시 저장되며 되돌릴 수 없습니다.", 
                            "적용", 
                            useDangerStyle: true
                        ))
                            return;
                        await SaveAlignBaseValues(recipe.Id, loaded);
                        CRecipeConfirmDialog.ShowCompletion("Load to Base 완료", "LastLoad X/Y 값을 Base에 반영하고 레시피에 저장했습니다.");
                        break;
                }
            }
            catch (Exception exception)
            {
                var title = action switch
                {
                    "LOAD" => "Load 확인",
                    "OFFSET_APPLY" => "Offset Apply 확인",
                    "OFFSET_INIT" => "Offset Init 확인",
                    "OFFSET_VIEW" => "Current Offset View 확인",
                    _ => "Base 작업 확인"
                };
                _statusReporter($"{title}: {exception.Message}");
                CRecipeConfirmDialog.ShowWarning(title, exception.Message);
            }
        }

        private async Task LoadAlignLastData(Func<string, string, bool>? confirm = null, Action<string, string>? warn = null)
        {
            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message, "Load");
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            var recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 선택된 레시피가 없습니다.");
            AlignDistortion.Update(recipe);
            var targets = AlignDistortion.LoadParameterKeys.ToArray();
            var selectionSignature = AlignDistortion.LoadSelectionSignature;
            var mode = _alignLastLoad.GetMode(targets);
            if (mode == EN_COMM_STATE.Offline)
            {
                warn("MELSEC 연결 확인", "MELSEC이 연결되어 있지 않습니다.");
                return;
            }

            var simulation = mode == EN_COMM_STATE.Simulation;
            var prompt = simulation ? $"시뮬레이션 랜덤값을 불러오시겠습니까?\n대상: {string.Join(", ", AlignDistortion.LoadKeyNames)} (X/Y {targets.Length}개 값)\n각 Base X/Y 값을 기준으로 -1 ~ +1 mm 범위의 랜덤값을 불러옵니다." : $"MELSEC Data를 Load하시겠습니까?\n대상: {string.Join(", ", AlignDistortion.LoadKeyNames)}\n선택한 키의 X/Y {targets.Length}개 값을 한 번 읽어옵니다.";
            if (!confirm(simulation ? "Simulation Load" : "MELSEC Data Load", prompt))
                return;
            if (selectionSignature != AlignDistortion.LoadSelectionSignature)
                throw new InvalidOperationException("선택한 DK 또는 키 위치가 변경되었습니다. Load를 다시 실행하세요.");
            if (_alignLastLoad.GetMode(targets) != mode)
                throw new InvalidOperationException("MELSEC 상태가 변경되었습니다. Load를 다시 실행하세요.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var values = simulation ? CAlignLastLoadService.CreateSimulationValues(recipe, parameterKeys: targets) : await _alignLastLoad.ReadOnlineAsync(timeout.Token, targets);
            var current = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false);
            if (current is null || !current.Id.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("현재 레시피가 변경되었습니다. Load를 다시 실행하세요.");
            AlignDistortion.Update(current);
            if (selectionSignature != AlignDistortion.LoadSelectionSignature)
                throw new InvalidOperationException("Load 중 선택한 DK 또는 키 위치가 변경되었습니다. 다시 Load해 주세요.");
            AlignDistortion.ApplyLastLoad(recipe.Id, values, DateTimeOffset.Now, targets, simulation);
            _statusReporter($"{(simulation ? "Simulation" : "MELSEC")} LastLoad X/Y {targets.Length}개 값을 불러왔습니다.");
        }

        private async Task SaveAlignBaseValues(string recipeId, IReadOnlyDictionary<string, string> values)
        {
            var current = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false);
            if (current is null || !current.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("현재 레시피가 변경되었습니다. Base 작업을 다시 실행하세요.");
            // Merge only the twenty base fields into the current recipe; retain all other parameters and its history.
            var updated = CAlignBaseData.Apply(current, values);
            await _recipeManager.SaveRecipe(updated);
            var saved = (await _recipeManager.LoadRecipes()).Single(recipe => recipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase));
            AlignDistortion.Update(saved);
            _statusReporter($"{saved.Name} 레시피의 Base 값을 저장했습니다.");
        }

#endregion
#region AlignOffset
        private async Task ExecuteAlignOffsetAction(
            string action, 
            Func<string, string, bool>? confirm = null, 
            Action<string, string>? completed = null, 
            Action<CAlignOffsetViewerPanel>? showViewer = null, 
            Func<ST_ALIGN_OFFSET_TOLERANCE_CHECK, bool>? resolveExceeded = null
        )
        {
            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message, action == "OFFSET_INIT" ? "초기화" : "적용", useDangerStyle: action == "OFFSET_INIT");
            completed ??= (title, message) => CRecipeConfirmDialog.ShowCompletion(title, message);
            resolveExceeded ??= review => new CAlignOffsetToleranceDialog(review)
            {
                Owner = Application.Current?.MainWindow
            }.ShowDialog() == true;
            var recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 선택된 레시피가 없습니다.");
            AlignDistortion.Update(recipe);
            var tolerance = RefreshAlignOffsetTolerance();
            if (action == "OFFSET_VIEW")
            {
                var settings = _settingManager.LoadSection(EN_SETTING_TAB.Option).Concat(_settingManager.LoadSection(EN_SETTING_TAB.Motor)).ToArray();
                var parameters = BuildOutputViewerParameterMap(recipe, settings);
                var cellCount = int.Parse(parameters["MAX_CELL_NUMBER"], System.Globalization.CultureInfo.InvariantCulture);
                var viewer = new CAlignOffsetViewerPanel(recipe, parameters, cellNo => BuildOutputViewerGlassPreview(parameters, cellCount, cellNo));
                if (showViewer is not null)
                    showViewer(viewer);
                else
                    new CAlignOffsetViewerDialog(viewer)
                    {
                        Owner = Application.Current?.MainWindow
                    }.ShowDialog();
                return;
            }

            if (action == "OFFSET_APPLY" && tolerance is null)
                throw new InvalidOperationException("Setting > Option > Interlock의 Align Offset Tolerance를 확인해 주세요.");
            var inputSignature = AlignDistortion.OffsetInputSignature;
            var model = action == "OFFSET_APPLY" ? AlignDistortion.CreateLoadedInterpolationModel() : null;
            var calculatedOffsets = await Task.Run(() => action == "OFFSET_APPLY" ? CAlignShotOffsetData.Calculate(recipe, model!) : CAlignShotOffsetData.ZeroValues(recipe));
            var offsets = calculatedOffsets;
            await EnsureAlignOffsetRecipeUnchanged(recipe);
            if (action == "OFFSET_APPLY" && inputSignature != AlignDistortion.OffsetInputSignature)
                throw new InvalidOperationException("계산 중 키 선택 또는 Load 값이 변경되었습니다. 다시 Apply해 주세요.");
            var title = action == "OFFSET_APPLY" ? "Offset Apply" : "Offset Init";
            var message = action == "OFFSET_APPLY" ? $"Lagrange 알고리즘 방식으로 전체 {offsets.Count:N0}개 Shot에 Align Offset을 적용하시겠습니까?\n기존 Align Offset은 새 계산값으로 대체됩니다." : $"전체 {offsets.Count:N0}개 Shot의 저장된 Align Offset X/Y를 0.000으로 초기화하시겠습니까?\n이 작업은 되돌릴 수 없습니다.";
            var check = action == "OFFSET_APPLY" ? CAlignOffsetTolerance.Check(tolerance!.Value, calculatedOffsets) : null;
            if (check is { ExceededShots.Count: > 0 })
            {
                if (!resolveExceeded(check) || !check.CanApplyRemaining)
                {
                    _statusReporter("Align Offset 적용을 취소했습니다. 기존 레시피 값은 유지됩니다.");
                    return;
                }

                // Filter whole records, never individual X/Y values.
                offsets = calculatedOffsets.Where(shot => !CAlignOffsetTolerance.Exceeds(shot, check.Tolerance)).ToArray();
            }
            else if (!confirm(title, message))
                return;
            await EnsureAlignOffsetRecipeUnchanged(recipe);
            if (action == "OFFSET_APPLY" && inputSignature != AlignDistortion.OffsetInputSignature)
                throw new InvalidOperationException("키 선택 또는 Load 값이 변경되었습니다. 다시 Apply해 주세요.");
            if (action == "OFFSET_APPLY" && RefreshAlignOffsetTolerance() != tolerance)
                throw new InvalidOperationException("확인 중 Align Offset Tolerance가 변경되었습니다. 다시 Apply해 주세요.");
            var logFile = _alignOffsetLogFile ?? throw new InvalidOperationException("Align Offset 로그 저장 경로가 설정되지 않았습니다.");
            // Capture raw inputs before any await; UI selections may change during file I/O.
            var includedKeys = AlignDistortion.LoadKeyNames.ToHashSet(StringComparer.Ordinal);
            var keyLog = AlignDistortion.Keys.Select(key =>
            {
                var correction = action == "OFFSET_INIT" ? new ST_ALIGN_CORRECTION(0, 0) : model!.Contains(key.GlassX, key.GlassY) ? model.Evaluate(key.GlassX, key.GlassY) : (ST_ALIGN_CORRECTION? )null;
                if (correction is { } result)
                    correction = new ST_ALIGN_CORRECTION(CAlignShotOffsetData.RoundValue(result.X), CAlignShotOffsetData.RoundValue(result.Y));
                return new ST_ALIGN_OFFSET_KEY_LOG(key.Name, includedKeys.Contains(key.Name), key.GlassX, key.GlassY, key.BaseRawX, key.BaseRawY, key.LastLoadRawX, key.LastLoadRawY, correction?.X, correction?.Y);
            }).ToArray();
            var excluded = check?.ExceededShots ?? [];
            var excludedKeys = excluded.Select(shot => (shot.CellNo, shot.ShotName)).ToHashSet();
            var previousValues = recipe.Parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);
            var savedOffsets = calculatedOffsets.Select(shot => !excludedKeys.Contains((shot.CellNo, shot.ShotName)) ? shot : shot with { X = CAlignShotOffsetData.ReadValue(previousValues, CAlignShotOffsetData.ParameterKey(shot.CellNo, shot.ShotName, "X")), Y = CAlignShotOffsetData.ReadValue(previousValues, CAlignShotOffsetData.ParameterKey(shot.CellNo, shot.ShotName, "Y")) }).ToArray();
            var log = new ST_ALIGN_OFFSET_LOG(recipe.Id, recipe.Name, action == "OFFSET_INIT", default, AlignDistortion.LastLoadedAt, AlignDistortion.LastLoadIsSimulation, keyLog, savedOffsets)
            {
                Tolerance = action == "OFFSET_APPLY" ? tolerance : null,
                CalculatedOffsets = calculatedOffsets,
                ExcludedOffsets = excluded
            };
            var updated = CAlignShotOffsetData.Apply(recipe, offsets);
            await _recipeManager.SaveRecipe(updated);
            Exception? logError = null;
            try
            {
                await logFile.Save(log with { ChangedAt = DateTimeOffset.Now });
            }
            catch (Exception exception)
            {
                logError = exception;
            }

            var current = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false);
            AlignDistortion.Update(current);
            await _refreshCurrentScreen();
            if (logError is not null)
                throw new InvalidOperationException($"레시피의 Align Offset {(action == "OFFSET_APPLY" ? "저장" : "초기화")}은 완료됐지만 CSV 로그를 저장하지 못했습니다.\n{logError.Message}", logError);
            var resultMessage = action == "OFFSET_INIT" ? $"{recipe.Name} 레시피의 전체 Shot Align Offset을 0.000으로 초기화했습니다." : $"{recipe.Name}: {offsets.Count:N0}개 Shot Align Offset을 저장했습니다." + (excluded.Count > 0 ? $"\nTolerance 초과 {excluded.Count:N0}개 Shot의 X/Y는 모두 기존 저장값을 유지했습니다." : "");
            _statusReporter(resultMessage);
            completed($"{title} 완료", resultMessage);
        }

        private double? RefreshAlignOffsetTolerance()
        {
            double? tolerance;
            try
            {
                tolerance = CAlignOffsetTolerance.Parse(_settingManager.GetValue(EN_SETTING_TAB.Option, CAlignOffsetTolerance.SettingKey, "0.500"));
            }
            catch (InvalidOperationException)
            {
                tolerance = null;
            }

            AlignDistortion.UpdateOffsetTolerance(tolerance);
            return tolerance;
        }

        private async Task EnsureAlignOffsetRecipeUnchanged(ST_RECIPE_DEFINITION expected)
        {
            var current = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false);
            if (current is null || !current.Id.Equals(expected.Id, StringComparison.OrdinalIgnoreCase) || current.Parameters.Count != expected.Parameters.Count)
                throw new InvalidOperationException("현재 레시피가 변경되었습니다. Offset 작업을 다시 실행해 주세요.");
            var values = current.Parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);
            if (expected.Parameters.Any(parameter => !values.TryGetValue(parameter.Key, out var value) || parameter.Value != value))
                throw new InvalidOperationException("현재 레시피 값이 변경되었습니다. Offset 작업을 다시 실행해 주세요.");
        }

#endregion
#region Fine
        private async Task ExecuteFineAction(
            string action, 
            Func<string, string, bool>? confirm = null, 
            Action<string, string>? warn = null, 
            Func<IReadOnlyList<ST_REVIEW_PLAN_POINT>, CancellationToken, Task<IReadOnlyList<ST_VISION_MARK_FIND>>>? simulationMeasurement = null
        )
        {
            if (action is "OFFSET_APPLY" or "OFFSET_INIT" or "OFFSET_VIEW")
            {
                await ExecuteFineOffsetAction(action, confirm, warn: warn);
                return;
            }

            if (action == "STOP")
            {
                _fineCancellation?.Cancel();
                return;
            }

            if (action is "START" or "RESTART" or "RESUME_NG" && !CheckOperationAlarm("FINE REVIEW"))
                return;
            if (_isReviewOperationRunning || action is not ("START" or "RESTART" or "RESUME_NG") || !Fine.CanConfigure || Fine.Lines.Count == 0)
                return;
            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message);
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            _isReviewOperationRunning = true;
            Simple.SetOffsetOperationRunning(true);
            Rough.SetOffsetOperationRunning(true);
            Fine.SetOperationRunning(true);
            ExecuteCommand.NotifyCanExecuteChanged();
            var version = Fine.SourceVersion;
            try
            {
                var resume = action == "RESUME_NG";
                var start = resume ? Fine.RetryStartLineNo ?? 1 : 1;
                var count = Fine.Lines.Where(line => line.OrderNo >= start).Sum(line => line.Points.Count);
                if (!confirm("FINE Review Start", $"{Fine.RecipeName} / {Fine.InspectionMethod} / Beam {Fine.SelectedBeam}\nLine #{start}부터 {count:N0}개 Shot을 검사하시겠습니까?\nStage는 공통 Process Move / IOF로 동작하며, MELSEC과 Vision은 각각 해당 장치의 연결·시뮬레이션 설정을 따릅니다."))
                    return;
                if (!CheckOperationAlarm("FINE REVIEW"))
                    return;
                _fineCancellation = new CancellationTokenSource();
                var token = _fineCancellation.Token;
                Fine.Prepare();
                // Paint preparation/Stop before continuing, even when recipe reads complete synchronously.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                token.ThrowIfCancellationRequested();
                Fine.Update(await LoadSelectedOutputViewerRecipe(token, fallbackToFirst: false), LoadSimpleOffsetSettings());
                if (version != Fine.SourceVersion)
                    throw new InvalidOperationException("레시피 또는 Setting이 변경되었습니다. 검사 대상을 다시 확인해 주세요.");
                token.ThrowIfCancellationRequested();
                var request = Fine.CreateRequest(start, resume);
                if (version != Fine.SourceVersion)
                    throw new InvalidOperationException("검사 대상이 변경되었습니다. 다시 확인해 주세요.");
                token.ThrowIfCancellationRequested();
                Fine.Begin();
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                void Publish(ST_FINE_REVIEW_SNAPSHOT snapshot)
                {
                    if (dispatcher is null || dispatcher.CheckAccess())
                        Fine.ApplyProgress(snapshot, version);
                    else
                        dispatcher.Invoke(() => Fine.ApplyProgress(snapshot, version));
                }

                var result = await _reviewManager.StartFineCorrection(request, Publish, token, simulationMeasurement);
                _statusReporter(result.Message);
                if (result.State == EN_REVIEW_SEQUENCE_STATE.Failed)
                    warn("FINE 검사 확인", result.Message);
                if (result.ResultSaveError.Length > 0)
                    warn("FINE 검사 결과 저장 실패", result.ResultSaveError);
            }
            catch (OperationCanceledException)
            {
                Fine.SetError("FINE 검사 중지");
            }
            catch (Exception error)
            {
                Fine.SetError(error.Message);
                _statusReporter(error.Message);
                warn("FINE 확인", error.Message);
            }
            finally
            {
                _fineCancellation?.Dispose();
                _fineCancellation = null;
                Fine.End();
                _isReviewOperationRunning = false;
                Simple.SetOffsetOperationRunning(false);
                Rough.SetOffsetOperationRunning(false);
                Fine.SetOperationRunning(false);
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }

#endregion
#region FineOffset
        private async Task ExecuteFineOffsetAction(
            string action, 
            Func<string, string, bool>? confirm = null, 
            Action<string, string>? completed = null, 
            Action<string, string>? warn = null, 
            Action<CAlignOffsetViewerPanel>? showViewer = null
        )
        {
            if (_isReviewOperationRunning || !Fine.CanConfigure || action is not ("OFFSET_APPLY" or "OFFSET_INIT" or "OFFSET_VIEW"))
                return;
            if (action == "OFFSET_VIEW")
            {
                await ExecuteSimpleOffsetAction(action, confirm, completed, warn, showViewer);
                return;
            }

            var initialize = action == "OFFSET_INIT";
            var title = initialize ? "FINE Offset Init" : "FINE Offset Apply";
            confirm ??= (caption, message) => CRecipeConfirmDialog.ShowConfirmation(caption, message, initialize ? "초기화" : "적용", useDangerStyle: initialize);
            completed ??= (caption, message) => CRecipeConfirmDialog.ShowCompletion(caption, message);
            warn ??= (caption, message) => CRecipeConfirmDialog.ShowWarning(caption, message);
            var version = Fine.SourceVersion;
            var recipeId = Fine.RecipeId;
            _isReviewOperationRunning = true;
            Simple.SetOffsetOperationRunning(true);
            Rough.SetOffsetOperationRunning(true);
            Fine.SetOperationRunning(true);
            ExecuteCommand.NotifyCanExecuteChanged();
            try
            {
                var recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 레시피가 없습니다.");
                var settings = LoadSimpleOffsetSettings();
                EnsureSource(recipe, settings);
                // Capture full-precision results on the UI dispatcher. Calculation and file I/O run off the UI thread.
                var input = initialize ? null : Fine.CreateOffsetInput();
                var targets = Fine.GetOffsetTargets();
                var deltas = initialize ? targets.Select(point => new ST_FINE_OFFSET_DELTA(point, 0, 0)).ToArray() : await Task.Run(() => CReviewManager.CalculateFineOffsets(input!.Targets, input.Measurements, input.Flip));
                var cells = deltas.Select(delta => delta.Shot.CellNo).Distinct().Count();
                var preserved = Fine.GetOffsetTargets().Count - deltas.Count;
                var message = $"현재 레시피: {recipe.Name}\n대상: {cells:N0} Cells / {deltas.Count:N0} Shots\n\n" + (initialize ? "레시피 전체 Shot의 Review Offset X/Y를 0.000으로 초기화하시겠습니까?\n되돌릴 수 없습니다." : $"{Fine.LineCount} Line · 같은 Cell/열 기준 " + (Fine.LineCount == 1 ? "측정값" : "Y 방향 Lagrange 보간값") + "을 기존 Review Offset에 누적하시겠습니까?\nVision Flip 변환값을 설계 좌표로 저장합니다." + (preserved > 0 ? $"\n검사 기준값이 없는 {preserved:N0}개 Shot은 기존 값을 유지합니다." : "")) + "\n새 REVIEW OFFSET 파일을 생성합니다.";
                if (!confirm(title, message))
                    return;
                recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 레시피가 없습니다.");
                settings = LoadSimpleOffsetSettings();
                EnsureSource(recipe, settings);
                var parameters = BuildOutputViewerParameterMap(recipe, settings);
                var saved = await Task.Run(async () =>
                {
                    var latest = await _reviewOffsetFile.LoadLatest(recipe.Id);
                    if (!latest.RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Review Offset 파일의 레시피가 일치하지 않습니다.");
                    var rows = latest.Rows.ToDictionary(row => (row.CellId, NormalizeReviewOffsetShotId(row.ShotId)));
                    foreach (var delta in deltas)
                    {
                        var point = delta.Shot;
                        var name = CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX);
                        var key = (point.CellNo, NormalizeReviewOffsetShotId(name));
                        var previous = rows.GetValueOrDefault(key);
                        var current = initialize || previous is null ? (X: 0.0, Y: 0.0) : CReviewOffsetData.ToDesign(parameters, point.HeadNo, previous.ReviewOffsetX, previous.ReviewOffsetY, previous.IsDesignCoordinate);
                        rows[key] = new(point.CellNo, key.Item2, CAlignShotOffsetData.RoundValue(current.X + delta.X), CAlignShotOffsetData.RoundValue(current.Y + delta.Y))
                        {
                            IsDesignCoordinate = true
                        };
                    }

                    return await _reviewOffsetFile.SaveNew(recipe.Id, rows.Values.OrderBy(row => row.CellId).ThenBy(row => row.ShotId).ToArray());
                });
                _simpleOffsetRecipeId = saved.RecipeId;
                _simpleOffsetLoad = Task.FromResult(saved);
                Simple.Update(recipe, settings, saved);
                Rough.Update(recipe, settings, saved);
                if (version == Fine.SourceVersion && recipeId == Fine.RecipeId)
                {
                    Fine.AcceptOffsetSave(initialize, saved.FileName);
                    if (!initialize)
                        _reviewManager?.FineMonitor.MarkOffsetSaved(Fine.MonitorRunId, saved.FileName);
                }

                _statusReporter($"{title}: {saved.FileName}");
                completed($"{title} 완료", $"{deltas.Count:N0}개 Shot의 Review Offset을 {(initialize ? "초기화" : "누적 적용")}했습니다.\n{saved.FileName}");
            }
            catch (Exception error)
            {
                _statusReporter($"{title}: {error.Message}");
                warn(title, error.Message);
            }
            finally
            {
                _isReviewOperationRunning = false;
                Simple.SetOffsetOperationRunning(false);
                Rough.SetOffsetOperationRunning(false);
                Fine.SetOperationRunning(false);
                ExecuteCommand.NotifyCanExecuteChanged();
            }

            void EnsureSource(Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe, IReadOnlyList<Drilling.Common.Managers.ST_SYSTEM_PARAMETER> settings)
            {
                Fine.Update(recipe, settings);
                if (Fine.RecipeId != recipeId || Fine.SourceVersion != version)
                    throw new InvalidOperationException("레시피 또는 Setting이 변경되었습니다. 검사 대상을 다시 확인해 주세요.");
            }
        }

#endregion
#region OffsetValidation
        private void ReportInvalidReviewOffset(Exception error)
        {
            if (error is not CReviewOffsetValidationException || _lastReviewOffsetWarning == error.Message)
                return;
            // A screen refresh must not reopen the same warning dialog continuously.
            _lastReviewOffsetWarning = error.Message;
            _statusReporter(error.Message);
            ReviewOffsetWarning("Review Offset 확인", error.Message);
        }

#endregion
#region Rough
        private void InitializeRough(CInterfaceManager? interfaces)
        {
            var inspection = Rough.Inspection!;
            inspection.SourceInvalidated += () => _roughCancellation?.Cancel();
            inspection.ReadFeedback = () =>
            {
                if (interfaces is null)
                    return ("-", "-", "OFFLINE");
                var values = CMotorStatusPollingService.ReadCachedSnapshot(interfaces);
                var mode = CMotorStatusPollingService.ReadStageConnectionState(interfaces);
                return (values.FirstOrDefault(row => row.Name == "Stage Y")?.Position ?? "-", values.FirstOrDefault(row => row.Name == "Review X")?.Position ?? "-", mode == EN_COMM_STATE.Simulation ? "SIMULATION" : mode == EN_COMM_STATE.Online ? "ONLINE" : "OFFLINE");
            };
            Fine.ReadFeedback = inspection.ReadFeedback;
        }

        private async Task ExecuteRoughAction(
            string action, 
            Func<string, string, bool>? confirm = null, 
            Action<string, string>? completed = null, 
            Action<string, string>? warn = null, 
            Action<CAlignOffsetViewerPanel>? showViewer = null, 
            Func<ST_REVIEW_PLAN_POINT, CancellationToken, Task<ST_ROUGH_REVIEW_MEASUREMENT>>? simulationMeasurement = null
        )
        {
            var inspection = Rough.Inspection!;
            if (action == "REVIEW_STOP")
            {
                _roughCancellation?.Cancel();
                return;
            }

            if (action is "REVIEW_START" or "NG_RETRY" && !CheckOperationAlarm("ROUGH REVIEW"))
                return;
            if (_isReviewOperationRunning || !inspection.CanExecute(action))
                return;
            if (action == "OFFSET_VIEW")
            {
                await ExecuteSimpleOffsetAction(action, confirm, completed, warn, showViewer);
                return;
            }

            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message, action == "OFFSET_INIT" ? "초기화" : "확인", useDangerStyle: action == "OFFSET_INIT");
            completed ??= (title, message) => CRecipeConfirmDialog.ShowCompletion(title, message);
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            _isReviewOperationRunning = true;
            Simple.SetOffsetOperationRunning(true);
            Rough.SetOffsetOperationRunning(true);
            Fine.SetOperationRunning(true);
            ExecuteCommand.NotifyCanExecuteChanged();
            inspection.RefreshCommands();
            try
            {
                var initialize = action == "OFFSET_INIT";
                var request = inspection.CreateOffsetRequest(initialize);
                var recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 선택된 레시피가 없습니다.");
                var settings = LoadSimpleOffsetSettings();
                EnsureSimpleOffsetSource(request, recipe, settings);
                if (action is "REVIEW_START" or "NG_RETRY")
                {
                    var retry = action == "NG_RETRY";
                    var plan = inspection.CreatePlan();
                    var count = plan.Points.Count(point => !retry || point.State == EN_REVIEW_POINT_STATE.Ng);
                    if (!confirm(retry ? "NG Retry" : "Review Start", $"{recipe.Name} / {Rough.SelectedHeadsText} / Beam {inspection.SelectedBeam}\n{count}개 Shot을 {(retry ? "재검사" : "PtoP 검사")}하시겠습니까?"))
                        return;
                    if (!CheckOperationAlarm("ROUGH REVIEW"))
                        return;
                    Rough.BackToGlassCommand.Execute(null);
                    EnsureSimpleOffsetSource(request, await LoadSelectedOutputViewerRecipe(CancellationToken.None, false) ?? throw new InvalidOperationException("현재 레시피가 없습니다."), LoadSimpleOffsetSettings());
                    _roughCancellation = new CancellationTokenSource();
                    var version = inspection.SourceVersion;
                    inspection.Begin(retry);
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    void Publish(ST_ROUGH_REVIEW_SNAPSHOT snapshot)
                    {
                        if (dispatcher is null || dispatcher.CheckAccess())
                            inspection.ApplyProgress(snapshot, version);
                        else
                            dispatcher.Invoke(() => inspection.ApplyProgress(snapshot, version));
                    }

                    var result = await _reviewManager.StartRoughCorrection(plan, retry, Publish, _roughCancellation.Token, simulationMeasurement);
                    _statusReporter(result.Message);
                    if (result.State == EN_REVIEW_SEQUENCE_STATE.Failed)
                        warn("ROUGH 검사 확인", result.Message);
                    if (result.ResultSaveError.Length > 0)
                        warn("ROUGH 검사 결과 저장 실패", result.ResultSaveError);
                    return;
                }

                if (action is not ("OFFSET_APPLY" or "OFFSET_INIT"))
                    return;
                var deltas = request.Rows.ToDictionary(row => (row.CellId, row.ShotId), row => initialize ? (X: 0.0, Y: 0.0) : inspection.IncrementForShot(request.TargetHeads.GetValueOrDefault((row.CellId, row.ShotId), request.HeadNo), row.CellId));
                if (!confirm(initialize ? "Offset Init" : "Offset Apply", $"현재 레시피: {recipe.Name}\n{Rough.SelectedHeadsText} / {inspection.Rows.Count}개 기준 Shot / {request.Rows.Count:N0}개 적용 Shot\n\n" + (initialize ? "이 범위의 Review Offset X/Y를 0.000으로 초기화하시겠습니까?" : "OK 결과의 보정량만 기존 설계 Review Offset에 누적하시겠습니까?\nNG·미검사·이미 적용한 결과는 0.000을 누적하여 기존값을 유지합니다.") + "\n새 REVIEW OFFSET 파일을 생성합니다."))
                    return;
                EnsureSimpleOffsetSource(request, await LoadSelectedOutputViewerRecipe(CancellationToken.None, false) ?? throw new InvalidOperationException("현재 레시피가 없습니다."), LoadSimpleOffsetSettings());
                var parameters = BuildOutputViewerParameterMap(recipe, settings);
                var saved = await Task.Run(async () =>
                {
                    var latest = await _reviewOffsetFile.LoadLatest(recipe.Id);
                    if (!latest.RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Review Offset 파일의 레시피가 일치하지 않습니다.");
                    var merged = latest.Rows.ToDictionary(row => (row.CellId, NormalizeReviewOffsetShotId(row.ShotId)));
                    foreach (var target in request.Rows)
                    {
                        var key = (target.CellId, target.ShotId);
                        var previous = merged.GetValueOrDefault(key);
                        var current = previous is null ? (X: 0.0, Y: 0.0) : CReviewOffsetData.ToDesign(parameters, request.TargetHeads.GetValueOrDefault(key, request.HeadNo), previous.ReviewOffsetX, previous.ReviewOffsetY, previous.IsDesignCoordinate);
                        var delta = deltas[key];
                        merged[key] = previous is { IsDesignCoordinate: true } && !initialize && delta == (0, 0) ? previous : target with
                        {
                            ReviewOffsetX = initialize ? 0 : CRoughCorrectionPanel.Round(current.X + delta.X),
                            ReviewOffsetY = initialize ? 0 : CRoughCorrectionPanel.Round(current.Y + delta.Y)
                        };
                    }

                    return await _reviewOffsetFile.SaveNew(recipe.Id, merged.Values.OrderBy(row => row.CellId).ThenBy(row => row.ShotId).ToArray());
                });
                _simpleOffsetRecipeId = saved.RecipeId;
                _simpleOffsetLoad = Task.FromResult(saved);
                inspection.MarkApplied(initialize);
                Rough.AcceptSavedOffsets(saved, request.Rows, recipe, settings);
                Simple.Update(recipe, settings, saved);
                _statusReporter($"ROUGH Review Offset 저장: {saved.FileName}");
                completed(initialize ? "Offset Init 완료" : "Offset Apply 완료", $"{saved.FileName}\n{request.Rows.Count:N0}개 Shot의 Review Offset을 저장했습니다.");
            }
            catch (OperationCanceledException)
            {
                _statusReporter("ROUGH 검사 중지");
            }
            catch (Exception exception)
            {
                _statusReporter(exception.Message);
                warn("ROUGH 확인", exception.Message);
            }
            finally
            {
                _roughCancellation?.Dispose();
                _roughCancellation = null;
                inspection.End();
                _isReviewOperationRunning = false;
                Simple.SetOffsetOperationRunning(false);
                Rough.SetOffsetOperationRunning(false);
                Fine.SetOperationRunning(false);
                ExecuteCommand.NotifyCanExecuteChanged();
                inspection.RefreshCommands();
            }
        }

#endregion
#region SimpleOffset
        private async Task ExecuteSimpleOffsetAction(
            string action, 
            Func<string, string, bool>? confirm = null, 
            Action<string, string>? completed = null, 
            Action<string, string>? warn = null, 
            Action<CAlignOffsetViewerPanel>? showViewer = null
        )
        {
            if (_isReviewOperationRunning || action is not ("OFFSET_APPLY" or "OFFSET_INIT" or "OFFSET_VIEW"))
                return;
            var title = action switch
            {
                "OFFSET_INIT" => "Offset Init",
                "OFFSET_VIEW" => "Cur Review Offset View",
                _ => "Offset Apply"
            };
            confirm ??= (caption, message) => CRecipeConfirmDialog.ShowConfirmation(caption, message, action == "OFFSET_INIT" ? "초기화" : "적용", useDangerStyle: action == "OFFSET_INIT");
            completed ??= (caption, message) => CRecipeConfirmDialog.ShowCompletion(caption, message);
            warn ??= (caption, message) => CRecipeConfirmDialog.ShowWarning(caption, message);
            _isReviewOperationRunning = true;
            ExecuteCommand.NotifyCanExecuteChanged();
            Simple.SetOffsetOperationRunning(true);
            Fine.SetOperationRunning(true);
            try
            {
                var request = action == "OFFSET_VIEW" ? null : Simple.CreateOffsetChange(action == "OFFSET_INIT");
                var recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 선택된 레시피가 없습니다.");
                var settings = LoadSimpleOffsetSettings();
                if (action == "OFFSET_VIEW")
                {
                    var saved = await Task.Run(() => _reviewOffsetFile.LoadLatest(recipe.Id));
                    var parameters = BuildOutputViewerParameterMap(recipe, settings);
                    CReviewOffsetData.ApplyToParameters(parameters, saved);
                    var cellCount = int.Parse(parameters["MAX_CELL_NUMBER"], CultureInfo.InvariantCulture);
                    var viewer = new CAlignOffsetViewerPanel(recipe, parameters, cellNo => BuildOutputViewerGlassPreview(parameters, cellCount, cellNo), reviewOffsets: true);
                    if (showViewer is not null)
                        showViewer(viewer);
                    else
                        new CAlignOffsetViewerDialog(viewer)
                        {
                            Owner = Application.Current?.MainWindow
                        }.ShowDialog();
                    return;
                }

                EnsureSimpleOffsetSource(request!, recipe, settings);
                var message = $"현재 레시피: {recipe.Name}\n대상: {request!.Scope} / {request.Rows.Count:N0} Shot\n" + $"Design Offset: X {request.X:0.000} / Y {request.Y:0.000} mm\n\n" + (action == "OFFSET_INIT" ? "해당 범위의 Review Offset X/Y를 모두 0.000으로 초기화하시겠습니까?" : "해당 범위의 모든 Shot을 입력한 X/Y 값으로 덮어쓰시겠습니까?") + "\n선택 범위 밖의 값은 유지하고 새 REVIEW OFFSET 파일을 생성합니다.";
                if (!confirm(title, message))
                    return;
                recipe = await LoadSelectedOutputViewerRecipe(CancellationToken.None, fallbackToFirst: false) ?? throw new InvalidOperationException("현재 선택된 레시피가 없습니다.");
                EnsureSimpleOffsetSource(request, recipe, LoadSimpleOffsetSettings());
                var result = await Task.Run(async () =>
                {
                    var latest = await _reviewOffsetFile.LoadLatest(recipe.Id);
                    if (!latest.RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Review Offset 파일의 레시피가 일치하지 않습니다.");
                    var rows = new Dictionary<(int Cell, string Shot), ST_REVIEW_OFFSET_ROW>();
                    foreach (var row in latest.Rows.Concat(request.Rows))
                    {
                        var shot = NormalizeReviewOffsetShotId(row.ShotId);
                        rows[(row.CellId, shot)] = row with
                        {
                            ShotId = shot
                        };
                    }

                    return await _reviewOffsetFile.SaveNew(recipe.Id, rows.Values.OrderBy(row => row.CellId).ThenBy(row => row.ShotId, StringComparer.Ordinal).ToArray());
                });
                _simpleOffsetRecipeId = result.RecipeId;
                _simpleOffsetLoad = Task.FromResult(result);
                Simple.AcceptSavedOffsets(result, request.Rows, recipe, settings);
                Rough.Update(recipe, settings, result);
                var resultMessage = $"{request.Scope} / {request.Rows.Count:N0} Shot의 Review Offset을 " + (action == "OFFSET_INIT" ? "0.000으로 초기화했습니다." : "입력값으로 저장했습니다.") + $"\n{result.FileName}";
                _statusReporter(resultMessage);
                completed($"{title} 완료", resultMessage);
            }
            catch (Exception exception)
            {
                _statusReporter($"{title}: {exception.Message}");
                warn(title, exception.Message);
            }
            finally
            {
                _isReviewOperationRunning = false;
                Simple.SetOffsetOperationRunning(false);
                Fine.SetOperationRunning(false);
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }

        private ST_SYSTEM_PARAMETER[] LoadSimpleOffsetSettings() => _settingManager.LoadSection(EN_SETTING_TAB.Option).Concat(_settingManager.LoadSection(EN_SETTING_TAB.Motor)).ToArray();
        private static void EnsureSimpleOffsetSource(ST_SIMPLE_OFFSET_CHANGE request, ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in recipe.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                parameters[item.Key] = item.Value;
            foreach (var item in settings.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                parameters[item.Key] = item.Value;
            CReviewOffsetData.RemoveFromParameters(parameters);
            if (!recipe.Id.Equals(request.RecipeId, StringComparison.OrdinalIgnoreCase) || parameters.Count != request.SourceParameters.Count || request.SourceParameters.Any(item => !parameters.TryGetValue(item.Key, out var value) || item.Value != value))
                throw new InvalidOperationException("레시피 또는 Head 설정이 변경되었습니다. 화면을 확인한 후 Offset 작업을 다시 실행해 주세요.");
        }
#endregion
    }

    public static class CAlignBaseData
    {
        public static IReadOnlyList<string> KeyNames { get; } = Enumerable.Range(1, 4).Select(n => $"AK{n}").Concat(Enumerable.Range(1, 6).Select(n => $"DK{n}")).ToArray();
        public static IReadOnlyList<string> ParameterKeys { get; } = KeyNames.SelectMany(name => new[] { $"ALIGNBASE_{name}_X", $"ALIGNBASE_{name}_Y" }).ToArray();

        public static IReadOnlyDictionary<string, string> ZeroValues() => ParameterKeys.ToDictionary(key => key, _ => "0.0", StringComparer.OrdinalIgnoreCase);
        public static IReadOnlyDictionary<string, string> ReadLastLoads(CAlignDistortionPanel panel)
        {
            var keys = panel.Keys.ToDictionary(key => key.Name, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in KeyNames)
            {
                if (!keys.TryGetValue(name, out var key))
                    throw new InvalidOperationException($"{name}의 Base 값을 확인하세요.");
                var selected = panel.LoadKeyNames.Contains(name);
                var x = selected ? key.LastLoadX : key.BaseSourceX;
                var y = selected ? key.LastLoadY : key.BaseSourceY;
                if (!IsFinite(x) || !IsFinite(y))
                    throw new InvalidOperationException($"{name}의 {(selected ? "LastLoad" : "Base")} 값이 없습니다. 선택한 키를 Load한 뒤 실행하세요.");
                values[$"ALIGNBASE_{name}_X"] = selected ? key.LastLoadRawX?.ToString("R", CultureInfo.InvariantCulture) ?? x.Trim() : x;
                values[$"ALIGNBASE_{name}_Y"] = selected ? key.LastLoadRawY?.ToString("R", CultureInfo.InvariantCulture) ?? y.Trim() : y;
            }

            return values;
        }

        public static ST_RECIPE_DEFINITION Apply(ST_RECIPE_DEFINITION recipe, IReadOnlyDictionary<string, string> values)
        {
            if (values.Count != ParameterKeys.Count || ParameterKeys.Any(key => !values.ContainsKey(key)))
                throw new InvalidOperationException("AK1~AK4, DK1~DK6의 Base X/Y 20개 값이 모두 필요합니다.");
            var parameters = recipe.Parameters.ToArray();
            foreach (var key in ParameterKeys)
            {
                var indices = Enumerable.Range(0, parameters.Length).Where(i => parameters[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (indices.Length != 1)
                    throw new InvalidOperationException($"레시피의 {key} 항목을 확인하세요.");
                var index = indices[0];
                var parameter = parameters[index];
                var text = values[key].Trim();
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    throw new InvalidOperationException($"{parameter.Name}: 유효한 숫자를 입력하세요.");
                if (parameter.Min != parameter.Max && (number < parameter.Min || number > parameter.Max))
                    throw new InvalidOperationException($"{parameter.Name}: {parameter.Min.ToString(CultureInfo.InvariantCulture)} ~ {parameter.Max.ToString(CultureInfo.InvariantCulture)} mm 범위로 입력하세요.");
                if (parameter.ChangeLimit > 0 && double.TryParse(parameter.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var oldValue) && Math.Abs(number - oldValue) > parameter.ChangeLimit)
                    throw new InvalidOperationException($"{parameter.Name}: 변경 허용 범위는 ±{parameter.ChangeLimit.ToString(CultureInfo.InvariantCulture)} mm입니다.");
                parameters[index] = parameter with
                {
                    Value = text
                };
            }

            return recipe with
            {
                Parameters = parameters
            };
        }

        private static bool IsFinite(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value);
    }

    public sealed class CAlignBaseEditPanel
    {
        private readonly ST_RECIPE_DEFINITION _recipe;
        public CAlignBaseEditPanel(ST_RECIPE_DEFINITION recipe, CAlignDistortionPanel preview)
        {
            _recipe = recipe;
            if (!recipe.Id.Equals(preview.RecipeId, StringComparison.OrdinalIgnoreCase) || preview.Keys.Count != 10)
                throw new InvalidOperationException("현재 레시피의 Glass Preview를 확인하세요.");
            RecipeName = recipe.Name;
            PreviewImage = preview.PreviewImage;
            PreviewHeight = preview.PreviewHeight;
            Keys = preview.Keys.Select(key => new CAlignBaseEditKey(key, recipe.Parameters.Single(parameter => parameter.Key.Equals($"ALIGNBASE_{key.Name}_X", StringComparison.OrdinalIgnoreCase)), recipe.Parameters.Single(parameter => parameter.Key.Equals($"ALIGNBASE_{key.Name}_Y", StringComparison.OrdinalIgnoreCase)))).ToArray();
        }

        public string RecipeName { get; }
        public ImageSource? PreviewImage { get; }
        public double PreviewHeight { get; }
        public IReadOnlyList<CAlignBaseEditKey> Keys { get; }

        public IReadOnlyDictionary<string, string> BuildValues()
        {
            var values = Keys.SelectMany(key => new[] { new KeyValuePair<string, string>(key.ParameterX.Key, key.BaseX), new KeyValuePair<string, string>(key.ParameterY.Key, key.BaseY) }).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            CAlignBaseData.Apply(_recipe, values);
            return values;
        }
    }

    public sealed class CAlignBaseEditKey : INotifyPropertyChanged
    {
        private string _baseX;
        private string _baseY;
        public CAlignBaseEditKey(ST_ALIGN_DISTORTION_KEY key, ST_RECIPE_PARAMETER parameterX, ST_RECIPE_PARAMETER parameterY)
        {
            Name = key.Name;
            KeyBrush = key.KeyBrush;
            EditorLeft = key.EditorLeft;
            EditorTop = key.EditorTop + (ST_ALIGN_DISTORTION_KEY.EditorHeight - 96) / 2;
            ParameterX = parameterX;
            ParameterY = parameterY;
            _baseX = parameterX.Value;
            _baseY = parameterY.Value;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; }
        public Brush KeyBrush { get; }
        public double EditorLeft { get; }
        public double EditorTop { get; }
        public ST_RECIPE_PARAMETER ParameterX { get; }
        public ST_RECIPE_PARAMETER ParameterY { get; }

        public string BaseX
        {
            get => _baseX;
            set
            {
                if (_baseX == value)
                    return;
                _baseX = value;
                PropertyChanged?.Invoke(this, new(nameof(BaseX)));
            }
        }

        public string BaseY
        {
            get => _baseY;
            set
            {
                if (_baseY == value)
                    return;
                _baseY = value;
                PropertyChanged?.Invoke(this, new(nameof(BaseY)));
            }
        }
    }

    public sealed partial class CAlignDistortionPanel : INotifyPropertyChanged
    {
#region 공유 상태·초기값
        public string AlgorithmPreviewHint { get; private set; } = "현재 레시피의 키 위치를 확인하세요.";
        public double? OffsetTolerance { get; private set; } = CAlignOffsetTolerance.DefaultValue;

        public const double CanvasWidth = 940;
        public const double CanvasHeight = 700;
        private static readonly SolidColorBrush SelectedDkBrush = MakeBrush(32, 161, 88);
        private static readonly SolidColorBrush UnselectedDkBrush = MakeBrush(128, 138, 148);
        private string _signature = "";
        private Rect _glassFrame;
        private IReadOnlyDictionary<string, (double X, double Y)> _lastLoadedPositions = new Dictionary<string, (double, double)>();
        private bool _baseActionRunning;
        private readonly Func<string, Task>? _executeBaseAction;
        public IReadOnlyList<CAlignDistortionDkSelection> DkSelections { get; private set; } = [];

        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<ST_ALIGN_DISTORTION_KEY> Keys { get; private set; } = [];
        public string Summary { get; private set; } = "Recipe not loaded";
        public string RecipeName { get; private set; } = "-";
        public string RecipeId { get; private set; } = "";
        public double PreviewHeight { get; private set; } = CanvasHeight;

#endregion
#region 생성·초기화
        public CAlignDistortionPanel(Func<string, Task>? executeBaseAction = null)
        {
            _executeBaseAction = executeBaseAction;
            BaseActionCommand = new CButtonCommand(RunBaseAction, action => executeBaseAction is not null && !_baseActionRunning && (action?.ToString()is "REFRESH" or "LOAD" || (Keys.Count == 10 && !string.IsNullOrEmpty(RecipeId))));
            LoadCommand = new CButtonCommand(_ => RunBaseAction("LOAD"));
        }

#endregion
#region 기본 처리
        private async void RunBaseAction(object? parameter)
        {
            if (!BaseActionCommand.CanExecute(parameter))
                return;
            _baseActionRunning = true;
            BaseActionCommand.NotifyCanExecuteChanged();
            try
            {
                await _executeBaseAction!(parameter?.ToString() ?? "");
            }
            finally
            {
                _baseActionRunning = false;
                BaseActionCommand.NotifyCanExecuteChanged();
            }
        }

        public CButtonCommand BaseActionCommand { get; }
        public CButtonCommand LoadCommand { get; }
        public ImageSource? PreviewImage { get; private set; }
        public DateTimeOffset? LastLoadedAt { get; private set; }
        public string LastLoadTime => LastLoadedAt?.ToString("yyMMdd-HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
        public bool LastLoadIsSimulation { get; private set; }
        public IReadOnlyList<string> LoadKeyNames => CAlignBaseData.KeyNames.Where(name => name.StartsWith("AK", StringComparison.Ordinal) || IsDkSelected(name)).ToArray();
        public IReadOnlyList<string> LoadParameterKeys => LoadKeyNames.SelectMany(name => new[] { $"ALIGNBASE_{name}_X", $"ALIGNBASE_{name}_Y" }).ToArray();
        public string LoadSelectionSignature => RecipeId + "|" + string.Join("|", Keys.Where(key => LoadKeyNames.Contains(key.Name)).Select(key => FormattableString.Invariant($"{key.Name}:{key.GlassX:R},{key.GlassY:R}")));

        // Publish a complete, successful one-shot read together with its completion time.
        public void ApplyLastLoad(
            string recipeId, 
            IReadOnlyDictionary<string, double> values, 
            DateTimeOffset loadedAt, 
            IReadOnlyCollection<string>? expectedParameterKeys = null, 
            bool isSimulation = false
        )
        {
            if (!RecipeId.Equals(recipeId, StringComparison.OrdinalIgnoreCase) || Keys.Count != 10)
                throw new InvalidOperationException("현재 레시피와 LastLoad 대상이 일치하지 않습니다.");
            var expected = expectedParameterKeys ?? CAlignBaseData.ParameterKeys;
            if (expected.Count == 0 || expected.Any(key => !CAlignBaseData.ParameterKeys.Contains(key)) || expected.Distinct(StringComparer.Ordinal).Count() != expected.Count || values.Count != expected.Count || expected.Any(key => !values.TryGetValue(key, out var value) || !double.IsFinite(value)))
                throw new InvalidOperationException("선택한 키의 LastLoad X/Y 값을 모두 정상적으로 읽어야 합니다.");
            Keys = Keys.Select(key => key with { LastLoadX = values.TryGetValue($"ALIGNBASE_{key.Name}_X", out var x) ? x.ToString("0.000", CultureInfo.InvariantCulture) : "-", LastLoadY = values.TryGetValue($"ALIGNBASE_{key.Name}_Y", out var y) ? y.ToString("0.000", CultureInfo.InvariantCulture) : "-", LastLoadRawX = values.TryGetValue($"ALIGNBASE_{key.Name}_X", out var rawX) ? rawX : null, LastLoadRawY = values.TryGetValue($"ALIGNBASE_{key.Name}_Y", out var rawY) ? rawY : null }).ToArray();
            _lastLoadedPositions = Keys.Where(key => key.LastLoadRawX is not null && key.LastLoadRawY is not null).ToDictionary(key => key.Name, key => (key.GlassX, key.GlassY), StringComparer.Ordinal);
            LastLoadedAt = loadedAt;
            LastLoadIsSimulation = isSimulation;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public void Update(ST_RECIPE_DEFINITION? recipe)
        {
            if (recipe is null)
            {
                Clear("Recipe not loaded");
                return;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in recipe.Parameters)
                if (!string.IsNullOrWhiteSpace(parameter.Key))
                    parameters[parameter.Key] = parameter.Value;
            var layoutKeys = new[]
            {
                "GLASS_SIZE_X",
                "GLASS_SIZE_Y",
                "AK_MARGIN_X",
                "AK_MARGIN_Y"
            }.Concat(Enumerable.Range(1, 6).SelectMany(n => new[] { $"DISTORTION_KEY{n}_X", $"DISTORTION_KEY{n}_Y" }));
            var baseKeys = Enumerable.Range(1, 4).Select(n => $"AK{n}").Concat(Enumerable.Range(1, 6).Select(n => $"DK{n}")).SelectMany(key => new[] { $"ALIGNBASE_{key}_X", $"ALIGNBASE_{key}_Y" });
            var signature = recipe.Id + "|" + recipe.Name + "|" + string.Join("|", layoutKeys.Concat(baseKeys).Select(key => parameters.GetValueOrDefault(key, "")));
            if (_signature == signature)
                return;
            var previousLoads = RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase) ? Keys.ToDictionary(key => key.Name, key => (key.LastLoadX, key.LastLoadY, key.LastLoadRawX, key.LastLoadRawY), StringComparer.OrdinalIgnoreCase) : new Dictionary<string, (string LastLoadX, string LastLoadY, double? LastLoadRawX, double? LastLoadRawY)>(StringComparer.OrdinalIgnoreCase);
            if (!RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase))
            {
                LastLoadedAt = null;
                LastLoadIsSimulation = false;
                _lastLoadedPositions = new Dictionary<string, (double, double)>();
            }

            double? Read(string key) => parameters.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
            var width = Read("GLASS_SIZE_X") ?? 0;
            var height = Read("GLASS_SIZE_Y") ?? 0;
            if (width <= 0 || height <= 0)
            {
                Clear("Glass Size X / Y must be greater than 0.", recipe);
                return;
            }

            var marginX = Math.Clamp(Read("AK_MARGIN_X") ?? 0, 0, width / 2);
            var marginY = Math.Clamp(Read("AK_MARGIN_Y") ?? 0, 0, height / 2);
            var gold = MakeBrush(230, 164, 0);
            var keys = new List<ST_ALIGN_DISTORTION_KEY>
            {
                new("AK1", marginX, marginY, gold),
                new("AK2", marginX, height - marginY, gold),
                new("AK3", width - marginX, marginY, gold),
                new("AK4", width - marginX, height - marginY, gold)
            };
            var distortionKeys = CCellPreviewDrawing.CreateDistortionKeyPreviews(width, height, marginX, marginY, Read);
            UpdateDkSelections(distortionKeys.Select(key => (key.DisplayText, key.GlassY)));
            keys.AddRange(distortionKeys.Select(key => new ST_ALIGN_DISTORTION_KEY(key.DisplayText, key.GlassX, key.GlassY, GetDkBrush(key.DisplayText))));
            var largestSide = Math.Max(keys.Count(key => key.GlassX <= width / 2), keys.Count(key => key.GlassX > width / 2));
            PreviewHeight = Math.Max(CanvasHeight, 16 + largestSide * ST_ALIGN_DISTORTION_KEY.EditorHeight + (largestSide - 1) * 8);
            var scale = Math.Min(360 / width, 500 / height);
            var frame = new Rect((CanvasWidth - width * scale) / 2, (PreviewHeight - height * scale) / 2, width * scale, height * scale);
            foreach (var key in keys)
            {
                key.MarkerX = frame.Left + Math.Clamp(key.GlassX, 0, width) * scale;
                key.MarkerY = frame.Top + Math.Clamp(key.GlassY, 0, height) * scale;
                key.IsInsideGlass = key.GlassX >= 0 && key.GlassX <= width && key.GlassY >= 0 && key.GlassY <= height;
                key.BaseRawX = Read($"ALIGNBASE_{key.Name}_X");
                key.BaseRawY = Read($"ALIGNBASE_{key.Name}_Y");
                key.BaseSourceX = parameters.GetValueOrDefault($"ALIGNBASE_{key.Name}_X", "-");
                key.BaseSourceY = parameters.GetValueOrDefault($"ALIGNBASE_{key.Name}_Y", "-");
                key.BaseX = key.BaseRawX?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-";
                key.BaseY = key.BaseRawY?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-";
                if (previousLoads.TryGetValue(key.Name, out var loaded))
                {
                    key.LastLoadX = loaded.LastLoadX;
                    key.LastLoadY = loaded.LastLoadY;
                    key.LastLoadRawX = loaded.LastLoadRawX;
                    key.LastLoadRawY = loaded.LastLoadRawY;
                }
            }

            foreach (var left in new[]
            {
                true,
                false
            }

            )
            {
                var side = keys.Where(key => (key.GlassX <= width / 2) == left).OrderBy(key => key.GlassY).ThenBy(key => key.GlassX).ThenBy(key => key.Name).ToArray();
                for (var i = 0; i < side.Length; i++)
                {
                    side[i].EditorLeft = left ? 6 : CanvasWidth - ST_ALIGN_DISTORTION_KEY.EditorWidth - 6;
                    side[i].EditorTop = side.Length == 1 ? (PreviewHeight - ST_ALIGN_DISTORTION_KEY.EditorHeight) / 2 : 8 + i * ((PreviewHeight - ST_ALIGN_DISTORTION_KEY.EditorHeight - 16) / (side.Length - 1));
                }
            }

            _glassFrame = frame;
            Keys = keys.ToArray();
            RefreshPreviewImage();
            Summary = $"Glass {width:0.###} × {height:0.###} mm";
            RecipeName = recipe.Name;
            RecipeId = recipe.Id;
            _signature = signature;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            BaseActionCommand.NotifyCanExecuteChanged();
        }

        private void UpdateDkSelections(IEnumerable<(string Name, double Y)> keys)
        {
            // Use recipe Y coordinates, not fixed DK number pairs, to form horizontal groups.
            var groups = keys.OrderBy(key => key.Y).GroupBy(key => key.Y).Select(group => group.Select(key => key.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray()).ToArray();
            if (DkSelections.Select(group => group.Name).SequenceEqual(groups.Select(names => string.Join(" + ", names))))
                return;
            var selectedKeys = DkSelections.Where(group => group.IsSelected).SelectMany(group => group.KeyNames).ToHashSet(StringComparer.Ordinal);
            DkSelections = groups.Select(names => new CAlignDistortionDkSelection(names, RefreshDkSelection, names.All(selectedKeys.Contains))).ToArray();
        }

        private bool IsDkSelected(string name) => DkSelections.Any(group => group.IsSelected && group.KeyNames.Contains(name));
        private Brush GetDkBrush(string name) => IsDkSelected(name) ? SelectedDkBrush : UnselectedDkBrush;
        private void RefreshDkSelection()
        {
            if (Keys.Count == 0)
                return;
            // Never reuse a previously loaded DK after it has been deselected.
            Keys = Keys.Select(key => key.Name.StartsWith("DK", StringComparison.Ordinal) ? key with { KeyBrush = GetDkBrush(key.Name), LastLoadX = IsDkSelected(key.Name) ? key.LastLoadX : "-", LastLoadY = IsDkSelected(key.Name) ? key.LastLoadY : "-", LastLoadRawX = IsDkSelected(key.Name) ? key.LastLoadRawX : null, LastLoadRawY = IsDkSelected(key.Name) ? key.LastLoadRawY : null } : key).ToArray();
            RefreshPreviewImage();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Keys)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlgorithmPreviewHint)));
        }

        private void RefreshPreviewImage()
        {
            var frame = _glassFrame;
            var group = new DrawingGroup();
            using (var drawing = group.Open())
            {
                drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, CanvasWidth, PreviewHeight));
                drawing.DrawRectangle(null, new Pen(MakeBrush(102, 136, 164), 2), frame);
                DrawAlgorithmPreview(drawing);
                foreach (var key in Keys)
                {
                    var left = key.EditorLeft < frame.Left;
                    var start = new Point(left ? key.EditorLeft + ST_ALIGN_DISTORTION_KEY.EditorWidth : key.EditorLeft, key.EditorTop + ST_ALIGN_DISTORTION_KEY.EditorHeight / 2);
                    var edge = left ? frame.Left - 14 : frame.Right + 14;
                    var geometry = new StreamGeometry();
                    using (var line = geometry.Open())
                    {
                        line.BeginFigure(start, false, false);
                        line.LineTo(new Point(edge, start.Y), true, false);
                        line.LineTo(new Point(edge, key.MarkerY), true, false);
                        line.LineTo(new Point(key.MarkerX, key.MarkerY), true, false);
                    }

                    var color = ((SolidColorBrush)key.KeyBrush).Color;
                    drawing.DrawGeometry(null, new Pen(MakeBrush(color.R, color.G, color.B, 110), 1), geometry);
                }

                foreach (var key in Keys)
                {
                    var excludedDk = key.Name.StartsWith("DK", StringComparison.Ordinal) && !IsDkSelected(key.Name);
                    var pen = new Pen(key.IsInsideGlass || excludedDk ? key.KeyBrush : Brushes.IndianRed, 2);
                    if (key.Name.StartsWith("AK"))
                        drawing.DrawRectangle(null, pen, new Rect(key.MarkerX - 5, key.MarkerY - 5, 10, 10));
                    else
                    {
                        drawing.DrawLine(pen, new Point(key.MarkerX - 6, key.MarkerY), new Point(key.MarkerX + 6, key.MarkerY));
                        drawing.DrawLine(pen, new Point(key.MarkerX, key.MarkerY - 6), new Point(key.MarkerX, key.MarkerY + 6));
                    }
                }
            }

            group.Freeze();
            var image = new DrawingImage(group);
            image.Freeze();
            PreviewImage = image;
        }

        public void Clear(string message, ST_RECIPE_DEFINITION? recipe = null)
        {
            _signature = "";
            PreviewImage = null;
            _glassFrame = Rect.Empty;
            Keys = [];
            DkSelections = [];
            Summary = message;
            RecipeName = recipe?.Name ?? "-";
            RecipeId = recipe?.Id ?? "";
            PreviewHeight = CanvasHeight;
            LastLoadedAt = null;
            LastLoadIsSimulation = false;
            _lastLoadedPositions = new Dictionary<string, (double, double)>();
            AlgorithmPreviewHint = "현재 레시피의 키 위치를 확인하세요.";
            PreviewRegionCount = 0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            BaseActionCommand.NotifyCanExecuteChanged();
        }

        private static SolidColorBrush MakeBrush(byte r, byte g, byte b, byte a = 255)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

#endregion
#region Algorithm
        public int PreviewRegionCount { get; private set; }
        public string SettingToleranceText => OffsetTolerance is { } value ? $"Setting Tolerance : ±{value.ToString("0.000", CultureInfo.InvariantCulture)} mm" : "Setting Tolerance : 설정값 확인 필요";

        public void UpdateOffsetTolerance(double? value)
        {
            if (OffsetTolerance == value)
                return;
            OffsetTolerance = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettingToleranceText)));
        }

        private ST_ALIGN_INTERPOLATION_KEY[] InterpolationKeys() => Keys.Select(key => new ST_ALIGN_INTERPOLATION_KEY(key.Name, key.GlassX, key.GlassY, key.BaseRawX - key.LastLoadRawX, key.BaseRawY - key.LastLoadRawY)).ToArray();
        private CAlignInterpolation CreateInterpolationModel()
        {
            if (Keys.Count != 10)
                throw new InvalidOperationException("현재 레시피의 AK / DK 위치를 확인하세요.");
            var selected = LoadKeyNames;
            if (Keys.Any(key => selected.Contains(key.Name) && !key.IsInsideGlass))
                throw new InvalidOperationException("선택한 키가 Glass 영역 밖에 있습니다. 레시피의 키 위치를 확인하세요.");
            return new CAlignInterpolation(InterpolationKeys(), selected.Where(name => name.StartsWith("DK", StringComparison.Ordinal)).ToArray());
        }

        public string OffsetInputSignature => RecipeId + "|" + LoadSelectionSignature + "|" + string.Join("|", Keys.Where(key => LoadKeyNames.Contains(key.Name)).Select(key => FormattableString.Invariant($"{key.Name}:{key.BaseRawX:R},{key.BaseRawY:R},{key.LastLoadRawX:R},{key.LastLoadRawY:R}")));

        public CAlignInterpolation CreateLoadedInterpolationModel()
        {
            if (LastLoadedAt is null)
                throw new InvalidOperationException("선택한 AK / DK 값을 먼저 Load해 주세요.");
            foreach (var key in Keys.Where(key => LoadKeyNames.Contains(key.Name)))
            {
                if (key.LastLoadRawX is null || key.LastLoadRawY is null)
                    throw new InvalidOperationException($"{key.Name}의 LastLoad가 없습니다. 현재 선택한 키를 다시 Load해 주세요.");
                if (!_lastLoadedPositions.TryGetValue(key.Name, out var position) || position.X != key.GlassX || position.Y != key.GlassY)
                    throw new InvalidOperationException("Load 후 키 위치가 변경되었습니다. 현재 위치 기준으로 다시 Load해 주세요.");
            }

            var model = CreateInterpolationModel();
            model.ValidateMeasurements();
            return model;
        }

        private void DrawAlgorithmPreview(DrawingContext drawing)
        {
            PreviewRegionCount = 0;
            CAlignInterpolation model;
            try
            {
                model = CreateInterpolationModel();
            }
            catch (InvalidOperationException error)
            {
                AlgorithmPreviewHint = error.Message;
                return;
            }

            Point Position(ST_ALIGN_INTERPOLATION_KEY key)
            {
                var source = Keys.Single(item => item.Name == key.Name);
                return new Point(source.MarkerX, source.MarkerY);
            }

            var rows = model.Rows;
            PreviewRegionCount = 1;
            var purple = MakeBrush(155, 116, 207);
            var outline = rows.Select(row => Position(row.Left)).Concat(rows.Reverse().Select(row => Position(row.Right))).ToArray();
            drawing.DrawGeometry(MakeBrush(155, 116, 207, 26), new Pen(purple, 1.8), Polygon(outline));
            foreach (var key in rows.SelectMany(row => new[] { row.Left, row.Right }))
                drawing.DrawEllipse(null, new Pen(MakeBrush(155, 116, 207, 180), 1.3), Position(key), 10, 10);
            var top = rows[0];
            var bottom = rows[^1];
            var center = new Point((Position(top.Left).X + Position(top.Right).X + Position(bottom.Left).X + Position(bottom.Right).X) / 4, (Position(top.Left).Y + Position(bottom.Left).Y) / 2);
            DrawRegionLabel(drawing, $"LAGRANGE\n{rows.Count * 2} KEYS", center, purple);
            AlgorithmPreviewHint = $"라그랑쥬: {rows.Count * 2}개 키를 함께 사용하는 전체 범위입니다. 보정량 분포는 표시하지 않습니다.";
        }

        private static StreamGeometry Polygon(IReadOnlyList<Point> points)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], true, true);
                context.PolyLineTo(points.Skip(1).ToArray(), true, false);
            }

            geometry.Freeze();
            return geometry;
        }

        private static void DrawRegionLabel(DrawingContext drawing, string label, Point center, Brush brush)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return;
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 14, brush, 1)
            {
                TextAlignment = TextAlignment.Center
            };
            drawing.DrawText(text, new Point(center.X, center.Y - text.Height / 2));
        }
#endregion
    }

    public sealed class CAlignDistortionDkSelection(IReadOnlyList<string> keyNames, Action selectionChanged, bool isSelected = false) : INotifyPropertyChanged
    {
        private bool _isSelected = isSelected;
        public IReadOnlyList<string> KeyNames { get; } = keyNames;
        public string Name => string.Join(" + ", KeyNames);

        public event PropertyChangedEventHandler? PropertyChanged;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                selectionChanged();
            }
        }
    }

    public sealed record ST_ALIGN_DISTORTION_KEY(string Name, double GlassX, double GlassY, Brush KeyBrush)
    {
        public const double EditorWidth = 222;
        public const double EditorHeight = 144;
        public double EditorLeft { get; internal set; }
        public double EditorTop { get; internal set; }
        public double MarkerX { get; internal set; }
        public double MarkerY { get; internal set; }
        public bool IsInsideGlass { get; internal set; }
        public string BaseX { get; internal set; } = "-";
        public string BaseY { get; internal set; } = "-";
        public string LastLoadX { get; internal set; } = "-";
        public string LastLoadY { get; internal set; } = "-";
        public double? BaseRawX { get; internal set; }
        public double? BaseRawY { get; internal set; }
        public string BaseSourceX { get; internal set; } = "-";
        public string BaseSourceY { get; internal set; } = "-";
        public double? LastLoadRawX { get; internal set; }
        public double? LastLoadRawY { get; internal set; }
        public string BaseMinusLoadX => Subtract(BaseX, LastLoadX);
        public string BaseMinusLoadY => Subtract(BaseY, LastLoadY);
        public string PositionText => $"Recipe X: {GlassX:0.###} mm / Y: {GlassY:0.###} mm" + (IsInsideGlass ? "" : " (outside glass)");

        private static string Subtract(string baseValue, string loadValue)
        {
            if (!double.TryParse(baseValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var basis) || !double.TryParse(loadValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var loaded) || !double.IsFinite(basis) || !double.IsFinite(loaded) || !double.IsFinite(basis - loaded))
                return "-";
            return (basis - loaded).ToString("0.000", CultureInfo.InvariantCulture);
        }
    }

    // This service is called only by Load; it owns no polling timer or device worker.
    public sealed class CAlignLastLoadService
    {
        private readonly Func<IReadOnlyList<ST_INTERFACE_COMM_STATUS>> _connections;
        private readonly Func<IReadOnlyList<ST_MELSEC_MAP_DATA>> _map;
        private readonly Func<string, CancellationToken, double> _read;
        public CAlignLastLoadService(CInterfaceManager? manager) : this(() => manager?.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec) ?? [], () => manager?.Melsec.GetMapList() ?? [], (id, token) => manager?.Melsec.ReadDoubleNoLog(id, token) ?? throw new InvalidOperationException("MELSEC이 연결되어 있지 않습니다."))
        {
        }

        public CAlignLastLoadService(Func<IReadOnlyList<ST_INTERFACE_COMM_STATUS>> connections, Func<IReadOnlyList<ST_MELSEC_MAP_DATA>> map, Func<string, CancellationToken, double> read)
        {
            _connections = connections;
            _map = map;
            _read = read;
        }

        public EN_COMM_STATE GetMode(IReadOnlyCollection<string>? parameterKeys = null)
        {
            var ids = Targets(parameterKeys).Select(MapId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var numbers = _map().Where(row => ids.Contains(row.Id)).Select(row => row.DeviceNo).DefaultIfEmpty(0).Distinct().ToArray();
            var connections = _connections();
            var sources = numbers.Select(number => connections.FirstOrDefault(connection => connection.Module == EN_EQP_MODULE.Melsec && connection.Number == number)).ToArray();
            // The configured simulation flag takes precedence even if the connection reports Offline or Online.
            if (sources.Any(source => source?.IsSimulation == true || source?.ConnectionState == EN_COMM_STATE.Simulation))
                return EN_COMM_STATE.Simulation;
            return sources.All(source => source?.ConnectionState == EN_COMM_STATE.Online) ? EN_COMM_STATE.Online : EN_COMM_STATE.Offline;
        }

        public static IReadOnlyDictionary<string, double> CreateSimulationValues(ST_RECIPE_DEFINITION recipe, Func<double>? random = null, IReadOnlyCollection<string>? parameterKeys = null)
        {
            random ??= Random.Shared.NextDouble;
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in Targets(parameterKeys))
            {
                var parameter = recipe.Parameters.SingleOrDefault(parameter => parameter.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (parameter is null || !double.TryParse(parameter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var basis) || !double.IsFinite(basis))
                    throw new InvalidOperationException($"{key}의 Base 값을 확인하세요.");
                var sample = random();
                if (!double.IsFinite(sample) || sample < 0 || sample > 1)
                    throw new InvalidOperationException("시뮬레이션 랜덤값이 올바르지 않습니다.");
                values[key] = basis + (sample * 2 - 1);
            }

            return values;
        }

        public async Task<IReadOnlyDictionary<string, double>> ReadOnlineAsync(CancellationToken token = default, IReadOnlyCollection<string>? parameterKeys = null)
        {
            var requested = Targets(parameterKeys);
            token.ThrowIfCancellationRequested();
            if (GetMode(requested) != EN_COMM_STATE.Online)
                throw new InvalidOperationException("MELSEC 연결 상태가 변경되었습니다. Load를 다시 실행하세요.");
            var map = _map().ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
            var targets = requested.Select(key => (Key: key, Id: MapId(key))).ToArray();
            // Validate the entire set before issuing any read, so missing addresses cannot publish a partial load.
            foreach (var target in targets)
            {
                if (!map.TryGetValue(target.Id, out var row) || !row.Use || string.IsNullOrWhiteSpace(row.Address) || row.Address.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{target.Id}의 MELSEC 수신 주소가 설정되어 있지 않습니다.");
                if (row.Access == EN_MELSEC_ACCESS.Write || row.DataType is not (EN_MELSEC_DATA_TYPE.Double or EN_MELSEC_DATA_TYPE.Float))
                    throw new InvalidOperationException($"{target.Id}의 읽기 권한과 데이터 형식(DOUBLE/FLOAT)을 확인하세요.");
                CMelsec.ValidateMapData(row);
            }

            var result = await Task.Run(() =>
            {
                var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in targets)
                {
                    token.ThrowIfCancellationRequested();
                    var value = _read(target.Id, token); // Serialized by the existing shared MELSEC request worker.
                    if (!double.IsFinite(value))
                        throw new InvalidOperationException($"{target.Id}를 정상적으로 읽지 못했습니다.");
                    values[target.Key] = value;
                }

                return values;
            }, token);
            token.ThrowIfCancellationRequested();
            var currentMap = _map().ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
            if (GetMode(requested) != EN_COMM_STATE.Online || targets.Any(target => !currentMap.TryGetValue(target.Id, out var current) || current != map[target.Id]))
                throw new InvalidOperationException("MELSEC 연결 또는 맵이 변경되었습니다. Load를 다시 실행하세요.");
            return result;
        }

        private static string MapId(string key) => key.Replace("ALIGNBASE_", "ALIGN_LASTLOAD_", StringComparison.Ordinal);
        private static string[] Targets(IReadOnlyCollection<string>? parameterKeys)
        {
            var requested = (parameterKeys ?? CAlignBaseData.ParameterKeys).ToArray();
            if (requested.Length == 0 || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length || requested.Any(key => !CAlignBaseData.ParameterKeys.Contains(key)))
                throw new InvalidOperationException("Load 대상 키의 X/Y 항목을 확인하세요.");
            return requested;
        }
    }

    public sealed class CAlignOffsetViewerPanel : INotifyPropertyChanged
    {
        private readonly IReadOnlyDictionary<int, ST_DESIGN_SHOT_POINT[]> _shotsByCell;
        private readonly IReadOnlyDictionary<string, ST_HEAD_SHOT_POINT> _headShots;
        private readonly IReadOnlyDictionary<string, string> _parameters;
        private readonly Func<int, ST_CORRECTION_OFFSET_VIEWER_PREVIEW> _buildPreview;
        private readonly bool _reviewOffsets;
        public CAlignOffsetViewerPanel(
            ST_RECIPE_DEFINITION recipe, 
            IReadOnlyDictionary<string, string> parameters, 
            Func<int, ST_CORRECTION_OFFSET_VIEWER_PREVIEW> buildPreview, 
            bool reviewOffsets = false
        )
        {
            _reviewOffsets = reviewOffsets;
            RecipeName = recipe.Name;
            _buildPreview = buildPreview;
            var current = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            // Assignment is shown for all physical heads, including currently disabled heads.
            for (var headNo = 1; headNo <= 8; headNo++)
                current[$"H{headNo:00}_USE"] = "ON";
            _parameters = current;
            var designShots = CShotCoordinatePlanBuilder.BuildDesignShots(current);
            _shotsByCell = designShots.GroupBy(shot => shot.CellNo).ToDictionary(group => group.Key, group => group.ToArray());
            _headShots = CShotCoordinatePlanBuilder.Build(current).Shots.ToDictionary(shot => shot.ShotKey, StringComparer.Ordinal);
            TotalShotCount = designShots.Count;
            SelectCellCommand = new CButtonCommand(SelectCell);
            ApplyPreview(0);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public CButtonCommand SelectCellCommand { get; }
        public string RecipeName { get; }
        public string ViewerTitle => _reviewOffsets ? "Cur Review Offset View" : "Current Offset View";
        public string OffsetDescription => _reviewOffsets ? "Review Offset: Saved value   |   Scanner Converted Offset: Current head direction" : "Design Offset: X/Y   |   Scanner Converted Offset: Current head direction";
        public int TotalShotCount { get; }
        public ImageSource? PreviewImage { get; private set; }
        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellLabels { get; private set; } = [];
        public IReadOnlyList<ST_ALIGN_OFFSET_VIEW_ROW> Rows { get; private set; } = [];
        public IReadOnlyList<ST_ALIGN_OFFSET_VIEW_CARD_ROW> ShotRows { get; private set; } = [];
        public bool HasSelectedCell => Rows.Count > 0;
        public string Summary { get; private set; } = "";
        public string SelectedCellTitle { get; private set; } = "Select a Cell";

        private void SelectCell(object? value)
        {
            if (!int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo) || !_shotsByCell.TryGetValue(cellNo, out var shots))
                return;
            Rows = shots.OrderBy(shot => shot.CellShotNo).Select(shot =>
            {
                var assigned = _headShots.GetValueOrDefault(shot.ShotKey);
                var xValue = _reviewOffsets ? shot.ReviewOffsetX : shot.AlignOffsetX;
                var yValue = _reviewOffsets ? shot.ReviewOffsetY : shot.AlignOffsetY;
                var design = !_reviewOffsets || shot.ReviewOffsetIsDesign;
                var converted = assigned is null ? ((double X, double Y)? )null : CReviewOffsetData.ToScanner(_parameters, assigned.HeadNo, xValue, yValue, design);
                return new ST_ALIGN_OFFSET_VIEW_ROW(
                    CReviewShotNameFormatter.ToMatrixName(shot.CellShotNo, shot.GroupCountX), 
                    assigned is null ? "-" : $"H{assigned.HeadNo:00}", 
                    xValue, 
                    yValue, 
                    converted is { } x ? CAlignShotOffsetData.RoundValue(x.X) : null, 
                    converted is { } y ? CAlignShotOffsetData.RoundValue(y.Y) : null, 
                    assigned is null ? "Head not assigned" : CScannerAxisTransform.Format(CScannerAxisTransform.Read(_parameters, assigned.HeadNo))
                )
                {
                    StoredTitle = !_reviewOffsets ? "Design Offset (mm)" : design ? "Design Review Offset (mm)" : "Review Offset · Scanner (mm)"
                };
            }).ToArray();
            var cards = shots.OrderBy(shot => shot.CellShotNo).Zip(Rows).ToDictionary(pair => pair.First.ShotKey, pair => pair.Second);
            ShotRows = shots.GroupBy(shot => shot.GroupRow).OrderBy(group => group.Key).Select(group => new ST_ALIGN_OFFSET_VIEW_CARD_ROW(group.Key, group.OrderBy(shot => shot.GroupColumn).Select(shot => cards[shot.ShotKey]).ToArray())).ToArray();
            SelectedCellTitle = $"Cell{cellNo:00} / {Rows.Count:N0} Shots";
            ApplyPreview(cellNo);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private void ApplyPreview(int selectedCellNo)
        {
            var preview = _buildPreview(selectedCellNo);
            PreviewImage = preview.Image;
            CellLabels = preview.CellLabels.Select(label => label with { Width = 26 }).ToArray();
            Summary = preview.Summary;
        }
    }

    public sealed record ST_ALIGN_OFFSET_VIEW_ROW(string ShotName, string Head, double StoredX, double StoredY, double? ScannerX, double? ScannerY, string HeadTransform)
    {
        public string StoredTitle { get; init; } = "Design Offset (mm)";
    }

    public sealed record ST_ALIGN_OFFSET_VIEW_CARD_ROW(int RowNo, IReadOnlyList<ST_ALIGN_OFFSET_VIEW_ROW> Shots);
    public enum EN_FINE_LINE_STATE
    {
        Wait,
        Running,
        Complete,
        Ng,
        Error
    }

    /// <summary>Fine UI receives sequence snapshots. No device/file polling or motion commands in this view model.</summary>
    public sealed partial class CFineCorrectionPanel : INotifyPropertyChanged
    {
#region 공유 상태·초기값
        private readonly Action<string>? _requestAction;
        private readonly Func<string, EN_REVIEW_RESUME_CHOICE>? _chooseRetry;
        private IReadOnlyDictionary<string, string> _parameters = new Dictionary<string, string>();
        private ST_REVIEW_PLAN? _source;
        private ST_GLASS_PREVIEW_FRAME _sourceFrame = new(44, 62, 772, 420);
        private IReadOnlyList<ST_CELL_PREVIEW_LABEL> _sourceCells = [];
        private int _selectedBeam = 1;
        private ST_RECIPE_DEFINITION? _recipe;
        private int _sourceVersion;
        private bool _operationRunning;
        private readonly Dictionary<string, ST_FINE_REVIEW_LINE> _lastSnapshots = new();
        private double _width = 780, _height = 660;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? SourceInvalidated;
        public CReviewCameraPreview Camera { get; } = new();
        public string RecipeId { get; private set; } = "";
        public string RecipeName { get; private set; } = "-";
        public string StageY { get; private set; } = "-";
        public string ReviewX { get; private set; } = "-";
        public string Mode { get; private set; } = "OFFLINE";
        public int LineCount { get; private set; } = 1;
        public string InspectionMethod { get; private set; } = "FLYING";
        public IReadOnlyList<int> BeamNumbers { get; private set; } = [];
        public string LastResultPath { get; private set; } = "";
        public string Status { get; private set; } = "검사 대기";
        public Transform PreviewTransform { get; private set; } = Transform.Identity;
        public double PreviewWidth { get; private set; } = 780;
        public double PreviewHeight { get; private set; } = 660;
        public IReadOnlyList<ST_FINE_CELL_VISUAL> Cells { get; private set; } = [];
        public IReadOnlyList<CFineInspectionLine> Lines { get; private set; } = [];
        public IReadOnlyList<ST_FINE_LINE_VISUAL> LineVisuals { get; private set; } = [];
        public IReadOnlyList<PointCollection> Connections { get; private set; } = [];

        private ST_REVIEW_PLAN_POINT? _monitorFocus;
        private IReadOnlyList<ST_REVIEW_PLAN_POINT> _monitorNgPoints = [];
        private bool _offsetApplied;
        private bool _allOffsetResultsOk;
#endregion
#region 생성·초기화
        public CFineCorrectionPanel(Action<string>? requestAction = null, Func<string, EN_REVIEW_RESUME_CHOICE>? chooseRetry = null)
        {
            _requestAction = requestAction;
            _chooseRetry = chooseRetry;
            SelectLineCountCommand = new(SelectLineCount, _ => CanConfigure);
            SelectMethodCommand = new(SelectMethod, _ => CanConfigure);
            OpenLineDetailsCommand = new(value =>
            {
                if (value is CFineInspectionLine line && Lines.Contains(line))
                    ShowLineDetails?.Invoke(line);
            });
            ActionCommand = new(ExecuteAction, value => value?.ToString() switch
            {
                "START" => CanConfigure && Lines.Count > 0,
                "STOP" => IsRunning,
                "NG_RETRY" => CanConfigure && Lines.Any(line => line.State == EN_FINE_LINE_STATE.Ng),
                "OFFSET_APPLY" => CanApplyOffset,
                "OFFSET_INIT" => CanConfigure && _source?.Points.Count > 0,
                "OFFSET_VIEW" => CanConfigure && _recipe is not null,
                _ => false
            });
        }

#endregion
#region 기본 처리
        public CButtonCommand SelectLineCountCommand { get; }
        public CButtonCommand SelectMethodCommand { get; }
        public CButtonCommand ActionCommand { get; }
        public CButtonCommand OpenLineDetailsCommand { get; }
        public Action<CFineInspectionLine>? ShowLineDetails { get; set; }
        public Func<(string StageY, string ReviewX, string Mode)>? ReadFeedback { get; set; }
        public bool IsOneLine => LineCount == 1;
        public bool IsThreeLine => LineCount == 3;
        public bool IsFiveLine => LineCount == 5;
        public bool IsSevenLine => LineCount == 7;
        public bool IsFlying => InspectionMethod == "FLYING";
        public bool IsPtoP => InspectionMethod == "P2P";

        public int SelectedBeam
        {
            get => _selectedBeam;
            set
            {
                if (!CanConfigure || !BeamNumbers.Contains(value) || value == _selectedBeam)
                    return;
                _selectedBeam = value;
                BuildLines();
            }
        }

        public bool IsRunning { get; private set; }
        public bool CanConfigure => !IsRunning && !_operationRunning;
        public int SourceVersion => _sourceVersion;
        public Guid MonitorRunId { get; private set; }
        public int? RetryStartLineNo { get; private set; }
        public ImageSource? PreviewImage { get; private set; }
        public Rect GlassBounds { get; private set; }
        public string Summary => $"{Cells.Count:N0} Cells / {LineCount} Line per Cell / {Lines.Count:N0} Inspection Lines / {Lines.Sum(line => line.Points.Count):N0} Shots / Beam {SelectedBeam}";
        public string ModelOrder => string.Join(" → ", Lines.DistinctBy(line => line.ModelNo).Select(line => $"MODEL {line.ModelNo} ({line.ModelCellCount} Cells)"));

        public void Update(ST_RECIPE_DEFINITION? recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            if (recipe is null)
            {
                Clear("현재 레시피를 선택해 주세요.");
                return;
            }

            var parameters = recipe.Parameters.Where(item => item.Key.Length > 0).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var setting in settings.Where(item => item.Key.Length > 0))
                parameters[setting.Key] = setting.Value;
            // Saved offsets do not change Fine line selection or the design-coordinate route.
            foreach (var key in parameters.Keys.Where(key => key.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)).ToArray())
                parameters.Remove(key);
            if (RecipeId == recipe.Id && RecipeName == recipe.Name && parameters.Count == _parameters.Count && parameters.All(item => _parameters.GetValueOrDefault(item.Key) == item.Value))
                return;
            _parameters = parameters;
            _recipe = recipe;
            RecipeId = recipe.Id;
            RecipeName = recipe.Name;
            var assignment = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
            {
                ["HEAD_COUNT"] = "8"
            };
            for (var head = 1; head <= 8; head++)
                assignment[$"H{head:00}_USE"] = "ON";
            var shots = CShotCoordinatePlanBuilder.Build(assignment).Shots;
            var split = int.TryParse(parameters.GetValueOrDefault("SPLITED_BEAM_COUNT"), out var count) ? count : 1;
            if (!CCellPatternCalculator.IsSupportedSplitBeamCount(split))
                throw new InvalidOperationException("SPLITED_BEAM_COUNT를 확인해 주세요.");
            BeamNumbers = Enumerable.Range(1, split * split).ToArray();
            if (!BeamNumbers.Contains(_selectedBeam))
                _selectedBeam = BeamNumbers[0];
            var masking = CRecipeShotMaskingPlan.Build(parameters);
            var enabled = CHeadAssignmentPreviewBuilder.BuildEnabledHeadNumbers(parameters).ToHashSet();
            var points = shots.Select(shot => new ST_REVIEW_PLAN_POINT(
                shot.SequenceNo, 
                shot.ShotKey, 
                shot.HeadNo, 
                shot.CellNo, 
                shot.CellShotNo, 
                shot.GroupCountX, 
                shot.GroupCountY, 
                enabled.Contains(shot.HeadNo) && !masking.EvaluateShot(shot).ShouldSkipLaser, 
                shot.DesignX, 
                shot.DesignY, 
                0, 
                0, 
                0, 
                0, 
                EN_REVIEW_POINT_STATE.Ready, 
                "WAIT"
            )).ToArray();
            _source = new(
                recipe.Id, 
                points.Select(point => point.CellNo).Distinct().Count(), 
                ReadTolerance(parameters, "REVIEW_TOLERANCE_X"), 
                ReadTolerance(parameters, "REVIEW_TOLERANCE_Y"), 
                CReviewManager.ParseVisionFlipType(parameters.GetValueOrDefault("VisionXFlip"), parameters.GetValueOrDefault("VisionYFlip"), parameters.GetValueOrDefault("VisionXyFlip")), 
                DateTimeOffset.Now, 
                points
            );
            var preview = CHeadAssignmentPreviewBuilder.BuildRecipePreview(parameters, new HashSet<int>(), CHeadAssignmentPreviewBuilder.BuildHeadLayout(settings), showKeys: false);
            if (preview.ValidationMessage.Length > 0)
                throw new InvalidOperationException(preview.ValidationMessage);
            PreviewImage = preview.Image;
            _sourceFrame = preview.Frame;
            _sourceCells = preview.CellLabels;
            BuildLines();
            RefreshFeedback();
        }

        public void Clear(string message)
        {
            MonitorRunId = Guid.Empty;
            _monitorNgPoints = [];
            _monitorFocus = null;
            MonitorNgImage = null;
            _sourceVersion++;
            SourceInvalidated?.Invoke();
            _lastSnapshots.Clear();
            _recipe = null;
            LastResultPath = "";
            _offsetApplied = false;
            RecipeId = "";
            RecipeName = "-";
            _source = null;
            _sourceCells = [];
            _parameters = new Dictionary<string, string>();
            PreviewImage = null;
            Lines = [];
            Cells = [];
            LineVisuals = [];
            Connections = [];
            IsRunning = false;
            Camera.Clear();
            BeamNumbers = [];
            _selectedBeam = 1;
            Status = message;
            RetryStartLineNo = null;
            RefreshOffsetEligibility();
            Notify();
        }

        private void BuildLines()
        {
            if (_source is null)
            {
                Notify();
                return;
            }

            MonitorRunId = Guid.Empty;
            _sourceVersion++;
            SourceInvalidated?.Invoke();
            _lastSnapshots.Clear();
            LastResultPath = "";
            _offsetApplied = false;
            var selection = CReviewRulePlanBuilder.Build(_source, new(EN_REVIEW_CORRECTION_RULE.Fine, EN_REVIEW_CORRECTION_SCOPE.HeadCell, SelectedBeam, "", EN_REVIEW_REFERENCE_LINE.First, LineCount)).ShotKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cellModels = _source.Points.Select(point => point.CellNo).Distinct().ToDictionary(cell => cell, cell => CRecipeModelPixelCountReader.ReadCellModelType(_parameters, cell));
            var counts = cellModels.Values.GroupBy(model => model).ToDictionary(group => group.Key, group => group.Count());
            var result = new List<CFineInspectionLine>();
            var stageDirection = CScannerAxisTransform.ReadStageDirection(_parameters);
            var calculatePosition = CReviewManager.CreateShotBeamPositionCalculator(_recipe!, stageDirection);
            ST_REVIEW_BEAM_POSITION Position(ST_REVIEW_PLAN_POINT point) => calculatePosition(point, SelectedBeam);
            foreach (var model in _source.Points.Where(point => point.Use && selection.Contains(point.ShotKey)).GroupBy(point => cellModels[point.CellNo]).OrderByDescending(group => counts[group.Key]).ThenBy(group => group.Key))
            {
                var row = 0;
                foreach (var line in model.GroupBy(point => Math.Round(point.DesignY, 6)).OrderBy(group => group.Key))
                {
                    var leftToRight = row++ % 2 == 0;
                    var ordered = (leftToRight ? line.OrderBy(point => point.DesignX) : line.OrderByDescending(point => point.DesignX)).ThenBy(point => point.CellNo).ThenBy(point => point.ShotNo).Select(point =>
                    {
                        var position = Position(point);
                        return point with
                        {
                            BeamNo = SelectedBeam,
                            ReviewTargetX = position.ReviewX,
                            ReviewTargetY = position.StageY
                        };
                    }).ToArray();
                    result.Add(new(
                        $"B{SelectedBeam}:{model.Key}:{line.Key.ToString("R", CultureInfo.InvariantCulture)}", 
                        result.Count + 1, 
                        model.Key, 
                        counts[model.Key], 
                        line.Key, 
                        leftToRight, 
                        ordered, 
                        Position(ordered[0]), 
                        Position(ordered[^1])
                    ));
                }
            }

            Lines = result;
            IsRunning = false;
            RetryStartLineNo = null;
            Status = "검사 대기";
            RefreshOffsetEligibility();
            Layout();
            Notify();
        }

        private void SelectLineCount(object? value)
        {
            if (!CanConfigure || !int.TryParse(value?.ToString(), out var count) || count is not (1 or 3 or 5 or 7) || LineCount == count)
                return;
            LineCount = count;
            BuildLines();
        }

        private void SelectMethod(object? value)
        {
            var method = value?.ToString();
            if (!CanConfigure || method is not ("FLYING" or "P2P") || InspectionMethod == method)
                return;
            InspectionMethod = method;
            BuildLines();
        }

        private void ExecuteAction(object? value)
        {
            var action = value?.ToString() ?? "";
            if (!ActionCommand.CanExecute(action))
                return;
            if (action == "NG_RETRY")
            {
                var first = Lines.First(line => line.State == EN_FINE_LINE_STATE.Ng);
                var choice = _chooseRetry?.Invoke($"NG Line #{first.OrderNo} / MODEL {first.ModelNo}\n처음부터 검사할지, NG가 발생한 라인부터 검사할지 선택해 주세요.") ?? EN_REVIEW_RESUME_CHOICE.Cancel;
                if (choice == EN_REVIEW_RESUME_CHOICE.Cancel)
                    return;
                RetryStartLineNo = choice == EN_REVIEW_RESUME_CHOICE.Restart ? 1 : first.OrderNo;
                Status = choice == EN_REVIEW_RESUME_CHOICE.Restart ? "재검사 선택: 처음부터" : $"재검사 선택: NG Line #{first.OrderNo}부터";
                Notify();
                action = choice == EN_REVIEW_RESUME_CHOICE.Restart ? "RESTART" : "RESUME_NG";
            }

            _requestAction?.Invoke(action);
        }

        /// <summary>Status-only adapter used by UI checks.</summary>
        public void ApplyLineStates(IReadOnlyDictionary<string, EN_FINE_LINE_STATE> states, bool isRunning)
        {
            foreach (var line in Lines)
                if (states.TryGetValue(line.Key, out var state))
                    line.Update(state);
            IsRunning = isRunning;
            RefreshOffsetEligibility();
            Status = isRunning ? "검사 중" : Lines.Any(line => line.State == EN_FINE_LINE_STATE.Ng) ? "NG 발생 · NG RETRY에서 재검사 시작 위치를 선택해 주세요." : "검사 대기";
            Notify();
        }

        public ST_FINE_REVIEW_REQUEST CreateRequest(int startLineNo = 1, bool resume = false)
        {
            if (_recipe is null)
                throw new InvalidOperationException("현재 레시피가 없습니다.");
            var source = _source ?? throw new InvalidOperationException("검사 대상이 없습니다.");
            var lines = Lines.Select(line => new ST_FINE_REVIEW_LINE(
                line.Key, 
                line.OrderNo, 
                line.ModelNo, 
                Array.AsReadOnly(line.Points.ToArray()), 
                line.State switch
            {
                EN_FINE_LINE_STATE.Complete => EN_FINE_REVIEW_LINE_STATE.Complete,
                EN_FINE_LINE_STATE.Ng => EN_FINE_REVIEW_LINE_STATE.Ng,
                EN_FINE_LINE_STATE.Error => EN_FINE_REVIEW_LINE_STATE.Error,
                EN_FINE_LINE_STATE.Running => EN_FINE_REVIEW_LINE_STATE.Running,
                _ => EN_FINE_REVIEW_LINE_STATE.Wait
            }
            )).ToArray();
            var plan = source with
            {
                Points = Array.AsReadOnly(lines.SelectMany(line => line.Points).ToArray())
            };
            return new(plan, Array.AsReadOnly(lines), IsFlying ? EN_FINE_INSPECTION_METHOD.Flying : EN_FINE_INSPECTION_METHOD.PtoP, startLineNo, resume)
            {
                Context = new(RecipeName, "-", "MANUAL FINE", LineCount, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_parameters)))
            };
        }

        public void SetOperationRunning(bool value)
        {
            _operationRunning = value;
            Notify();
        }

        public void Prepare()
        {
            IsRunning = true;
            Status = "검사 준비 중 · 레시피/목표 위치 확인";
            Notify();
        }

        public void Begin()
        {
            _lastSnapshots.Clear();
            LastResultPath = "";
            _offsetApplied = false;
            _allOffsetResultsOk = false;
            IsRunning = true;
            Status = "검사 준비 중";
            Notify();
        }

        public void End()
        {
            IsRunning = false;
            Notify();
        }

        public void SetError(string message)
        {
            Status = message;
            Notify();
        }

        public void ApplyProgress(ST_FINE_REVIEW_SNAPSHOT snapshot, int version)
        {
            if (version != _sourceVersion || snapshot.RecipeId != RecipeId)
                return;
            MonitorRunId = snapshot.RunId;
            var byKey = Lines.ToDictionary(line => line.Key);
            var layoutChanged = false;
            foreach (var incoming in snapshot.Lines)
            {
                if (_lastSnapshots.GetValueOrDefault(incoming.Key) == incoming || !byKey.TryGetValue(incoming.Key, out var line))
                    continue;
                layoutChanged |= line.ApplyProgress(incoming, _source!.VisionFlipType);
                _lastSnapshots[incoming.Key] = incoming;
            }

            IsRunning = snapshot.State == EN_REVIEW_SEQUENCE_STATE.Running;
            Status = snapshot.Message + (snapshot.ResultFilePath.Length > 0 ? " · 결과 저장 완료" : "");
            LastResultPath = snapshot.ResultFilePath;
            RefreshOffsetEligibility();
            if (layoutChanged)
                Layout();
            Notify();
        }

        private static double ReadTolerance(IReadOnlyDictionary<string, string> parameters, string key) => double.TryParse(parameters.GetValueOrDefault(key, "0.03"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : throw new InvalidOperationException($"{key}를 확인해 주세요.");
        /// <summary>Replace one line's received Target-Hole snapshot. Call on the UI dispatcher, like ApplyLineStates.
        /// Missing Shots remain unmeasured. Execution state is supplied separately by the sequence.</summary>
        public void ApplyLineResults(string lineKey, IReadOnlyList<ST_FINE_SHOT_RESULT> results)
        {
            var line = Lines.SingleOrDefault(item => item.Key == lineKey) ?? throw new ArgumentException("검사 대상에 없는 라인입니다.", nameof(lineKey));
            line.ApplyResults(results, _source!.VisionFlipType);
            RefreshOffsetEligibility();
            Notify();
        }

        public void RefreshFeedback()
        {
            if (ReadFeedback is null)
                return;
            var values = ReadFeedback();
            Camera.UpdateFeedback(values.StageY, values.ReviewX, values.Mode);
            if ((StageY, ReviewX, Mode) == values)
                return;
            StageY = values.StageY;
            ReviewX = values.ReviewX;
            Mode = values.Mode;
            Notify();
        }

        public void ResizePreview(double width, double height)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 || Math.Abs(width - _width) < 0.5 && Math.Abs(height - _height) < 0.5)
                return;
            _width = width;
            _height = height;
            Layout();
            Notify();
        }

        private void Layout()
        {
            const double margin = 16, spacing = 19, columnStep = 34;
            PreviewWidth = Math.Max(300, _width);
            var largestSide = Math.Max(Lines.Count(line => line.LeftToRight), Lines.Count(line => !line.LeftToRight));
            var capacity = Math.Max(1, (int)((_height - 2 * margin) / spacing));
            var maxColumns = Math.Max(1, (int)((PreviewWidth - 150 - 2 * margin) / (2 * columnStep)));
            var columns = Math.Clamp((largestSide + capacity - 1) / capacity, 1, maxColumns);
            PreviewHeight = Math.Max(_height, Math.Ceiling(largestSide / (double)columns) * spacing + 2 * margin);
            capacity = Math.Max(1, (int)((PreviewHeight - 2 * margin) / spacing));
            int Columns(bool side) => Math.Min(columns, (Lines.Count(line => line.LeftToRight == side) + capacity - 1) / capacity);
            var leftGutter = 20 + Columns(true) * columnStep;
            var rightGutter = 20 + Columns(false) * columnStep;
            var availableWidth = PreviewWidth - leftGutter - rightGutter;
            var scale = Math.Min(availableWidth / _sourceFrame.Width, (PreviewHeight - 2 * margin) / _sourceFrame.Height);
            var left = leftGutter + (availableWidth - _sourceFrame.Width * scale) / 2;
            var top = (PreviewHeight - _sourceFrame.Height * scale) / 2;
            var transform = new MatrixTransform(scale, 0, 0, scale, left - _sourceFrame.CanvasLeft * scale, top - _sourceFrame.CanvasTop * scale);
            transform.Freeze();
            PreviewTransform = transform;
            GlassBounds = transform.TransformBounds(new Rect(_sourceFrame.CanvasLeft, _sourceFrame.CanvasTop, _sourceFrame.Width, _sourceFrame.Height));
            Camera.Configure(_parameters, GlassBounds);
            var physicalScale = GlassBounds.Width / Read("GLASS_SIZE_X", 925);
            Point Project(double x, double y) => new(GlassBounds.Left + x * physicalScale, GlassBounds.Top + y * physicalScale);
            Cells = _sourceCells.Select(cell => new ST_FINE_CELL_VISUAL(cell.CellNo, CRecipeModelPixelCountReader.ReadCellModelType(_parameters, cell.CellNo), transform.TransformBounds(cell.CellBounds))).ToArray();
            var visuals = new List<ST_FINE_LINE_VISUAL>();
            foreach (var side in new[]
            {
                true,
                false
            }

            )
            {
                var lines = Lines.Where(line => line.LeftToRight == side).OrderBy(line => line.DesignY).ThenBy(line => line.OrderNo).ToArray();
                var sideColumns = Columns(side);
                if (sideColumns == 0)
                    continue;
                var rows = (lines.Length + sideColumns - 1) / sideColumns;
                var centers = Enumerable.Range(0, rows).Select(row => lines.Skip(row * sideColumns).Take(sideColumns).Average(line => Project(0, line.DesignY).Y)).ToArray();
                for (var i = 0; i < centers.Length; i++)
                    centers[i] = Math.Max(Math.Clamp(centers[i], margin, PreviewHeight - margin), i == 0 ? margin : centers[i - 1] + spacing);
                for (var i = centers.Length - 1; i >= 0; i--)
                    centers[i] = Math.Min(centers[i], i == centers.Length - 1 ? PreviewHeight - margin : centers[i + 1] - spacing);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var start = Project(line.Points[0].DesignX, line.DesignY);
                    var end = Project(line.Points[^1].DesignX, line.DesignY);
                    if (Math.Abs(start.X - end.X) < 5)
                    {
                        start.X += side ? -3 : 3;
                        end.X += side ? 3 : -3;
                    }

                    var column = i % sideColumns;
                    var center = centers[i / sideColumns];
                    var badgeX = side ? GlassBounds.Left - 42 - column * columnStep : GlassBounds.Right + 14 + column * columnStep;
                    var leaderEdge = badgeX + (side ? 28 : 0);
                    var elbow = side ? GlassBounds.Left - 6 - column * 2 : GlassBounds.Right + 6 + column * 2;
                    var approach = leaderEdge + (side ? 3 : -3);
                    var busY = center - 11;
                    var arrowX = start.X + (end.X - start.X) * 0.6;
                    var direction = side ? 1 : -1;
                    visuals.Add(new(
                        line, 
                        Points(start, end), 
                        Points(new(arrowX - direction * 5, start.Y - 3), new(arrowX, start.Y), new(arrowX - direction * 5, start.Y + 3)), 
                        Points(start, new(elbow, start.Y), new(elbow, busY), new(approach, busY), new(approach, center), new(leaderEdge, center)), 
                        badgeX, 
                        center - 8
                    ));
                }
            }

            LineVisuals = visuals.OrderBy(visual => visual.Line.OrderNo).ToArray();
            Connections = LineVisuals.Zip(LineVisuals.Skip(1), (previous, next) =>
            {
                var end = previous.Segment[^1];
                var start = next.Segment[0];
                var sideX = previous.Line.LeftToRight ? GlassBounds.Right + 8 : GlassBounds.Left - 8;
                return Points(end, new(sideX, end.Y), new(sideX, start.Y), start);
            }).ToArray();
            RefreshMonitorMarkers();
        }

        private static PointCollection Points(params Point[] points)
        {
            var result = new PointCollection(points);
            result.Freeze();
            return result;
        }

        private double Read(string key, double fallback) => double.TryParse(_parameters.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0 ? value : fallback;
        private void Notify()
        {
            PropertyChanged?.Invoke(this, new(null));
            SelectLineCountCommand.NotifyCanExecuteChanged();
            SelectMethodCommand.NotifyCanExecuteChanged();
            ActionCommand.NotifyCanExecuteChanged();
        }

#endregion
#region Monitor
        public ImageSource? MonitorNgImage { get; private set; }
        public Visibility MonitorFocusVisibility => _monitorFocus is null ? Visibility.Collapsed : Visibility.Visible;
        public double MonitorFocusX => _monitorFocus is null ? 0 : ProjectMonitorPoint(_monitorFocus).X - 7;
        public double MonitorFocusY => _monitorFocus is null ? 0 : ProjectMonitorPoint(_monitorFocus).Y - 7;

        internal void SetMonitorFocus(ST_REVIEW_PLAN_POINT? point)
        {
            _monitorFocus = point;
            Notify();
        }

        internal void SetMonitorNgPoints(IReadOnlyList<ST_REVIEW_PLAN_POINT> points)
        {
            if (_monitorNgPoints.SequenceEqual(points))
                return;
            _monitorNgPoints = points;
            RefreshMonitorMarkers();
            Notify();
        }

        private void RefreshMonitorMarkers()
        {
            if (_monitorNgPoints.Count == 0)
            {
                MonitorNgImage = null;
                return;
            }

            // One frozen drawing instead of a WPF control per NG Shot.
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, PreviewWidth, PreviewHeight));
                foreach (var point in _monitorNgPoints)
                    context.DrawEllipse(Brushes.Red, null, ProjectMonitorPoint(point), 3, 3);
            }

            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            MonitorNgImage = image;
        }

        /// <summary>Read-only projection of the actual run plan, never a plan regenerated from current UI selections.</summary>
        internal void AttachMonitor(ST_FINE_REVIEW_REQUEST request)
        {
            Clear("검사 계획 표시");
            _operationRunning = true;
            _source = request.Plan;
            RecipeId = request.Plan.RecipeId;
            RecipeName = request.Context?.RecipeName ?? RecipeId;
            _parameters = request.Context?.Parameters ?? new Dictionary<string, string>();
            LineCount = request.Context?.LinesPerCell ?? 1;
            InspectionMethod = request.Method == EN_FINE_INSPECTION_METHOD.Flying ? "FLYING" : "P2P";
            _selectedBeam = request.Plan.Points.FirstOrDefault()?.BeamNo ?? 1;
            if (_parameters.Count > 0)
            {
                var settings = _parameters.Select(pair => new ST_SYSTEM_PARAMETER(EN_SETTING_TAB.Option, pair.Key, pair.Value, "", "", Key: pair.Key)).ToArray();
                var preview = CHeadAssignmentPreviewBuilder.BuildRecipePreview(_parameters, new HashSet<int>(), CHeadAssignmentPreviewBuilder.BuildHeadLayout(settings), showKeys: false);
                if (preview.ValidationMessage.Length > 0)
                    throw new InvalidOperationException(preview.ValidationMessage);
                PreviewImage = preview.Image;
                _sourceFrame = preview.Frame;
                _sourceCells = preview.CellLabels;
            }

            var modelCounts = request.Lines.GroupBy(line => line.ModelNo).ToDictionary(group => group.Key, group => group.SelectMany(line => line.Points).Select(point => point.CellNo).Distinct().Count());
            Lines = request.Lines.Select(line => new CFineInspectionLine(line.Key, line.Number, line.ModelNo, modelCounts[line.ModelNo], line.Points[0].DesignY, line.Points[0].DesignX <= line.Points[^1].DesignX, line.Points)).ToArray();
            Layout();
            Notify();
        }

        internal Point ProjectMonitorPoint(ST_REVIEW_PLAN_POINT point)
        {
            var scale = GlassBounds.Width / Read("GLASS_SIZE_X", 925);
            return new(GlassBounds.Left + point.DesignX * scale, GlassBounds.Top + point.DesignY * scale);
        }

#endregion
#region Offset
        public bool CanApplyOffset => CanConfigure && !_offsetApplied && _allOffsetResultsOk;
        public string OffsetStatus => _offsetApplied ? "현재 검사 결과 적용 완료" : Lines.Any(line => line.NgCount > 0 || line.State == EN_FINE_LINE_STATE.Ng) ? "NG 발생 · Offset 전체 적용 불가" : CanApplyOffset ? "모든 검사 OK · Offset 적용 가능" : "모든 검사 대상 OK 완료 후 적용 가능";

        public IReadOnlyList<ST_REVIEW_PLAN_POINT> GetOffsetTargets() => _source?.Points.Where(point => point.HeadNo > 0).ToArray() ?? throw new InvalidOperationException("현재 레시피가 없습니다.");
        public ST_FINE_OFFSET_INPUT CreateOffsetInput()
        {
            if (_offsetApplied)
                throw new InvalidOperationException("이미 적용한 검사 결과입니다. 다시 검사한 후 적용해 주세요.");
            if (Lines.Any(line => line.State == EN_FINE_LINE_STATE.Ng || line.NgCount > 0))
                throw new InvalidOperationException("NG가 있어 FINE Offset 전체를 적용할 수 없습니다. NG RETRY 후 다시 적용해 주세요.");
            if (Lines.Count == 0 || Lines.Any(line => line.State != EN_FINE_LINE_STATE.Complete))
                throw new InvalidOperationException("검사가 정상 완료되어야 Offset을 적용할 수 있습니다.");
            return new(GetOffsetTargets(), Lines.SelectMany(line => line.Points).ToArray(), _source!.VisionFlipType);
        }

        private void RefreshOffsetEligibility() => _allOffsetResultsOk = Lines.Count > 0 && Lines.All(line => line.State == EN_FINE_LINE_STATE.Complete && line.NgCount == 0 && line.Points.All(point => point.State == EN_REVIEW_POINT_STATE.Ok && string.Equals(point.Judge?.Trim(), "OK", StringComparison.OrdinalIgnoreCase)));
        public void AcceptOffsetSave(bool initialize, string fileName)
        {
            _offsetApplied = !initialize;
            Status = $"Review Offset {(initialize ? "초기화" : "누적 적용")} 완료 · {fileName}";
            Notify();
        }
#endregion
    }

    public sealed record ST_FINE_LINE_VISUAL(CFineInspectionLine Line, PointCollection Segment, PointCollection Arrow, PointCollection Leader, double BadgeX, double BadgeY);
    public sealed record ST_FINE_CELL_VISUAL(int CellNo, int ModelNo, Rect Bounds)
    {
        public string Tooltip => $"Cell{CellNo:00} / MODEL {ModelNo}";
    }

    public sealed record ST_FINE_OFFSET_INPUT(IReadOnlyList<ST_REVIEW_PLAN_POINT> Targets, IReadOnlyList<ST_REVIEW_PLAN_POINT> Measurements, EN_VISION_FLIP_TYPE Flip);
    /// <summary>Raw received Target-Hole values, before Vision Flip. Null coordinates represent an NG without a numeric result.</summary>
    public sealed record ST_FINE_SHOT_RESULT(string ShotKey, double? ReceivedX, double? ReceivedY, string Judge);
    public sealed class CFineInspectionLine : INotifyPropertyChanged
    {
        public CFineInspectionLine(
            string key, 
            int orderNo, 
            int modelNo, 
            int modelCellCount, 
            double designY, 
            bool leftToRight, 
            IReadOnlyList<ST_REVIEW_PLAN_POINT> points, 
            ST_REVIEW_BEAM_POSITION? startPosition = null, 
            ST_REVIEW_BEAM_POSITION? endPosition = null
        )
        {
            Key = key;
            OrderNo = orderNo;
            ModelNo = modelNo;
            ModelCellCount = modelCellCount;
            DesignY = designY;
            LeftToRight = leftToRight;
            Points = points;
            StartPosition = startPosition;
            EndPosition = endPosition;
            ApplyResults([], EN_VISION_FLIP_TYPE.Normal);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Key { get; }
        public int OrderNo { get; }
        public int ModelNo { get; }
        public int ModelCellCount { get; }
        public double DesignY { get; }
        public bool LeftToRight { get; private set; }
        public IReadOnlyList<ST_REVIEW_PLAN_POINT> Points { get; private set; }
        public EN_FINE_LINE_STATE State { get; private set; }
        public string StateText => State switch
        {
            EN_FINE_LINE_STATE.Running => "RUNNING",
            EN_FINE_LINE_STATE.Complete => "COMPLETE",
            EN_FINE_LINE_STATE.Ng => "NG",
            EN_FINE_LINE_STATE.Error => "ERROR",
            _ => "WAIT"
        };
        public Brush StateBrush => State switch
        {
            EN_FINE_LINE_STATE.Running or EN_FINE_LINE_STATE.Complete => Brushes.SeaGreen,
            EN_FINE_LINE_STATE.Ng or EN_FINE_LINE_STATE.Error => Brushes.Firebrick,
            _ => Brushes.DarkGoldenrod
        };
        public string Title => $"#{OrderNo}  MODEL {ModelNo}  {(LeftToRight ? "→" : "←")}";
        public string Detail => $"Y {DesignY:0.000} mm · {Points.Select(point => point.CellNo).Distinct().Count()} Cells · {Points.Count} Shots · Beam {Points[0].BeamNo}";
        public ST_REVIEW_BEAM_POSITION? StartPosition { get; private set; }
        public ST_REVIEW_BEAM_POSITION? EndPosition { get; private set; }
        public string StartStageY => Format(StartPosition?.StageY);
        public string EndStageY => Format(EndPosition?.StageY);
        public string StartReviewX => Format(StartPosition?.ReviewX);
        public string EndReviewX => Format(EndPosition?.ReviewX);
        public IReadOnlyList<ST_FINE_SHOT_DETAIL> ShotResults { get; private set; } = [];
        public IReadOnlyList<ST_FINE_AXIS_STATISTICS> Statistics { get; private set; } = [];
        public int MeasuredCount { get; private set; }
        public int NgCount { get; private set; }
        public int UnmeasuredCount => Points.Count - MeasuredCount;
        public string ResultSummary => $"검사 {MeasuredCount} / {Points.Count} · NG {NgCount} · 미검사 {UnmeasuredCount}";
        public Brush NgBrush => NgCount > 0 ? Brushes.Firebrick : StateBrush;

        public void Update(EN_FINE_LINE_STATE state)
        {
            if (State == state)
                return;
            State = state;
            PropertyChanged?.Invoke(this, new(null));
        }

        internal bool ApplyProgress(ST_FINE_REVIEW_LINE line, EN_VISION_FLIP_TYPE flip)
        {
            var routeChanged = !Points.Select(point => point.ShotKey).SequenceEqual(line.Points.Select(point => point.ShotKey));
            Points = line.Points;
            StartPosition = new(Points[0].BeamNo, Points[0].ReviewTargetY, Points[0].ReviewTargetX);
            EndPosition = new(Points[^1].BeamNo, Points[^1].ReviewTargetY, Points[^1].ReviewTargetX);
            if (Points.Count > 1)
                LeftToRight = Points[0].DesignX <= Points[^1].DesignX;
            State = line.State switch
            {
                EN_FINE_REVIEW_LINE_STATE.Running => EN_FINE_LINE_STATE.Running,
                EN_FINE_REVIEW_LINE_STATE.Complete => EN_FINE_LINE_STATE.Complete,
                EN_FINE_REVIEW_LINE_STATE.Ng => EN_FINE_LINE_STATE.Ng,
                EN_FINE_REVIEW_LINE_STATE.Error => EN_FINE_LINE_STATE.Error,
                _ => EN_FINE_LINE_STATE.Wait
            };
            ApplyResults(Points.Where(point => point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng or EN_REVIEW_POINT_STATE.Current || point.Judge == "ERROR").Select(point => new ST_FINE_SHOT_RESULT(
                point.ShotKey, 
                point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? point.ErrorX : null, 
                point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? point.ErrorY : null, 
                point.State == EN_REVIEW_POINT_STATE.Current ? "RUNNING" : point.Judge
            )).ToArray(), flip);
            return routeChanged;
        }

        internal void ApplyResults(IReadOnlyList<ST_FINE_SHOT_RESULT> results, EN_VISION_FLIP_TYPE flip)
        {
            var keys = Points.Select(point => point.ShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var received = new Dictionary<string, ST_FINE_SHOT_RESULT>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in results)
            {
                var judge = result.Judge?.Trim().ToUpperInvariant() ?? "";
                if (!keys.Contains(result.ShotKey) || !received.TryAdd(result.ShotKey, result with { Judge = judge }))
                    throw new ArgumentException("라인에 속하지 않거나 중복된 Shot 결과입니다.", nameof(results));
                if (judge is not ("OK" or "NG" or "ERROR" or "RUNNING") || result.ReceivedX.HasValue != result.ReceivedY.HasValue || result.ReceivedX is { } x && !double.IsFinite(x) || result.ReceivedY is { } y && !double.IsFinite(y) || judge == "OK" && !result.ReceivedX.HasValue || (judge is "ERROR" or "RUNNING") && result.ReceivedX.HasValue)
                    throw new ArgumentException("검사 결과의 판정 또는 X/Y 수치가 올바르지 않습니다.", nameof(results));
            }

            // Compute once per result snapshot, never on the position feedback timer. Include numeric NG results;
            // unmeasured and NG without coordinates must not contribute a fabricated zero to the statistics.
            var rows = Points.Select((point, index) =>
            {
                received.TryGetValue(point.ShotKey, out var result);
                ST_REVIEW_COORDINATE_OFFSET? converted = result?.ReceivedX is { } x && result.ReceivedY is { } y ? CReviewManager.VisionErrorToStageOffset(x, y, flip) : null;
                return new ST_FINE_SHOT_DETAIL(
                    index + 1, 
                    $"HEAD {point.HeadNo:00}", 
                    $"Cell{point.CellNo:00}", 
                    CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX), 
                    point.BeamNo, 
                    result?.ReceivedX, 
                    result?.ReceivedY, 
                    converted?.X, 
                    converted?.Y, 
                    result?.Judge ?? "미검사"
                );
            }).ToArray();
            var statistics = new[]
            {
                ST_FINE_AXIS_STATISTICS.Create("X", rows.Select(row => row.ConvertedX)),
                ST_FINE_AXIS_STATISTICS.Create("Y", rows.Select(row => row.ConvertedY))
            };
            ShotResults = rows;
            Statistics = statistics;
            MeasuredCount = received.Values.Count(result => result.Judge is "OK" or "NG");
            NgCount = received.Values.Count(result => result.Judge == "NG");
            PropertyChanged?.Invoke(this, new(null));
        }

        internal static string Format(double? value) => value is { } number ? (Math.Round(number, 3, MidpointRounding.AwayFromZero) + 0d).ToString("0.000", CultureInfo.InvariantCulture) : "-";
    }

    public sealed record ST_FINE_SHOT_DETAIL(int No, string Head, string Cell, string Shot, int BeamNo, double? ReceivedX, double? ReceivedY, double? ConvertedX, double? ConvertedY, string Judge)
    {
        public string ReceivedXText => CFineInspectionLine.Format(ReceivedX);
        public string ReceivedYText => CFineInspectionLine.Format(ReceivedY);
        public string ConvertedXText => CFineInspectionLine.Format(ConvertedX);
        public string ConvertedYText => CFineInspectionLine.Format(ConvertedY);
        public Brush JudgeBrush => Judge switch
        {
            "NG" or "ERROR" => Brushes.Firebrick,
            "OK" or "RUNNING" => Brushes.SeaGreen,
            _ => Brushes.DarkGoldenrod
        };
    }

    public sealed record ST_FINE_AXIS_STATISTICS(string Axis, string Maximum, string Minimum, string Average)
    {
        internal static ST_FINE_AXIS_STATISTICS Create(string axis, IEnumerable<double?> coordinates)
        {
            var values = coordinates.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            return values.Length == 0 ? new(axis, "-", "-", "-") : new(axis, CFineInspectionLine.Format(values.Max()), CFineInspectionLine.Format(values.Min()), CFineInspectionLine.Format(values.Average()));
        }
    }

    public sealed record ST_CORRECTION_TAB(string Name, bool IsSelected);
    public sealed record ST_CORRECTION_SOURCE_ROW(string Type, string Name, string Value, string State, string Source)
    {
        public Brush StateBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(State);
            }
        }
    }

    public sealed record ST_CORRECTION_VALUE_ROW(string Item, string Target, string Value, string Unit, string State)
    {
        public Brush ValueBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(State);
            }
        }

        public Brush StateBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(State);
            }
        }
    }

    public sealed record ST_CORRECTION_HISTORY_ROW(string Time, string User, string Tab, string Item, string Before, string After, string Result)
    {
        public string DisplayTime
        {
            get
            {
                return FormatDisplayTime(Time);
            }
        }

        public string ApplyResult
        {
            get
            {
                return IsApplyOk(Result) ? "OK" : "NG";
            }
        }

        public Brush ResultBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(Result);
            }
        }

        public Brush ApplyResultBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(ApplyResult);
            }
        }

        private static string FormatDisplayTime(string value)
        {
            var text = (value ?? "").Trim();
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dateTime))
            {
                return dateTime.ToString("yy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time))
            {
                return DateTime.Today.Add(time).ToString("yy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return text;
        }

        private static bool IsApplyOk(string value)
        {
            return (value ?? "").Trim().ToUpperInvariant()is "OK" or "SAVED" or "APPLIED" or "LOADED" or "READY" or "CURRENT";
        }
    }

    public sealed record ST_CORRECTION_OFFSET_SHOT_MATRIX_ROW(int RowNo, IReadOnlyList<ST_CORRECTION_OFFSET_SHOT_ITEM> Shots);
    public sealed record ST_CORRECTION_OFFSET_SHOT_ITEM(string ShotKey, string ShotName, string Head, string StoredTitle, string StoredOffset, string ConvertedOffset, string CoordinateDetail);
    public sealed record ST_CORRECTION_OFFSET_VIEWER_PREVIEW(ImageSource? Image, IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellLabels, string Summary);
    internal readonly record struct ST_CORRECTION_AXIS_SETTING_STATE(string Summary, string Detail, string Vision, string Scanner, ST_CORRECTION_AXIS_FLIP_ROW VisionRow, IReadOnlyList<ST_CORRECTION_AXIS_FLIP_ROW> ScannerRows);
    public sealed record ST_CORRECTION_AXIS_FLIP_ROW(string Name, bool XFlip, bool YFlip, bool XyFlip)
    {
        public string XState => XFlip ? "ON" : "OFF";
        public string YState => YFlip ? "ON" : "OFF";
        public string XyState => XyFlip ? "ON" : "OFF";
    }

    public sealed record ST_CORRECTION_REVIEW_RESULT_ROW(string ShotKey, string Head, int Cell, double ErrorXValue, double ErrorYValue, string Judge)
    {
        public string ErrorX
        {
            get
            {
                return FormatDisplayValue(ErrorXValue);
            }
        }

        public string ErrorY
        {
            get
            {
                return FormatDisplayValue(ErrorYValue);
            }
        }

        public Brush JudgeBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(Judge);
            }
        }

        private static string FormatDisplayValue(double value)
        {
            return value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        }
    }

    public sealed record ST_CORRECTION_REVIEW_OFFSET_ROW(string ShotKey, string Head, double OffsetXValue, double OffsetYValue, string State)
    {
        public string OffsetX
        {
            get
            {
                return FormatDisplayValue(OffsetXValue);
            }
        }

        public string OffsetY
        {
            get
            {
                return FormatDisplayValue(OffsetYValue);
            }
        }

        public Brush StateBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(State);
            }
        }

        public Brush OffsetValueBrush
        {
            get
            {
                return State.Trim().ToUpperInvariant() switch
                {
                    "PENDING" => CStatusBrush.Wait,
                    "SAVED" => CStatusBrush.Online,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }

        private static string FormatDisplayValue(double value)
        {
            return value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Projects one offset component from the loaded Cell snapshot; selection performs no I/O.</summary>
    public sealed class COutputOffsetViewerPanel : INotifyPropertyChanged
    {
        private static readonly (string Key, string Name)[] Options = [("RECIPE", "Recipe"), ("REVIEW", "Review"), ("ALIGN", "Align"), ("HEAD", "Head"), ("FIRST_BEAM", "First Beam"), ("SUM", "SUM")];
        private IReadOnlyDictionary<string, string> _parameters = new Dictionary<string, string>();
        private IReadOnlyList<ST_HEAD_SHOT_POINT> _shots = [];
        public COutputOffsetViewerPanel() => SelectKindCommand = new(SelectKind);
        public event PropertyChangedEventHandler? PropertyChanged;
        public CButtonCommand SelectKindCommand { get; }
        public string SelectedKind { get; private set; } = "SUM";
        public IReadOnlyList<ST_CORRECTION_OFFSET_KIND> Kinds => Options.Select(item => new ST_CORRECTION_OFFSET_KIND(item.Key, item.Name, item.Key == SelectedKind)).ToArray();
        public string Description => SelectedKind == "SUM" ? "Design SUM / Scanner Converted SUM · mm · Recipe + Review + Align + Head + First Beam" : $"{Options.Single(item => item.Key == SelectedKind).Name} Offset · Saved / Scanner Converted · mm";
        public IReadOnlyList<ST_CORRECTION_OFFSET_SHOT_MATRIX_ROW> ShotRows { get; private set; } = [];

        public void Update(IReadOnlyDictionary<string, string> parameters, IReadOnlyList<ST_HEAD_SHOT_POINT> shots)
        {
            _parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            _shots = shots;
            RefreshRows();
        }

        public void Clear()
        {
            _shots = [];
            RefreshRows();
        }

        private void SelectKind(object? value)
        {
            var key = value?.ToString()?.Trim().ToUpperInvariant();
            if (key == SelectedKind || !Options.Any(item => item.Key == key))
                return;
            SelectedKind = key!;
            RefreshRows();
        }

        private void RefreshRows()
        {
            ShotRows = _shots.GroupBy(shot => shot.GroupRow).OrderBy(group => group.Key).Select(group => new ST_CORRECTION_OFFSET_SHOT_MATRIX_ROW(group.Key + 1, group.OrderBy(shot => shot.GroupColumn).ThenBy(shot => shot.CellShotNo).Select(CreateCard).ToArray())).ToArray();
            PropertyChanged?.Invoke(this, new(null));
        }

        private ST_CORRECTION_OFFSET_SHOT_ITEM CreateCard(ST_HEAD_SHOT_POINT shot)
        {
            var name = CReviewShotNameFormatter.ToMatrixName(shot.CellShotNo, shot.GroupCountX);
            (double X, double Y) saved, converted;
            var title = "Saved · Design (mm)";
            var detail = CScannerAxisTransform.Format(CScannerAxisTransform.Read(_parameters, shot.HeadNo));
            switch (SelectedKind)
            {
                case "RECIPE":
                    // Recipe per-Shot offsets are already scanner coordinates in the process builder.
                    saved = converted = (shot.RecipeOffsetX, shot.RecipeOffsetY);
                    title = "Saved · Scanner (mm)";
                    break;
                case "REVIEW":
                    converted = (shot.ReviewOffsetX, shot.ReviewOffsetY);
                    var design = CReviewOffsetData.IsDesignCoordinate(_parameters, $"CELL{shot.CellNo}_{name}_REVIEW_OFFSET_");
                    saved = design ? CScannerAxisTransform.ScannerOffsetToDesign(_parameters, shot.HeadNo, converted.X, converted.Y) : converted;
                    title = design ? title : "Saved · Scanner (mm)";
                    break;
                case "ALIGN":
                    saved = (shot.AlignOffsetX, shot.AlignOffsetY);
                    converted = (shot.AlignScannerOffsetX, shot.AlignScannerOffsetY);
                    break;
                case "HEAD":
                    saved = (shot.HeadDefaultOffsetX, shot.HeadDefaultOffsetY);
                    // These two recipe components use the builder's fixed recipe-Y -> stage-Y mapping.
                    converted = CScannerAxisTransform.Read(_parameters, shot.HeadNo).Apply(saved.X, -saved.Y);
                    break;
                case "FIRST_BEAM":
                    saved = (shot.FirstBeamOffsetX, shot.FirstBeamOffsetY);
                    converted = CScannerAxisTransform.Read(_parameters, shot.HeadNo).Apply(saved.X, -saved.Y);
                    break;
                default:
                    converted = (shot.FinalScannerOffsetX, shot.FinalScannerOffsetY);
                    saved = CScannerAxisTransform.ScannerOffsetToDesign(_parameters, shot.HeadNo, converted.X, converted.Y);
                    title = "Design SUM (mm)";
                    detail += "\nSUM은 별도 저장값이 없습니다. 구성 항목을 같은 설계 좌표로 환산한 합계와 기존 실제 적용 합계를 표시합니다.";
                    break;
            }

            return new(shot.ShotKey, name, $"H{shot.HeadNo:00}", title, Pair(saved), Pair(converted), detail);
        }

        private static string Pair((double X, double Y) value) => $"X {Number(value.X)}   Y {Number(value.Y)}";
        private static string Number(double value)
        {
            var rounded = CAlignShotOffsetData.RoundValue(value);
            return (rounded == 0 ? 0 : rounded).ToString("F3", CultureInfo.InvariantCulture);
        }
    }

    public sealed record ST_CORRECTION_OFFSET_KIND(string Key, string Name, bool IsSelected);
    /// <summary>ROUGH screen state. Receives sequence snapshots; never polls hardware or reads files.</summary>
    public sealed class CRoughCorrectionPanel(CSimpleCorrectionPanel owner) : INotifyPropertyChanged
    {
        private ST_RECIPE_DEFINITION? _recipe;
        private IReadOnlyDictionary<string, string> _parameters = new Dictionary<string, string>();
        private string _targetSignature = "";
        private int _sourceVersion;
        private int _selectedBeam = 1;
        private ST_SIMPLE_CORRECTION_FIRST_SHOT[] _references = [];
        private ST_SIMPLE_CORRECTION_SHOT[] _assigned = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? SourceInvalidated;
        public Func<(string StageY, string ReviewX, string Mode)>? ReadFeedback { get; set; }
        public IReadOnlyList<CRoughCorrectionRow> Rows { get; private set; } = [];
        public IReadOnlyList<int> BeamNumbers { get; private set; } = [];
        public bool IsRunning { get; private set; }
        public int SourceVersion => _sourceVersion;
        public string StageY { get; private set; } = "-";
        public string ReviewX { get; private set; } = "-";
        public string Mode { get; private set; } = "OFFLINE";
        public string Status { get; private set; } = "검사 대기";

        public int SelectedBeam
        {
            get => _selectedBeam;
            set
            {
                if (IsRunning || owner.IsOffsetOperationRunning || !BeamNumbers.Contains(value) || value == _selectedBeam)
                    return;
                _selectedBeam = value;
                SyncTargets(_references, _assigned, _parameters);
                owner.RefreshInspectionDisplay();
            }
        }

        public bool CanConfigure => !IsRunning && !owner.IsOffsetOperationRunning;
        public string Summary => $"{Rows.Count} Shots / OK {Rows.Count(row => row.Point.State == EN_REVIEW_POINT_STATE.Ok)} / NG {Rows.Count(row => row.Point.State == EN_REVIEW_POINT_STATE.Ng)}";

        public bool CanExecute(string action) => action switch
        {
            "REVIEW_STOP" => IsRunning,
            "REVIEW_START" => CanConfigure && Rows.Count > 0,
            "NG_RETRY" => CanConfigure && Rows.Any(row => row.Point.State == EN_REVIEW_POINT_STATE.Ng),
            "OFFSET_APPLY" => CanConfigure && Rows.Any(row => row.Point.State == EN_REVIEW_POINT_STATE.Ok && !row.Applied),
            "OFFSET_INIT" => CanConfigure && _assigned.Length > 0 && Rows.Count > 0,
            "OFFSET_VIEW" => CanConfigure && _recipe is not null,
            _ => false
        };
        public void UpdateSource(ST_RECIPE_DEFINITION recipe, IReadOnlyDictionary<string, string> parameters)
        {
            var source = SourceMap(parameters);
            var previous = SourceMap(_parameters);
            if (_recipe?.Id != recipe.Id || previous.Count != source.Count || previous.Any(item => source.GetValueOrDefault(item.Key) != item.Value))
            {
                _sourceVersion++;
                SourceInvalidated?.Invoke();
                _targetSignature = "";
                Rows = [];
            }

            _recipe = recipe;
            _parameters = parameters;
            var split = int.TryParse(parameters.GetValueOrDefault("SPLITED_BEAM_COUNT"), out var count) ? count : 0;
            BeamNumbers = Enumerable.Range(1, Math.Max(0, split * split)).ToArray();
            if (!BeamNumbers.Contains(_selectedBeam))
                _selectedBeam = BeamNumbers.FirstOrDefault();
            RefreshFeedback();
        }

        public void Clear()
        {
            _sourceVersion++;
            SourceInvalidated?.Invoke();
            _recipe = null;
            _targetSignature = "";
            _references = [];
            _assigned = [];
            Rows = [];
            BeamNumbers = [];
            Status = "현재 레시피를 선택해 주세요.";
            Notify();
        }

        public void SyncTargets(IReadOnlyList<ST_SIMPLE_CORRECTION_FIRST_SHOT> references, IReadOnlyList<ST_SIMPLE_CORRECTION_SHOT> assigned, IReadOnlyDictionary<string, string> parameters)
        {
            _references = references.ToArray();
            _assigned = assigned.ToArray();
            _parameters = parameters;
            var signature = $"{_sourceVersion}/{owner.SelectedHeadsText}/{owner.IsHeadScope}/{_selectedBeam}/" + string.Join("|", references.Select(row => row.ShotKey));
            if (_targetSignature == signature)
            {
                foreach (var row in Rows)
                    row.SetCurrent(ReadDesignOffset(row.Point.HeadNo, row.Point.CellNo, row.ShotName));
                return;
            }

            _targetSignature = signature;
            if (_recipe is null || _selectedBeam < 1)
            {
                Rows = [];
                Notify();
                return;
            }

            var byKey = assigned.ToDictionary(shot => shot.Key, StringComparer.OrdinalIgnoreCase);
            var points = new List<ST_REVIEW_PLAN_POINT>();
            var direction = CScannerAxisTransform.ReadStageDirection(parameters);
            foreach (var reference in references.Where(row => row.ShotKey.Length > 0))
            {
                var shot = byKey[reference.ShotKey];
                var point = new ST_REVIEW_PLAN_POINT(points.Count + 1, shot.Key, shot.HeadNo, shot.CellNo, shot.CellShotNo, shot.GroupCountX, shot.GroupCountY, true, shot.DesignX, shot.DesignY, 0, 0, 0, 0, EN_REVIEW_POINT_STATE.Ready, "WAIT")
                {
                    BeamNo = _selectedBeam
                };
                var position = CReviewManager.GetShotBeamPositions(_recipe, point, direction).Single(beam => beam.BeamNo == _selectedBeam);
                points.Add(point with { ReviewTargetX = position.ReviewX, ReviewTargetY = position.StageY });
            }

            Rows = CReviewManager.OrderRoughTargets(points).Select((point, index) => new CRoughCorrectionRow(point with { PointNo = index + 1 }, byKey[point.ShotKey].Name, DeltaToDesign)).ToArray();
            foreach (var row in Rows)
                row.SetCurrent(ReadDesignOffset(row.Point.HeadNo, row.Point.CellNo, row.ShotName));
            Status = "검사 대기";
            Notify();
        }

        public ST_REVIEW_PLAN CreatePlan()
        {
            if (_recipe is null || Rows.Count == 0)
                throw new InvalidOperationException("검사 대상 Shot이 없습니다.");
            var vision = CReviewManager.ParseVisionFlipType(_parameters.GetValueOrDefault("VisionXFlip"), _parameters.GetValueOrDefault("VisionYFlip"), _parameters.GetValueOrDefault("VisionXyFlip"));
            return new(
                _recipe.Id, 
                Rows.Select(row => row.Point.CellNo).Distinct().Count(), 
                ReadTolerance("REVIEW_TOLERANCE_X"), 
                ReadTolerance("REVIEW_TOLERANCE_Y"), 
                vision, 
                DateTimeOffset.Now, 
                Rows.Select(row => row.Point).ToArray()
            )
            {
                CorrectionPlan = new(EN_REVIEW_CORRECTION_RULE.Rough, owner.IsHeadScope ? EN_REVIEW_CORRECTION_SCOPE.Head : EN_REVIEW_CORRECTION_SCOPE.HeadCell, Rows.Select(row => row.Point).ToArray()),
                StageSpeed = double.TryParse(_parameters.GetValueOrDefault("STAGE_SPEED"), NumberStyles.Float, CultureInfo.InvariantCulture, out var stageSpeed) ? stageSpeed : null
            };
        }

        public void Begin(bool retry)
        {
            IsRunning = true;
            if (!retry)
                foreach (var row in Rows)
                {
                    row.Applied = false;
                    row.Update(row.Point with { State = EN_REVIEW_POINT_STATE.Ready, Judge = "WAIT", ErrorX = 0, ErrorY = 0 });
                }

            Notify();
        }

        public void ApplyProgress(ST_ROUGH_REVIEW_SNAPSHOT snapshot, int version)
        {
            if (version != _sourceVersion || snapshot.RecipeId != _recipe?.Id)
                return;
            var points = snapshot.Points.ToDictionary(point => point.ShotKey, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Rows)
                if (points.TryGetValue(row.Point.ShotKey, out var point))
                    row.Update(point);
            Status = snapshot.Message;
            RefreshFeedback();
            Notify();
            owner.RefreshInspectionDisplay();
        }

        public void End()
        {
            IsRunning = false;
            Notify();
            owner.RefreshInspectionDisplay();
        }

        public void RefreshCommands()
        {
            Notify();
            owner.OffsetActionCommand.NotifyCanExecuteChanged();
        }

        public CRoughCorrectionRow? FindRow(string key) => Rows.FirstOrDefault(row => row.Point.ShotKey.Equals(key, StringComparison.OrdinalIgnoreCase));
        public void RefreshFeedback()
        {
            if (ReadFeedback is null)
                return;
            var feedback = ReadFeedback();
            owner.Camera.UpdateFeedback(feedback.StageY, feedback.ReviewX, feedback.Mode);
            if ((StageY, ReviewX, Mode) == feedback)
                return;
            StageY = feedback.StageY;
            ReviewX = feedback.ReviewX;
            Mode = feedback.Mode;
            Notify();
        }

        public ST_SIMPLE_OFFSET_CHANGE CreateOffsetRequest(bool initialize)
        {
            if (_recipe is null || Rows.Count == 0)
                throw new InvalidOperationException("Offset 대상이 없습니다.");
            var rows = _assigned.Select(shot => new ST_REVIEW_OFFSET_ROW(shot.CellNo, shot.Name, 0, 0) { IsDesignCoordinate = true }).ToArray();
            return new(_recipe.Id, owner.SelectedHeadNo, 0, 0, 0, rows, SourceMap(_parameters))
            {
                TargetHeads = _assigned.ToDictionary(shot => (shot.CellNo, shot.Name), shot => shot.HeadNo)
            };
        }

        public (double X, double Y) IncrementForCell(int cellNo) => IncrementForShot(owner.SelectedHeadNo, cellNo);
        public (double X, double Y) IncrementForShot(int headNo, int cellNo)
        {
            var row = Rows.FirstOrDefault(item => item.Point.HeadNo == headNo && (owner.IsHeadScope || item.Point.CellNo == cellNo));
            return row is { Applied: false, Point.State: EN_REVIEW_POINT_STATE.Ok } ? row.DesignDelta : (0, 0);
        }

        public void MarkApplied(bool initialize)
        {
            foreach (var row in Rows)
            {
                if (row.Point.State == EN_REVIEW_POINT_STATE.Ok)
                    row.Applied = true;
                if (initialize)
                {
                    row.Applied = false;
                    row.Update(row.Point with { State = EN_REVIEW_POINT_STATE.Ready, Judge = "WAIT", ErrorX = 0, ErrorY = 0 });
                }
            }

            Notify();
            owner.RefreshInspectionDisplay();
        }

        private (double X, double Y) ReadDesignOffset(int headNo, int cellNo, string shot)
        {
            var prefix = $"CELL{cellNo}_{shot}_REVIEW_OFFSET_";
            double Read(string axis) => double.TryParse(_parameters.GetValueOrDefault(prefix + axis, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
            return CReviewOffsetData.ToDesign(_parameters, headNo, Read("X"), Read("Y"), CReviewOffsetData.IsDesignCoordinate(_parameters, prefix));
        }

        private (double X, double Y) DeltaToDesign(ST_REVIEW_PLAN_POINT point)
        {
            var plan = CreatePlan();
            // Vision Flip defines the received camera error in design coordinates.
            // Stage direction and Head flips belong to the later script conversion.
            var design = CReviewManager.VisionErrorToStageOffset(point.ErrorX, point.ErrorY, plan.VisionFlipType);
            return (Round(design.X), Round(design.Y));
        }

        private double ReadTolerance(string key) => double.TryParse(_parameters.GetValueOrDefault(key, "0.03"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : throw new InvalidOperationException($"{key}를 확인해 주세요.");
        private static Dictionary<string, string> SourceMap(IReadOnlyDictionary<string, string> values) => values.Where(item => !CReviewOffsetData.IsParameterKey(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        internal static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
        private void Notify() => PropertyChanged?.Invoke(this, new(null));
    }

    public sealed class CRoughCorrectionRow(ST_REVIEW_PLAN_POINT point, string shotName, Func<ST_REVIEW_PLAN_POINT, (double X, double Y)> toDesign) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public ST_REVIEW_PLAN_POINT Point { get; private set; } = point;
        public string ShotName { get; } = shotName;
        public string Title => $"{Point.PointNo}. H{Point.HeadNo:00} / Cell{Point.CellNo:00}-{ShotName}";
        public string ReviewX => Point.ReviewTargetX.ToString("0.000", CultureInfo.InvariantCulture);
        public string StageY => Point.ReviewTargetY.ToString("0.000", CultureInfo.InvariantCulture);
        public string CurrentX { get; private set; } = "0.000";
        public string CurrentY { get; private set; } = "0.000";
        public bool Applied { get; set; }
        public (double X, double Y) DesignDelta => Point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? toDesign(Point) : (0, 0);
        public string DeltaX => Point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? CRoughCorrectionPanel.Round(Point.ErrorX).ToString("0.000", CultureInfo.InvariantCulture) : "-";
        public string DeltaY => Point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? CRoughCorrectionPanel.Round(Point.ErrorY).ToString("0.000", CultureInfo.InvariantCulture) : "-";
        public string ConvertedX => Point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? DesignDelta.X.ToString("0.000", CultureInfo.InvariantCulture) : "-";
        public string ConvertedY => Point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? DesignDelta.Y.ToString("0.000", CultureInfo.InvariantCulture) : "-";
        public string State => Point.State switch
        {
            EN_REVIEW_POINT_STATE.Current => "RUNNING",
            EN_REVIEW_POINT_STATE.Ok => "OK",
            EN_REVIEW_POINT_STATE.Ng => "NG",
            _ => Point.Judge == "ERROR" ? "ERROR" : "WAIT"
        };
        public Brush StateBrush => State switch
        {
            "OK" or "RUNNING" => Brushes.SeaGreen,
            "NG" or "ERROR" => Brushes.Firebrick,
            _ => Brushes.DarkGoldenrod
        };

        public void Update(ST_REVIEW_PLAN_POINT point)
        {
            Point = point;
            Notify();
        }

        public void SetCurrent((double X, double Y) value)
        {
            var x = value.X.ToString("0.000", CultureInfo.InvariantCulture);
            var y = value.Y.ToString("0.000", CultureInfo.InvariantCulture);
            if (CurrentX == x && CurrentY == y)
                return;
            CurrentX = x;
            CurrentY = y;
            Notify();
        }

        private void Notify() => PropertyChanged?.Invoke(this, new(null));
    }

    /// <summary>SIMPLE correction selection, design-coordinate drafts, and recipe preview. Does not control motion.</summary>
    public sealed class CSimpleCorrectionPanel : INotifyPropertyChanged
    {
        private IReadOnlyDictionary<string, string> _parameters = new Dictionary<string, string>();
        private IReadOnlyDictionary<int, ST_SIMPLE_CORRECTION_SHOT[]> _cells = new Dictionary<int, ST_SIMPLE_CORRECTION_SHOT[]>();
        private IReadOnlySet<int> _enabledHeads = new HashSet<int>();
        private IReadOnlyDictionary<string, string> _assignment = new Dictionary<string, string>();
        private ST_PREVIEW_HEAD_LAYOUT _headLayout = CHeadAssignmentPreviewBuilder.BuildHeadLayout([]);
        private int _selectedHead;
        private readonly HashSet<int> _selectedHeads = [];
        private readonly Dictionary<(string Scope, int Head, int Cell), string> _representatives = [];
        private int _selectedCell;
        private string _selectedScope = "HEAD";
        private string _selectedCorrectionShotKey = "";
        private IReadOnlySet<string> _referenceShotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CSimpleCorrectionInput> _inputDrafts = new(StringComparer.OrdinalIgnoreCase);
        private double _viewportWidth = 780;
        private double _viewportHeight = 565;
        private readonly Func<string, Task>? _executeOffsetAction;
        public bool IsOffsetOperationRunning { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        public CSimpleCorrectionPanel(Func<string, Task>? executeOffsetAction = null, bool isRough = false)
        {
            if (isRough)
                Inspection = new CRoughCorrectionPanel(this);
            _executeOffsetAction = executeOffsetAction;
            SelectCellCommand = new(SelectCell, _ => !IsOffsetOperationRunning);
            SelectHeadCommand = new(SelectHead, _ => !IsOffsetOperationRunning);
            SelectScopeCommand = new(SelectScope, _ => !IsOffsetOperationRunning);
            SelectCorrectionShotCommand = new(SelectCorrectionShot, _ => !IsOffsetOperationRunning);
            OffsetActionCommand = new(RunOffsetAction, action => _executeOffsetAction is not null && (Inspection is not null ? Inspection.CanExecute(action?.ToString() ?? "") : !IsOffsetOperationRunning && (action?.ToString() == "OFFSET_VIEW" ? RecipeId.Length > 0 : action?.ToString()is "OFFSET_APPLY" or "OFFSET_INIT" && CorrectionRows.Count == 1)));
            BackToGlassCommand = new(_ => ShowGlass());
            UpdateHeads();
        }

        public CButtonCommand SelectCellCommand { get; }
        public CButtonCommand SelectHeadCommand { get; }
        public int SelectedHeadNo => _selectedHead;
        public IReadOnlyList<int> SelectedHeadNos => _selectedHeads.Order().ToArray();
        public string SelectedHeadsText => string.Join(", ", SelectedHeadNos.Select(head => $"H{head:00}"));
        public CButtonCommand SelectScopeCommand { get; }
        public CButtonCommand SelectCorrectionShotCommand { get; }
        public CButtonCommand OffsetActionCommand { get; }
        public CRoughCorrectionPanel? Inspection { get; }
        public CReviewCameraPreview Camera { get; } = new();

        public void RefreshFeedback() => Inspection?.RefreshFeedback();
        public bool IsRough => Inspection is not null;
        public bool IsSimple => !IsRough;
        public string HeadSelectionTitle => IsRough ? "HEAD SELECT · 복수 선택" : "HEAD SELECT";
        public bool IsHeadScope => _selectedScope == "HEAD";
        public bool IsHeadCellScope => _selectedScope == "HEAD_CELL";
        public IReadOnlyList<ST_SIMPLE_CORRECTION_FIRST_SHOT> FirstShotRows { get; private set; } = [];
        public IReadOnlyList<ST_SIMPLE_CORRECTION_CALLOUT> FirstShotCallouts { get; private set; } = [];
        public IReadOnlyList<CSimpleCorrectionInput> CorrectionRows { get; private set; } = [];
        public Visibility CorrectionEmptyVisibility => CorrectionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public string CorrectionEmptyText => _selectedHead == 0 ? "Head를 선택해 주세요." : _referenceShotKeys.Count == 0 ? "실제로 가공하는 기준 Shot이 없습니다." : "프리뷰의 Shot 박스를 선택해 주세요.";
        public CButtonCommand BackToGlassCommand { get; }
        public string RecipeName { get; private set; } = "-";
        public string RecipeId { get; private set; } = "";
        public string ErrorText { get; private set; } = "";
        public ImageSource? PreviewImage { get; private set; }
        public ST_GLASS_PREVIEW_FRAME GlassFrame { get; private set; } = new(44, 62, 772, 420);
        public double PreviewContentWidth { get; private set; } = 780;
        public double PreviewContentHeight { get; private set; } = 565;
        public Transform PreviewTransform { get; private set; } = Transform.Identity;

        public void ResizePreview(double width, double height)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || Math.Abs(width - _viewportWidth) < 0.5 && Math.Abs(height - _viewportHeight) < 0.5)
                return;
            _viewportWidth = width;
            _viewportHeight = height;
            RefreshCallouts();
            Notify();
        }

        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellLabels { get; private set; } = [];
        public IReadOnlyList<ST_SIMPLE_CORRECTION_HEAD> HeadSelections { get; private set; } = [];
        public IReadOnlyList<ST_SIMPLE_CORRECTION_SHOT_ROW> ShotRows { get; private set; } = [];
        public Visibility GlassVisibility => _selectedCell == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CellVisibility => _selectedCell > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string PreviewTitle => _selectedCell > 0 ? $"{(IsRough ? SelectedHeadsText : $"HEAD {_selectedHead:00}")} / CELL {_selectedCell:00} / SHOT" : "GLASS PREVIEW";
        public Visibility SelectHeadHintVisibility => _selectedHead == 0 ? Visibility.Visible : Visibility.Collapsed;
        public string Summary { get; private set; } = "현재 레시피를 선택해 주세요.";
        public string CellSummary { get; private set; } = "";

        public void Update(ST_RECIPE_DEFINITION? recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, ST_REVIEW_OFFSET_FILE_DATA? currentOffsets = null)
        {
            if (recipe is null)
            {
                Clear("현재 레시피를 찾을 수 없습니다.");
                return;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in recipe.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                parameters[item.Key] = item.Value;
            foreach (var item in settings.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                parameters[item.Key] = item.Value;
            if (currentOffsets is not null)
            {
                if (!string.Equals(currentOffsets.RecipeId, recipe.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Current Review Offset recipe does not match the preview recipe.");
                CMenuCorrection.ApplyReviewOffsetsToOutputViewerParameters(parameters, currentOffsets);
            }

            if (RecipeId == recipe.Id && RecipeName == recipe.Name && ErrorText.Length == 0 && _parameters.Count == parameters.Count && parameters.All(item => _parameters.GetValueOrDefault(item.Key) == item.Value))
                return;
            var assignment = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
            {
                ["HEAD_COUNT"] = "8"
            };
            _enabledHeads = CHeadAssignmentPreviewBuilder.BuildEnabledHeadNumbers(parameters).ToHashSet();
            // Keep physical assignment visible for all eight Heads, including NOT USE Heads.
            for (var head = 1; head <= 8; head++)
                assignment[$"H{head:00}_USE"] = "ON";
            var plan = CShotCoordinatePlanBuilder.Build(assignment);
            var masking = CRecipeShotMaskingPlan.Build(assignment);
            var assigned = plan.Shots.ToDictionary(shot => shot.ShotKey, StringComparer.OrdinalIgnoreCase);
            var cells = CShotCoordinatePlanBuilder.BuildDesignShots(assignment).GroupBy(shot => shot.CellNo).ToDictionary(group => group.Key, group => group.Select(shot =>
            {
                var point = assigned.GetValueOrDefault(shot.ShotKey) ?? CreateUnassignedPoint(shot);
                var mask = masking.EvaluateShot(point);
                var reasons = new List<string>();
                if (mask.IsChessSkipped)
                    reasons.Add("CHESS");
                if (mask.IsMasked)
                    reasons.Add("MASKING");
                if (point.HeadNo == 0)
                    reasons.Add("UNASSIGNED");
                else if (!_enabledHeads.Contains(point.HeadNo))
                    reasons.Add("HEAD OFF");
                return new ST_SIMPLE_CORRECTION_SHOT(
                    shot.ShotKey, 
                    CReviewShotNameFormatter.ToMatrixName(shot.CellShotNo, shot.GroupCountX), 
                    point.HeadNo, 
                    shot.GroupColumn, 
                    shot.GroupRow, 
                    reasons.Count == 0, 
                    reasons.Count == 0 ? "PROCESS" : string.Join(" / ", reasons), 
                    mask.IsChessSkipped, 
                    mask.IsMasked
                )
                {
                    CellNo = shot.CellNo,
                    SequenceNo = point.SequenceNo,
                    DesignX = shot.DesignX,
                    DesignY = shot.DesignY,
                    CellShotNo = shot.CellShotNo,
                    GroupCountX = shot.GroupCountX,
                    GroupCountY = shot.GroupCountY
                };
            }).OrderBy(shot => shot.Row).ThenBy(shot => shot.Column).ToArray());
            if (RecipeId != recipe.Id)
            {
                _selectedHead = 0;
                _selectedHeads.Clear();
                _representatives.Clear();
                _selectedCell = 0;
                _selectedCorrectionShotKey = "";
                _inputDrafts.Clear();
            }

            if (!cells.ContainsKey(_selectedCell))
                _selectedCell = 0;
            RecipeId = recipe.Id;
            RecipeName = recipe.Name;
            _parameters = parameters;
            _cells = cells;
            Inspection?.UpdateSource(recipe, parameters);
            _assignment = assignment;
            _headLayout = CHeadAssignmentPreviewBuilder.BuildHeadLayout(settings);
            ErrorText = "";
            RefreshHeadPreview();
            UpdateCell();
            Notify();
        }

        public void Clear(string message)
        {
            _parameters = new Dictionary<string, string>();
            _inputDrafts.Clear();
            _selectedCorrectionShotKey = "";
            _cells = new Dictionary<int, ST_SIMPLE_CORRECTION_SHOT[]>();
            _enabledHeads = new HashSet<int>();
            _selectedCell = 0;
            _selectedHead = 0;
            _selectedHeads.Clear();
            _representatives.Clear();
            _assignment = new Dictionary<string, string>();
            RecipeId = "";
            RecipeName = "-";
            PreviewImage = null;
            CellLabels = [];
            ShotRows = [];
            ErrorText = message;
            Summary = message;
            CellSummary = "";
            Inspection?.Clear();
            Camera.Clear();
            RefreshReferenceShots();
            UpdateHeads();
            Notify();
        }

        private void SelectHead(object? value)
        {
            if (!int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var head) || head is < 1 or > 8 || !IsRough && head == _selectedHead)
                return;
            if (!IsRough)
            {
                _selectedHeads.Clear();
                _selectedHeads.Add(head);
            }
            else if (!_selectedHeads.Add(head))
                _selectedHeads.Remove(head);
            _selectedHead = _selectedHeads.Contains(head) ? head : _selectedHeads.Order().FirstOrDefault();
            _selectedCell = 0;
            _selectedCorrectionShotKey = "";
            if (_assignment.Count > 0)
                RefreshHeadPreview();
            UpdateCell();
            Notify();
        }

        private void SelectScope(object? value)
        {
            var scope = value?.ToString()?.Trim().ToUpperInvariant();
            if (scope is not ("HEAD" or "HEAD_CELL") || scope == _selectedScope)
                return;
            _selectedScope = scope;
            _selectedCorrectionShotKey = "";
            UpdateCell();
            Notify();
        }

        private void SelectCorrectionShot(object? value)
        {
            var key = value?.ToString() ?? "";
            if (IsSimple && !_referenceShotKeys.Contains(key))
                return;
            var shot = _cells.Values.SelectMany(rows => rows).FirstOrDefault(shot => shot.Key == key);
            if (shot is null || !shot.CanProcess || !_selectedHeads.Contains(shot.HeadNo))
                return;
            if (IsRough && !_referenceShotKeys.Contains(key))
                _representatives[(_selectedScope, shot.HeadNo, IsHeadScope ? 0 : shot.CellNo)] = key;
            _selectedCorrectionShotKey = key;
            UpdateCell();
            Notify();
        }

        private async void RunOffsetAction(object? value)
        {
            if (!OffsetActionCommand.CanExecute(value))
                return;
            await _executeOffsetAction!(value?.ToString() ?? "");
        }

        public void SetOffsetOperationRunning(bool running)
        {
            IsOffsetOperationRunning = running;
            SelectCellCommand.NotifyCanExecuteChanged();
            SelectHeadCommand.NotifyCanExecuteChanged();
            SelectScopeCommand.NotifyCanExecuteChanged();
            SelectCorrectionShotCommand.NotifyCanExecuteChanged();
            Notify();
        }

        public ST_SIMPLE_OFFSET_CHANGE CreateOffsetChange(bool initialize)
        {
            var input = CorrectionRows.SingleOrDefault() ?? throw new InvalidOperationException("프리뷰의 Shot 박스를 선택해 주세요.");
            var x = initialize ? 0 : double.Parse(input.InputX, CultureInfo.InvariantCulture);
            var y = initialize ? 0 : double.Parse(input.InputY, CultureInfo.InvariantCulture);
            var rows = _cells.Values.SelectMany(shots => shots).Where(shot => shot.HeadNo == _selectedHead && (IsHeadScope || shot.CellNo == input.CellNo)).Select(shot => new ST_REVIEW_OFFSET_ROW(shot.CellNo, shot.Name, x, y) { IsDesignCoordinate = true }).ToArray();
            if (rows.Length == 0)
                throw new InvalidOperationException("적용할 Shot이 없습니다.");
            return new(
                RecipeId, 
                _selectedHead, 
                IsHeadScope ? 0 : input.CellNo, 
                x, 
                y, 
                rows, 
                _parameters.Where(item => !CReviewOffsetData.IsParameterKey(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
            );
        }

        public void AcceptSavedOffsets(ST_REVIEW_OFFSET_FILE_DATA saved, IReadOnlyList<ST_REVIEW_OFFSET_ROW> changed, ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            if (!RecipeId.Equals(saved.RecipeId, StringComparison.OrdinalIgnoreCase))
                return;
            var affected = changed.Select(row => (row.CellId, row.ShotId.ToUpperInvariant())).ToHashSet();
            foreach (var key in _inputDrafts.Where(item => affected.Contains((item.Value.CellNo, item.Value.ShotName.ToUpperInvariant()))).Select(item => item.Key).ToArray())
                _inputDrafts.Remove(key);
            // Offset changes may affect scanner ordering, so rebuild the actual first-Shot references as well.
            Update(recipe, settings, saved);
            RefreshCorrectionRows();
            Notify();
        }

        private void RefreshReferenceShots()
        {
            if (_selectedHead == 0)
            {
                FirstShotRows = [];
                _referenceShotKeys = new HashSet<string>();
                _selectedCorrectionShotKey = "";
                CorrectionRows = [];
                Inspection?.SyncTargets([], [], _parameters);
                RefreshCallouts();
                return;
            }

            // SequenceNo is assigned by the common machining plan, including Stage direction and scanner-axis ordering.
            // Do not infer the first processing Shot from Cell number or the displayed row/column order.
            var headShots = _cells.Values.SelectMany(shots => shots).Where(shot => _selectedHeads.Contains(shot.HeadNo)).ToArray();
            ST_SIMPLE_CORRECTION_FIRST_SHOT First(int head, int cellNo, IEnumerable<ST_SIMPLE_CORRECTION_SHOT> shots)
            {
                var candidates = shots.Where(shot => shot.CanProcess).ToArray();
                var chosen = _representatives.GetValueOrDefault((_selectedScope, head, cellNo));
                var first = candidates.FirstOrDefault(shot => shot.Key == chosen) ?? candidates.MinBy(shot => shot.SequenceNo);
                return new(first?.CellNo ?? cellNo, first?.Key ?? "", first?.Name ?? "")
                {
                    HeadNo = head
                };
            }

            FirstShotRows = headShots.GroupBy(shot => shot.HeadNo).OrderBy(group => group.Key).SelectMany(head => IsHeadScope ? new[] { First(head.Key, 0, head) } : head.GroupBy(shot => shot.CellNo).OrderBy(cell => cell.Key).Select(cell => First(head.Key, cell.Key, cell))).ToArray();
            _referenceShotKeys = FirstShotRows.Where(row => row.ShotKey.Length > 0).Select(row => row.ShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Inspection?.SyncTargets(FirstShotRows, headShots, _parameters);
            RefreshCorrectionRows();
            RefreshCallouts();
        }

        private void RefreshCorrectionRows()
        {
            if (IsRough)
            {
                CorrectionRows = [];
                return;
            }

            if (!_referenceShotKeys.Contains(_selectedCorrectionShotKey))
                _selectedCorrectionShotKey = "";
            CorrectionRows = FirstShotRows.Where(row => row.ShotKey.Length > 0 && string.Equals(row.ShotKey, _selectedCorrectionShotKey, StringComparison.OrdinalIgnoreCase)).Select(row =>
            {
                var key = $"{_selectedScope}/{_selectedHead}/{row.ShotKey}";
                if (!_inputDrafts.TryGetValue(key, out var input))
                {
                    input = new CSimpleCorrectionInput(_selectedHead, row.CellNo, row.ShotKey, row.ShotName);
                    _inputDrafts.Add(key, input);
                }

                double Current(string axis)
                {
                    var raw = _parameters.GetValueOrDefault($"CELL{row.CellNo}_{row.ShotName}_REVIEW_OFFSET_{axis}", _parameters.GetValueOrDefault($"REVIEW_OFFSET_{axis}", "0"));
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                        throw new InvalidOperationException($"Cell{row.CellNo:00}-{row.ShotName}의 현재 {axis} 보정값이 올바르지 않습니다.");
                    return value;
                }

                var design = CReviewOffsetData.ToDesign(_parameters, _selectedHead, Current("X"), Current("Y"), CReviewOffsetData.IsDesignCoordinate(_parameters, $"CELL{row.CellNo}_{row.ShotName}_REVIEW_OFFSET_"));
                input.UpdateCurrent(design.X, design.Y);
                return input;
            }).ToArray();
        }

        private void RefreshCallouts()
        {
            var labels = CellLabels.ToDictionary(label => label.CellNo);
            var references = FirstShotRows.Where(row => row.ShotKey.Length > 0 && labels.ContainsKey(row.CellNo)).OrderBy(row => labels[row.CellNo].CanvasCenterX).ThenBy(row => labels[row.CellNo].CanvasCenterY).ToArray();
            // Balance both sides even when all selected Cells lie in the same half of the Glass.
            var leftCount = references.Length == 1 ? (labels[references[0].CellNo].CanvasCenterX <= GlassFrame.CanvasLeft + GlassFrame.Width / 2 ? 1 : 0) : (references.Length + 1) / 2;
            var left = references.Take(leftCount).ToArray();
            var right = references.Skip(leftCount).ToArray();
            const double labelHeight = 28, rowSpacing = 34, columnGap = 10, edgeGap = 22, margin = 12;
            var labelWidth = Math.Clamp(references.Select(row => $"Cell{row.CellNo:00}-{row.ShotName}".Length * 7.5 + 18).DefaultIfEmpty(88).Max(), 88, 132);
            var sideCount = (left.Length > 0 ? 1 : 0) + (right.Length > 0 ? 1 : 0);
            var rowCapacity = Math.Max(1, (int)Math.Floor((_viewportHeight - 2 * margin) / rowSpacing));
            var maxColumns = sideCount == 0 ? 1 : Math.Max(1, (int)Math.Floor(((_viewportWidth - 120) / sideCount - edgeGap - margin + columnGap) / (labelWidth + columnGap)));
            int Columns(int count) => count == 0 ? 0 : Math.Min(maxColumns, (count + rowCapacity - 1) / rowCapacity);
            var leftColumns = Columns(left.Length);
            var rightColumns = Columns(right.Length);
            int Rows(int count, int columns) => columns == 0 ? 0 : (count + columns - 1) / columns;
            double Gutter(int columns) => margin + (columns == 0 ? 0 : columns * labelWidth + (columns - 1) * columnGap + edgeGap);
            var leftGutter = Gutter(leftColumns);
            var rightGutter = Gutter(rightColumns);
            // A very short/narrow viewport scrolls instead of compressing every label and the Glass into unreadable text.
            PreviewContentWidth = Math.Max(_viewportWidth, leftGutter + rightGutter + 120);
            PreviewContentHeight = Math.Max(_viewportHeight, Math.Max(Rows(left.Length, leftColumns), Rows(right.Length, rightColumns)) * rowSpacing + 2 * margin);
            var availableWidth = PreviewContentWidth - leftGutter - rightGutter;
            var scale = Math.Min(availableWidth / GlassFrame.Width, (PreviewContentHeight - 2 * margin) / GlassFrame.Height);
            var glassLeft = leftGutter + (availableWidth - GlassFrame.Width * scale) / 2;
            var glassTop = (PreviewContentHeight - GlassFrame.Height * scale) / 2;
            var transform = new MatrixTransform(scale, 0, 0, scale, glassLeft - GlassFrame.CanvasLeft * scale, glassTop - GlassFrame.CanvasTop * scale);
            transform.Freeze();
            PreviewTransform = transform;
            var frame = transform.TransformBounds(new Rect(GlassFrame.CanvasLeft, GlassFrame.CanvasTop, GlassFrame.Width, GlassFrame.Height));
            if (IsRough)
                Camera.Configure(_parameters, frame);
            var result = new List<ST_SIMPLE_CORRECTION_CALLOUT>();
            AddSide(left, true, leftColumns);
            AddSide(right, false, rightColumns);
            FirstShotCallouts = result;
            void AddSide(ST_SIMPLE_CORRECTION_FIRST_SHOT[] side, bool isLeft, int columns)
            {
                if (columns == 0)
                    return;
                var ordered = side.OrderBy(row => labels[row.CellNo].CanvasCenterY).ThenBy(row => labels[row.CellNo].CanvasCenterX).ThenBy(row => row.CellNo).ToArray();
                var rows = Rows(ordered.Length, columns);
                var centers = Enumerable.Range(0, rows).Select(row => ordered.Skip(row * columns).Take(columns).Average(item => transform.Transform(new Point(labels[item.CellNo].CanvasCenterX, labels[item.CellNo].CanvasCenterY)).Y)).ToArray();
                var top = margin + labelHeight / 2;
                var bottom = PreviewContentHeight - margin - labelHeight / 2;
                for (var row = 0; row < rows; row++)
                    centers[row] = Math.Max(Math.Clamp(centers[row], top, bottom), row == 0 ? top : centers[row - 1] + rowSpacing);
                for (var row = rows - 1; row >= 0; row--)
                    centers[row] = Math.Min(centers[row], row == rows - 1 ? bottom : centers[row + 1] - rowSpacing);
                for (var index = 0; index < ordered.Length; index++)
                {
                    var item = ordered[index];
                    var cell = labels[item.CellNo];
                    var column = index % columns;
                    var center = centers[index / columns];
                    var point = transform.Transform(new Point(cell.CanvasCenterX, cell.CanvasCenterY));
                    var bounds = cell.CellBounds.IsEmpty ? new Rect(point.X - 3, point.Y - 3, 6, 6) : transform.TransformBounds(cell.CellBounds);
                    bounds.Inflate(2, 2);
                    var labelLeft = isLeft ? frame.Left - edgeGap - labelWidth - column * (labelWidth + columnGap) : frame.Right + edgeGap + column * (labelWidth + columnGap);
                    var labelEdge = isLeft ? labelLeft + labelWidth : labelLeft;
                    var elbow = isLeft ? frame.Left - 8 - column * 3 : frame.Right + 8 + column * 3;
                    var approach = labelEdge + (isLeft ? columnGap / 2 : -columnGap / 2);
                    // Outer-column leaders pass through the gap above the row, rather than through the inner label boxes.
                    var busY = center - labelHeight / 2 - 3;
                    var line = new PointCollection
                    {
                        new(isLeft ? bounds.Left : bounds.Right, point.Y),
                        new(elbow, point.Y),
                        new(elbow, busY),
                        new(approach, busY),
                        new(approach, center),
                        new(labelEdge, center)
                    };
                    line.Freeze();
                    result.Add(new(item.CellNo, item.ShotKey, item.ShotName, bounds, line, labelLeft, center - labelHeight / 2, labelWidth, labelHeight) { IsSelected = !IsRough && string.Equals(item.ShotKey, _selectedCorrectionShotKey, StringComparison.OrdinalIgnoreCase), InspectionState = Inspection?.FindRow(item.ShotKey)?.State ?? "", InspectionBrush = Inspection?.FindRow(item.ShotKey)?.StateBrush });
                }
            }
        }

        private void RefreshHeadPreview()
        {
            var visibleCells = _cells.Where(cell => cell.Value.Any(shot => _selectedHeads.Contains(shot.HeadNo))).Select(cell => cell.Key).ToHashSet();
            if (!visibleCells.Contains(_selectedCell))
                _selectedCell = 0;
            var preview = CHeadAssignmentPreviewBuilder.BuildRecipePreview(_assignment, new HashSet<int>(), _headLayout, showKeys: false, visibleCellNos: visibleCells);
            if (!string.IsNullOrEmpty(preview.ValidationMessage))
                throw new InvalidOperationException(preview.ValidationMessage);
            PreviewImage = preview.Image;
            GlassFrame = preview.Frame;
            CellLabels = preview.CellLabels.Select(label => label with { Width = Math.Min(label.Width, 24) }).ToArray();
            Summary = _selectedHead == 0 ? "Head를 선택해 주세요." : $"{(IsRough ? SelectedHeadsText : $"HEAD {_selectedHead:00}")} / {visibleCells.Count:N0} Cells / " + $"{_cells.Values.SelectMany(shots => shots).Count(shot => _selectedHeads.Contains(shot.HeadNo) && shot.CanProcess):N0} Process Shots" + (IsRough ? " · Cell → Shot 클릭: 대표점 변경" : "");
        }

        private void SelectCell(object? value)
        {
            if (!int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cell) || _selectedHead == 0 || !CellLabels.Any(label => label.CellNo == cell))
                return;
            _selectedCell = cell;
            UpdateCell();
            Notify();
        }

        private void ShowGlass()
        {
            _selectedCell = 0;
            UpdateCell();
            Notify();
        }

        private void UpdateCell()
        {
            RefreshReferenceShots();
            var shots = (_cells.GetValueOrDefault(_selectedCell) ?? []).Where(shot => _selectedHeads.Contains(shot.HeadNo)).Select(shot => shot with { IsReference = _referenceShotKeys.Contains(shot.Key) }).ToArray();
            ShotRows = shots.GroupBy(shot => shot.Row).OrderBy(group => group.Key).Select(group => new ST_SIMPLE_CORRECTION_SHOT_ROW(group.Key, group.OrderBy(shot => shot.Column).ToArray())).ToArray();
            CellSummary = _selectedCell > 0 ? $"{shots.Count(shot => shot.CanProcess):N0} Process / {shots.Length:N0} Shots · 기준 Shot: 파랑 / CHESS: 보라 / MASKING: 빨강" : "";
            UpdateHeads();
        }

        private void UpdateHeads()
        {
            var all = _cells.Values.SelectMany(shots => shots).Where(shot => shot.CanProcess).ToArray();
            HeadSelections = Enumerable.Range(1, 8).Select(head => new ST_SIMPLE_CORRECTION_HEAD(head, all.Count(shot => shot.HeadNo == head), _enabledHeads.Contains(head), _selectedHeads.Contains(head))).ToArray();
        }

        // Retain the design model even when no physical Head covers some coordinates.
        private static ST_HEAD_SHOT_POINT CreateUnassignedPoint(ST_DESIGN_SHOT_POINT shot) => new(
            SequenceNo: 0, 
            ShotKey: shot.ShotKey, 
            HeadNo: 0, 
            CellNo: shot.CellNo, 
            CellShotNo: shot.CellShotNo, 
            GroupColumn: shot.GroupColumn, 
            GroupRow: shot.GroupRow, 
            GroupCountX: shot.GroupCountX, 
            GroupCountY: shot.GroupCountY, 
            DesignX: shot.DesignX, 
            DesignY: shot.DesignY, 
            ScannerGx: 0, 
            ScannerGy: 0, 
            StageX: 0, 
            StageY: 0, 
            RecipeOffsetX: shot.RecipeOffsetX, 
            RecipeOffsetY: shot.RecipeOffsetY, 
            HeadDefaultOffsetX: 0, 
            HeadDefaultOffsetY: 0, 
            FirstBeamOffsetX: 0, 
            FirstBeamOffsetY: 0, 
            ReviewOffsetX: shot.ReviewOffsetX, 
            ReviewOffsetY: shot.ReviewOffsetY, 
            ShiftOffsetX: 0, 
            ShiftOffsetY: 0, 
            ShiftScannerOffsetX: 0, 
            ShiftScannerOffsetY: 0, 
            FinalScannerOffsetX: shot.RecipeOffsetX + shot.ReviewOffsetX + shot.AlignOffsetX, 
            FinalScannerOffsetY: shot.RecipeOffsetY + shot.ReviewOffsetY + shot.AlignOffsetY, 
            EncoderWaitPosition: 0
        );
        private void Notify()
        {
            OffsetActionCommand.NotifyCanExecuteChanged();
            PropertyChanged?.Invoke(this, new(null));
        }

        public void RefreshInspectionDisplay()
        {
            RefreshCallouts();
            Notify();
        }
    }

    public sealed record ST_SIMPLE_CORRECTION_HEAD(int HeadNo, int ShotCount, bool IsEnabled, bool IsSelected)
    {
        public string Name => $"HEAD {HeadNo:00}";
        public string Detail => IsEnabled ? $"{ShotCount:N0} Shots" : "NOT USE";
    }

    public sealed record ST_SIMPLE_CORRECTION_SHOT(string Key, string Name, int HeadNo, int Column, int Row, bool CanProcess, string State, bool IsChessSkipped, bool IsMasked)
    {
        public string Head => HeadNo > 0 ? $"H{HeadNo:00}" : "-";
        public int CellNo { get; init; }
        public int SequenceNo { get; init; }
        public bool IsReference { get; init; }
        public double DesignX { get; init; }
        public double DesignY { get; init; }
        public int CellShotNo { get; init; }
        public int GroupCountX { get; init; }
        public int GroupCountY { get; init; }
    }

    public sealed record ST_SIMPLE_CORRECTION_SHOT_ROW(int Row, IReadOnlyList<ST_SIMPLE_CORRECTION_SHOT> Shots);
    public sealed record ST_SIMPLE_CORRECTION_FIRST_SHOT(int CellNo, string ShotKey, string ShotName)
    {
        public int HeadNo { get; init; }
        public string Cell => CellNo > 0 ? $"CELL {CellNo:00}" : "-";
        public string Shot => ShotKey.Length > 0 ? ShotName : "가공 Shot 없음";
    }

    public sealed record ST_SIMPLE_CORRECTION_CALLOUT(
        int CellNo, 
        string ShotKey, 
        string ShotName, 
        Rect CellBounds, 
        PointCollection LeaderPoints, 
        double LabelLeft, 
        double LabelTop, 
        double LabelWidth, 
        double LabelHeight
    )
    {
        public bool IsSelected { get; init; }
        public string InspectionState { get; init; } = "";
        public Brush? InspectionBrush { get; init; }
        public bool HasInspectionState => InspectionState.Length > 0;
        public string Detail => $"CELL {CellNo:00} / SHOT {ShotName}";
        public string Label => $"Cell{CellNo:00}-{ShotName}";
    }

    /// <summary>Per-scope edit draft. Changing an input never writes a recipe or applies motion.</summary>
    public sealed class CSimpleCorrectionInput(int headNo, int cellNo, string shotKey, string shotName) : INotifyPropertyChanged
    {
        private string _inputX = "0.000", _inputY = "0.000";
        private bool _editedX, _editedY;
        public event PropertyChangedEventHandler? PropertyChanged;
        public int HeadNo { get; } = headNo;
        public int CellNo { get; } = cellNo;
        public string ShotKey { get; } = shotKey;
        public string ShotName { get; } = shotName;
        public string Title { get; } = $"Cell{cellNo:00}-{shotName}";
        public string CurrentX { get; private set; } = "0.000";
        public string CurrentY { get; private set; } = "0.000";

        public string InputX
        {
            get => _inputX;
            set
            {
                _inputX = Normalize(value);
                _editedX = true;
                Notify();
            }
        }

        public string InputY
        {
            get => _inputY;
            set
            {
                _inputY = Normalize(value);
                _editedY = true;
                Notify();
            }
        }

        public void UpdateCurrent(double x, double y)
        {
            var currentX = Format(x);
            var currentY = Format(y);
            if (CurrentX == currentX && CurrentY == currentY)
                return;
            CurrentX = currentX;
            CurrentY = currentY;
            if (!_editedX)
                _inputX = currentX;
            if (!_editedY)
                _inputY = currentY;
            Notify();
        }

        private static string Normalize(string input)
        {
            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new ArgumentException("유한한 숫자를 입력해 주세요.", nameof(input));
            return Format(value);
        }

        private static string Format(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.000", CultureInfo.InvariantCulture);
        private void Notify() => PropertyChanged?.Invoke(this, new(null));
    }

    public sealed record ST_SIMPLE_OFFSET_CHANGE(string RecipeId, int HeadNo, int CellNo, double X, double Y, IReadOnlyList<ST_REVIEW_OFFSET_ROW> Rows, IReadOnlyDictionary<string, string> SourceParameters)
    {
        public IReadOnlyDictionary<(int Cell, string Shot), int> TargetHeads { get; init; } = new Dictionary<(int, string), int>();
        public string Scope => CellNo == 0 ? $"HEAD {HeadNo:00} 전체" : $"HEAD {HeadNo:00} / Cell{CellNo:00}";
    }
}
