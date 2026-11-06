namespace Drilling.UI.Menu.Menus
{
    using System.Globalization;
    using System.IO;
    using System.ComponentModel;
    using System.Windows;
    using Drilling.UI.Popup;
    using Drilling.Common.Managers;
    using Drilling.Common.Interface;
    using Drilling.Common.Motion;
    using Drilling.Common.Alarm;
    using Drilling.Common.InterLock;
    using Drilling.Common.Station;
    using Drilling.Common.Recipe;
    using Drilling.Common.Review;
    using Drilling.Common.Correction;
    using System.Windows.Media;

    public sealed class CMenuRecipe : CMenuBase
    {
        private static readonly string[] CellParameterKeys = ["CELL_ALIGN_TO_1ST_PIXEL_X", "CELL_ALIGN_TO_1ST_PIXEL_Y", "CELL_ROTATION", "CELL_MODEL_TYPE"];
        private static readonly string[] PatternParameterKeys = ["PIXEL_SIZE", "SPLITED_BEAM_COUNT"];
        private static readonly string[] GlassSettingKeys = [
            "GLASS_SIZE_X", 
            "GLASS_SIZE_Y", 
            "MAX_CELL_NUMBER", 
            "AK_MARGIN_X", 
            "AK_MARGIN_Y", 
            "ESC_EDGE_TO_GLASS_EDGE", 
            "GLASS_CENTER_TO_SCANNER_CENTER"
        ];
        private const int RecipeFilesPageSize = 10;
        private const int MaximumMaskingHoleCount = 3;
        private const int MaximumCellModelTypeCount = 5;
        private const double MinimumLayoutPatternWidth = 18.0;
        private const double MinimumLayoutPatternHeight = 32.0;
        private const string SelectRecipeSettingKey = "SELECT_RECIPE";
        private readonly CRecipeManager _recipeManager;
        private readonly CSettingManager _settingManager;
        private readonly Func<string> _selectRecipeIdProvider;
        private readonly Action<string> _selectRecipeIdSetter;
        private readonly Func<string> _viewRecipeIdProvider;
        private readonly Action<string> _viewRecipeIdSetter;
        private readonly Func<string> _selectedCategoryProvider;
        private readonly Action<string> _selectedCategorySetter;
        private string _selectedGroup = "ALL";
        private int _selectedCellNo = 1;
        private int _selectedCellShotNo = 1;
        private ST_SHOT_GROUP_EDITOR_ROW? _selectedShotGroup;
        private IReadOnlyList<ST_RECIPE_CELL_VIEW_MODEL> _recipeCells = [];
        private IReadOnlyList<ST_RECIPE_FILE> _recipeFiles = [];
        private IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> _previewTrackedItems = [];
        private IReadOnlyList<ST_SYSTEM_PARAMETER> _headPreviewSettings = [];
        private CancellationTokenSource? _previewRefreshCancellation;
        private string _lastRecipeListSelectedFile = "";
        private readonly Dictionary<string, string> _lastValidChessValues = new(StringComparer.OrdinalIgnoreCase);
        private bool _isRevertingChessValue;
        private bool _isBatchUpdatingCellPlacement;
        private string _recipeFilesPageText = "PAGE 0 / 0";
        private int _recipeFilesPageIndex;
        private int _recipeFilesPageCount;
        private readonly Func<CMenuRecipe?> _editScreenProvider;
        private readonly Action<string> _setStatusMessage;
        private readonly Action<EN_MENU, string> _showLoadingScreen;
        private readonly Action _refreshShellStatus;
        private readonly Func<Task> _refreshCurrentScreen;
        public CMenuRecipe(
            CRecipeManager recipeManager, 
            CSettingManager settingManager, 
            Func<string> selectRecipeIdProvider, 
            Action<string> selectRecipeIdSetter, 
            Func<string> viewRecipeIdProvider, 
            Action<string> viewRecipeIdSetter, 
            Func<string> selectedCategoryProvider, 
            Action<string> selectedCategorySetter, 
            Func<CMenuRecipe?> editScreenProvider, 
            Action<string> setStatusMessage, 
            Action<EN_MENU, string> showLoadingScreen, 
            Action refreshShellStatus, 
            Func<Task> refreshCurrentScreen
        )
        {
            _recipeManager = recipeManager;
            _settingManager = settingManager;
            _selectRecipeIdProvider = selectRecipeIdProvider;
            _selectRecipeIdSetter = selectRecipeIdSetter;
            _viewRecipeIdProvider = viewRecipeIdProvider;
            _viewRecipeIdSetter = viewRecipeIdSetter;
            _selectedCategoryProvider = selectedCategoryProvider;
            _selectedCategorySetter = selectedCategorySetter;
            _editScreenProvider = editScreenProvider;
            _setStatusMessage = setStatusMessage;
            _showLoadingScreen = showLoadingScreen;
            _refreshShellStatus = refreshShellStatus;
            _refreshCurrentScreen = refreshCurrentScreen;
            ZeroDefence = new CRecipeZeroDefencePanel(WriteZeroDefenceDraft);
            async void ViewCallback(object? parameter)
            {
                await View(parameter);
            }

            ViewCommand = new CButtonCommand(ViewCallback);
            async void ShowPreviousRecipePageCallback(object? arg0)
            {
                await ShowPreviousRecipePage();
            }

            bool CanShowPreviousRecipePageCallback(object? arg0)
            {
                return CanShowPreviousRecipePage();
            }

            PreviousRecipePageCommand = new CButtonCommand(ShowPreviousRecipePageCallback, CanShowPreviousRecipePageCallback);
            async void ShowNextRecipePageCallback(object? arg0)
            {
                await ShowNextRecipePage();
            }

            bool CanShowNextRecipePageCallback(object? arg0)
            {
                return CanShowNextRecipePage();
            }

            NextRecipePageCommand = new CButtonCommand(ShowNextRecipePageCallback, CanShowNextRecipePageCallback);
            async void SelectCategoryCallback(object? parameter)
            {
                await SelectCategory(parameter);
            }

            SelectCategoryCommand = new CButtonCommand(SelectCategoryCallback);
            async void SelectGroupCallback(object? parameter)
            {
                await SelectGroup(parameter);
            }

            SelectGroupCommand = new CButtonCommand(SelectGroupCallback);
            async void SelectCellCallback(object? parameter)
            {
                await SelectCell(parameter);
            }

            SelectCellCommand = new CButtonCommand(SelectCellCallback);
            async void SelectPreviewCellCallback(object? parameter)
            {
                await SelectPreviewCell(parameter);
            }

            SelectPreviewCellCommand = new CButtonCommand(SelectPreviewCellCallback);
            void BackToCellPreviewCallback(object? arg0)
            {
                SetCellDetailVisible(false);
            }

            BackToCellPreviewCommand = new CButtonCommand(BackToCellPreviewCallback);
            ToggleCellPlacementCommand = new CButtonCommand(_ => ToggleCellPlacementExpanded());
            async void PreviousCellCallback(object? arg0)
            {
                await SelectAdjacentCell(-1);
            }

            bool MatchesObject(object? arg0)
            {
                return _selectedCellNo > 1;
            }

            PreviousCellCommand = new CButtonCommand(PreviousCellCallback, MatchesObject);
            async void NextCellCallback(object? arg0)
            {
                await SelectAdjacentCell(1);
            }

            bool MatchesObject2(object? arg0)
            {
                return _selectedCellNo < _recipeCells.Count;
            }

            NextCellCommand = new CButtonCommand(NextCellCallback, MatchesObject2);
            NavigateShotGroupCommand = new CButtonCommand(NavigateShotGroup, CanNavigateShotGroup);
            async void SelectRecipeCallback(object? arg0)
            {
                await Select();
            }

            bool CanSelectRecipeCallback(object? arg0)
            {
                return CanSelectViewRecipe();
            }

            SelectCommand = new CButtonCommand(SelectRecipeCallback, CanSelectRecipeCallback);
            async void CreateCallback(object? arg0)
            {
                await Create();
            }

            CreateCommand = new CButtonCommand(CreateCallback);
            async void ModifyCallback(object? arg0)
            {
                await Modify();
            }

            ModifyCommand = new CButtonCommand(ModifyCallback, _ => CanEditViewRecipe());
            async void SaveCallback(object? arg0)
            {
                await Save();
            }

            SaveCommand = new CButtonCommand(SaveCallback, _ => CanEditViewRecipe());
            async void DeleteCallback(object? arg0)
            {
                await Delete();
            }

            DeleteCommand = new CButtonCommand(DeleteCallback, _ => CanDeleteViewRecipe());
        }

        public override EN_MENU Menu
        {
            get
            {
                return EN_MENU.Recipe;
            }
        }

        public override void OnDeactivated()
        {
            _viewRecipeIdSetter(_selectRecipeIdProvider());
        }

        public IReadOnlyList<ST_DISPLAY_ITEM> RecipeList { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> Parameters { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> History { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> Actions { get; private set; } = [];
        public string SelectedRecipeFile { get; private set; } = "";
        public string SelectRecipeFile { get; private set; } = "";

        public string ViewRecipeFile
        {
            get
            {
                return SelectedRecipeFile;
            }
        }

        public bool IsViewRecipeSelected { get; private set; } = true;

        public string RecipeSelectionSummaryText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SelectRecipeFile) && string.IsNullOrWhiteSpace(ViewRecipeFile))
                {
                    return "-";
                }

                return IsViewRecipeSelected ? $"SELECT/VIEW {SelectRecipeFile}" : $"SELECT {SelectRecipeFile} / VIEW {ViewRecipeFile}";
            }
        }

        public IReadOnlyList<ST_RECIPE_FILE> RecipeFiles
        {
            get
            {
                return _recipeFiles;
            }

            private set
            {
                SetSequenceProperty(ref _recipeFiles, value);
            }
        }

        public string RecipeFilesPageText
        {
            get
            {
                return _recipeFilesPageText;
            }

            private set
            {
                SetProperty(ref _recipeFilesPageText, value);
            }
        }

        public IReadOnlyList<ST_RECIPE_CATEGORY_TAB> ItemTabs { get; private set; } = [];
        public IReadOnlyList<ST_RECIPE_GROUP_TAB> GroupTabs { get; private set; } = [];
        public IReadOnlyList<ST_RECIPE_CELL_OVERVIEW_ROW> CellOverviewRows { get; private set; } = [];
        public IReadOnlyList<ST_SHOT_GROUP_EDITOR_ROW> ShotGroupRows { get; private set; } = [];

        public ST_SHOT_GROUP_EDITOR_ROW? SelectedShotGroup
        {
            get
            {
                return _selectedShotGroup;
            }

            set
            {
                if (!SetProperty(ref _selectedShotGroup, value))
                {
                    return;
                }

                if (value is not null)
                {
                    _selectedCellShotNo = value.CellShotNo;
                }

                foreach (var row in ShotGroupRows)
                {
                    row.IsSelected = value is not null && row.CellShotNo == value.CellShotNo;
                }

                OnPropertyChanged(nameof(SelectedShotGroupIndicatorText));
                NavigateShotGroupCommand.NotifyCanExecuteChanged();
                RefreshCellPatternViewer();
            }
        }

        public ST_RECIPE_PARAMETER_EDITOR_ITEM? GlassSizeXItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? GlassSizeYItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? MaxCellNumberItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? PixelSizeItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? PixelCountXItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? PixelCountYItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? ProcessPitchItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? ChessItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? SplitBeamCountItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? CellModelTypeCountItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? AkMarginXItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? AkMarginYItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? EscEdgeToGlassEdgeItem { get; private set; }
        public ST_RECIPE_PARAMETER_EDITOR_ITEM? GlassCenterToScannerCenterItem { get; private set; }
        public IReadOnlyList<ST_RECIPE_DISTORTION_KEY_ITEM> DistortionKeyItems { get; private set; } = [];
        public bool IsCellCategory { get; private set; }
        public bool IsHeadCategory { get; private set; }
        public bool IsZeroDefenceCategory => ItemTabs.Any(tab => tab.IsSelected && tab.Category.Equals("ZERO_DEFENCE", StringComparison.OrdinalIgnoreCase));
        public CRecipeZeroDefencePanel ZeroDefence { get; }

        private bool _isCellDetailVisible;
        private bool _isCellPlacementExpanded;
        public bool IsCellDetailVisible
        {
            get
            {
                return _isCellDetailVisible;
            }
        }

        public bool IsCellPreviewVisible
        {
            get
            {
                return !_isCellDetailVisible;
            }
        }

        public bool IsCellPlacementExpanded
        {
            get
            {
                return _isCellPlacementExpanded;
            }
        }

        public string CellPlacementExpandButtonText
        {
            get
            {
                return _isCellPlacementExpanded ? "COLLAPSE" : "EXPAND";
            }
        }

        public ImageSource? CellPreviewImage { get; private set; }
        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellPreviewLabels { get; private set; } = [];
        public string CellPreviewSummaryText { get; private set; } = "";
        public ImageSource? CellShotMapImage { get; private set; }
        public ImageSource? SelectedShotDetailImage { get; private set; }
        public ImageSource? HeadPreviewImage { get; private set; }
        public IReadOnlyList<ST_HEAD_COVERAGE_PREVIEW_LABEL> HeadPreviewLabels { get; private set; } = [];
        public string HeadPreviewSummaryText { get; private set; } = "";

        public string CurrentCellIndicatorText
        {
            get
            {
                return $"CURRENT: Cell{_selectedCellNo}";
            }
        }

        public string SelectedCellNavigationText
        {
            get
            {
                return $"CELL {_selectedCellNo:00} / {Math.Max(1, _recipeCells.Count):00}";
            }
        }

        public string SelectedCellShotGroupTitle
        {
            get
            {
                var cell = GetSelectedRecipeCell();
                return cell is null ? $"CELL {_selectedCellNo:00} SHOT MAP" : $"CELL {_selectedCellNo:00} SHOT MAP / " + $"{cell.PatternLayout.GroupCountX:N0} x {cell.PatternLayout.GroupCountY:N0} / " + $"{ShotGroupRows.Count:N0} SHOTS";
            }
        }

        public bool IsSelectedCellShotMapLandscape
        {
            get
            {
                var cell = GetSelectedRecipeCell();
                return cell is not null && cell.PatternLayout.PixelCountX > cell.PatternLayout.PixelCountY;
            }
        }

        public double CellShotMapPanelHeight
        {
            get
            {
                return IsSelectedCellShotMapLandscape ? 460.0 : 440.0;
            }
        }

        public double CellShotMapSurfaceWidth
        {
            get
            {
                var cell = GetSelectedRecipeCell();
                return cell is null ? CRecipeCellPatternPreviewDrawing.CellMapPortraitDesignWidth : CRecipeCellPatternPreviewDrawing.GetCellMapDesignWidth(cell.PatternLayout);
            }
        }

        public double CellShotMapSurfaceHeight
        {
            get
            {
                var cell = GetSelectedRecipeCell();
                return cell is null ? CRecipeCellPatternPreviewDrawing.CellMapPortraitDesignHeight : CRecipeCellPatternPreviewDrawing.GetCellMapDesignHeight(cell.PatternLayout);
            }
        }

        public int CellShotMapGridColumn => IsSelectedCellShotMapLandscape ? 0 : 0;
        public int CellShotMapGridColumnSpan => IsSelectedCellShotMapLandscape ? 3 : 1;
        public int CellShotMapGridRow => 0;
        public int CellShotMapGridRowSpan => IsSelectedCellShotMapLandscape ? 1 : 3;
        public int CellShotDetailGridColumn => IsSelectedCellShotMapLandscape ? 0 : 2;
        public int CellShotDetailGridColumnSpan => IsSelectedCellShotMapLandscape ? 3 : 1;
        public int CellShotDetailGridRow => IsSelectedCellShotMapLandscape ? 2 : 0;
        public int CellShotDetailGridRowSpan => IsSelectedCellShotMapLandscape ? 1 : 3;
        public GridLength CellShotDetailVerticalGap => IsSelectedCellShotMapLandscape ? new GridLength(0.0) : new GridLength(10.0);
        public int SelectedShotDetailGridColumn => 0;
        public int SelectedShotDetailGridColumnSpan => IsSelectedCellShotMapLandscape ? 1 : 3;
        public int SelectedShotDetailGridRow => 0;
        public int ShotInformationGridColumn => IsSelectedCellShotMapLandscape ? 2 : 0;
        public int ShotInformationGridColumnSpan => IsSelectedCellShotMapLandscape ? 1 : 3;
        public int ShotInformationGridRow => IsSelectedCellShotMapLandscape ? 0 : 2;

        public string SelectedCellExcludedTargetText
        {
            get
            {
                var shotGroups = GetSelectedRecipeCell()?.PatternLayout;
                return shotGroups is null || !shotGroups.HasExcludedTargets ? "FULL CELL PITCH RANGE" : $"TRAILING PIXEL X+ {shotGroups.ExcludedTargetCountX:N0} / Y+ {shotGroups.ExcludedTargetCountY:N0}";
            }
        }

        public bool SelectedCellHasExcludedTargets
        {
            get
            {
                return GetSelectedRecipeCell()?.PatternLayout.HasExcludedTargets == true;
            }
        }

        public string SelectedShotGroupIndicatorText
        {
            get
            {
                return SelectedShotGroup is null ? "NO SHOT GROUP SELECTED" : SelectedShotGroup.MatrixPointName;
            }
        }

        public string SelectedShotGroupGridText
        {
            get
            {
                var cell = GetSelectedRecipeCell();
                return SelectedShotGroup is null || cell is null ? "-" : $"ROW {SelectedShotGroup.Row:N0} / {cell.PatternLayout.GroupCountY:N0}   " + $"COL {SelectedShotGroup.Column:N0} / {cell.PatternLayout.GroupCountX:N0}";
            }
        }

        public string SelectedShotGroupCenterText
        {
            get
            {
                return SelectedShotGroup is null ? "-" : $"X {SelectedShotGroup.CenterXText} mm   Y {SelectedShotGroup.CenterYText} mm";
            }
        }

        public string SelectedShotGroupBeamText
        {
            get
            {
                var splitBeamCount = ReadManagedInt(AllManagedItems, "SPLITED_BEAM_COUNT", 1);
                return $"{splitBeamCount:N0} x {splitBeamCount:N0} / " + $"{(long)splitBeamCount * splitBeamCount:N0} BEAMS";
            }
        }

        public string SelectedShotGroupPixelWindowText
        {
            get
            {
                var cell = GetSelectedRecipeCell();
                var modelPattern = ReadManagedModelPattern(AllManagedItems, cell?.ModelType ?? 1);
                var splitBeamCount = ReadManagedInt(AllManagedItems, "SPLITED_BEAM_COUNT", 1);
                return $"{modelPattern.ProcessPitch:N0} PIXEL CENTER STRIDE / {splitBeamCount:N0} x {splitBeamCount:N0} DOE";
            }
        }

        public string SelectedShotGroupPitchText
        {
            get
            {
                var cell = GetSelectedRecipeCell();
                return cell is null ? "-" : $"CENTER {cell.PatternLayout.GroupPitchMillimeters:0.######} mm   " + $"BEAM {cell.PatternLayout.BeamPitchMillimeters:0.######} mm";
            }
        }

        public string SelectedShotGroupStateText
        {
            get
            {
                return SelectedShotGroup?.PlacementState ?? "-";
            }
        }

        public string ShotGroupPatternSummary { get; private set; } = "";
        public string SelectedGroup { get; private set; } = "ALL";
        public IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> AllManagedItems { get; private set; } = [];
        public IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> ManagedItems { get; private set; } = [];

        private IReadOnlyList<ST_RECIPE_HISTORY_ROW> _changeHistory = [];
        public IReadOnlyList<ST_RECIPE_HISTORY_ROW> ChangeHistory { get => _changeHistory; private set => SetProperty(ref _changeHistory, value); }
        public IReadOnlyList<ST_RECIPE_STATE_ROW> StateRows { get; private set; } = [];
        public CButtonCommand SelectCommand { get; }
        public CButtonCommand PreviousRecipePageCommand { get; }
        public CButtonCommand NextRecipePageCommand { get; }
        public CButtonCommand ViewCommand { get; }
        public CButtonCommand SelectCategoryCommand { get; }
        public CButtonCommand SelectGroupCommand { get; }
        public CButtonCommand SelectCellCommand { get; }
        public CButtonCommand SelectPreviewCellCommand { get; }
        public CButtonCommand BackToCellPreviewCommand { get; }
        public CButtonCommand ToggleCellPlacementCommand { get; }
        public CButtonCommand PreviousCellCommand { get; }
        public CButtonCommand NextCellCommand { get; }
        public CButtonCommand NavigateShotGroupCommand { get; }
        public CButtonCommand CreateCommand { get; }
        public CButtonCommand ModifyCommand { get; }
        public CButtonCommand SaveCommand { get; }
        public CButtonCommand DeleteCommand { get; }

        public override async Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
        {
            var recipes = await _recipeManager.LoadRecipes(cancellationToken);
            var history = await _recipeManager.LoadHistory(cancellationToken);
            var optionSettings = _settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken).Concat(_settingManager.LoadSection(EN_SETTING_TAB.Motor, cancellationToken)).ToArray();
            var selectRecipe = GetSelectedRecipe(recipes, _selectRecipeIdProvider());
            var selectRecipeId = selectRecipe?.Id ?? NormalizeRecipeIdInput(_selectRecipeIdProvider());
            var viewRecipeId = NormalizeRecipeIdInput(_viewRecipeIdProvider());
            if (string.IsNullOrWhiteSpace(viewRecipeId))
            {
                viewRecipeId = selectRecipeId;
                _viewRecipeIdSetter(viewRecipeId);
            }

            var recipe = GetSelectedRecipe(recipes, viewRecipeId);
            var selectedRecipeFile = GetRecipeFileName(recipe);
            var selectRecipeFile = GetRecipeFileName(selectRecipe);
            if (string.IsNullOrWhiteSpace(selectRecipeFile) && !string.IsNullOrWhiteSpace(selectRecipeId))
            {
                selectRecipeFile = $"{selectRecipeId}.csv";
            }

            var isViewRecipeSelected = recipe is not null && !string.IsNullOrWhiteSpace(selectRecipeId) && recipe.Id.Equals(selectRecipeId, StringComparison.OrdinalIgnoreCase);
            SyncRecipeFilesPage(recipes, selectedRecipeFile);
            var loadedManagedItems = BuildManagedItems(recipe);
            var allManagedItems = GetEditItems(loadedManagedItems, _editScreenProvider(), selectedRecipeFile);
            allManagedItems = EnsureCellPlacementItems(allManagedItems, GetCellCount(allManagedItems));
            var categories = BuildCategories(allManagedItems);
            var selectedCategory = NormalizeCategory(_selectedCategoryProvider(), categories);
            var isCellCategory = selectedCategory.Equals("CELL", StringComparison.OrdinalIgnoreCase);
            var isHeadCategory = selectedCategory.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            var cellCount = GetCellCount(allManagedItems);
            var cells = BuildCells(allManagedItems, cellCount);
            _recipeCells = cells;
            _selectedCellNo = Math.Clamp(_selectedCellNo, 1, cellCount);
            OnPropertyChanged(nameof(SelectedCellNavigationText));
            PreviousCellCommand.NotifyCanExecuteChanged();
            NextCellCommand.NotifyCanExecuteChanged();
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_CELL_VIEW_MODEL cell)
            {
                return cell.CellNo == _selectedCellNo;
            }

            bool ShouldIncludeItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Category.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase) && !IsMovedToCellTab(item);
            }

            var categoryFilteredItems = isCellCategory ? cells.First(MatchesItem).Items : string.IsNullOrWhiteSpace(selectedCategory) ? [] : allManagedItems.Where(ShouldIncludeItem).ToArray();
            var groups = BuildGroups(categoryFilteredItems);
            var selectedGroup = isCellCategory ? "ALL" : NormalizeGroup(_selectedGroup, groups);
            bool MatchesSourceGroup(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.SourceGroup.Equals(selectedGroup, StringComparison.OrdinalIgnoreCase);
            }

            var filteredManagedItems = isCellCategory || selectedGroup == "ALL" ? categoryFilteredItems : categoryFilteredItems.Where(MatchesSourceGroup).ToArray();
            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION item)
            {
                return new ST_DISPLAY_ITEM(item.Id, item.Name);
            }

            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay2(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER item)
            {
                return new ST_DISPLAY_ITEM(item.Name, item.Value, $"{item.Unit} / {item.Range}");
            }

            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay3(global::Drilling.Common.Managers.ST_RECIPE_HISTORY item)
            {
                return new ST_DISPLAY_ITEM(item.ChangedAt.ToString("yyyy-MM-dd HH:mm"), item.ItemName, $"{item.OldValue} -> {item.NewValue} / {item.OperatorId}");
            }

            Apply(
                recipes.Select(BuildDisplay).ToArray(), 
                recipe?.Parameters.Select(BuildDisplay2).ToArray() ?? [], 
                history.Select(BuildDisplay3).ToArray(), 
                BuildActions(), 
                selectedRecipeFile, 
                selectRecipeFile, 
                isViewRecipeSelected, 
                BuildRecipeFiles(recipes, recipe, selectRecipe, _recipeFilesPageIndex), 
                BuildCategoryTabs(categories, selectedCategory), 
                BuildGroupTabs(groups, selectedGroup), 
                BuildCellOverviewRows(cells, allManagedItems), 
                isCellCategory, 
                isCellCategory ? BuildLayoutPreview(allManagedItems, cells, _selectedCellNo, optionSettings) : null, 
                isHeadCategory, 
                isHeadCategory ? BuildHeadPreview(allManagedItems, optionSettings, selectedGroup) : null, 
                optionSettings, 
                selectedGroup, 
                allManagedItems, 
                filteredManagedItems, 
                BuildChangeHistory(history), 
                BuildStateRows(recipe, selectedRecipeFile, allManagedItems)
            );
            if (IsZeroDefenceCategory)
                ZeroDefence.Update(recipe is null ? null : recipe with { Parameters = BuildRecipeParameters(recipe.Id) }, optionSettings.Concat(_settingManager.LoadSection(EN_SETTING_TAB.Motor, cancellationToken)).ToArray(), CanEditViewRecipe());
            bool MatchesItem2(global::Drilling.UI.Menu.Menus.ST_RECIPE_CELL_VIEW_MODEL cell)
            {
                return cell.CellNo == _selectedCellNo;
            }

            UpdateShotGroupRows(isCellCategory && _isCellDetailVisible ? BuildShotGroupRows(allManagedItems, cells.First(MatchesItem2)) : []);
            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay4(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER item)
            {
                return new ST_DISPLAY_ITEM(item.Name, $"{item.Value} {item.Unit}".Trim(), item.Range);
            }

            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay5(global::Drilling.Common.Managers.ST_RECIPE_HISTORY item)
            {
                return new ST_DISPLAY_ITEM(item.ItemName, $"{item.OldValue} -> {item.NewValue}", item.OperatorId);
            }

            return new CScreenViewModel(
                EN_MENU.Recipe, 
                "RECIPE / MANAGE", 
                "Recipe item edit, create, modify, save, delete.", 
                [new("Recipe Count", recipes.Count.ToString()), new("Select", selectRecipeFile), new("View", selectedRecipeFile)], 
                [new("Managed Items", recipe?.Parameters.Select(BuildDisplay4).ToArray() ?? []), new("Change History", history.Select(BuildDisplay5).ToArray())], 
                recipe: this
            );
        }

        private async Task ShowPreviousRecipePage()
        {
            if (!CanShowPreviousRecipePage())
            {
                return;
            }

            _recipeFilesPageIndex--;
            NotifyRecipeFilesPageCommands();
            await _refreshCurrentScreen();
        }

        private async Task ShowNextRecipePage()
        {
            if (!CanShowNextRecipePage())
            {
                return;
            }

            _recipeFilesPageIndex++;
            NotifyRecipeFilesPageCommands();
            await _refreshCurrentScreen();
        }

        private bool CanShowPreviousRecipePage()
        {
            return _recipeFilesPageIndex > 0;
        }

        private bool CanShowNextRecipePage()
        {
            return _recipeFilesPageIndex + 1 < _recipeFilesPageCount;
        }

        private void NotifyRecipeFilesPageCommands()
        {
            PreviousRecipePageCommand.NotifyCanExecuteChanged();
            NextRecipePageCommand.NotifyCanExecuteChanged();
        }

        private async Task View(object? parameter)
        {
            var recipeId = GetRecipeIdFromParameter(parameter);
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                return;
            }

            if (!recipeId.Equals(GetRecipeIdFromParameter(SelectedRecipeFile), StringComparison.OrdinalIgnoreCase) && HasEditedItems() && !CRecipeConfirmDialog.ShowConfirmation("레시피 VIEW 변경", "수정 중입니다. 저장하지 않고 이동할 경우 변경이 취소됩니다.\n이동하시겠습니까?"))
            {
                return;
            }

            _viewRecipeIdSetter(recipeId);
            NotifyCommands();
            _setStatusMessage($"Recipe {recipeId}.csv view loaded.");
            await _refreshCurrentScreen();
        }

        private async Task Select()
        {
            var recipeId = GetRecipeIdFromParameter(SelectedRecipeFile);
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                _setStatusMessage("Recipe select skipped. No recipe is in view.");
                return;
            }

            if (!CanSelectViewRecipe())
            {
                _setStatusMessage($"Recipe {recipeId}.csv is already selected.");
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("레시피 선택", $"{recipeId}.csv 레시피를 SELECT 하시겠습니까?"))
            {
                return;
            }

            var previousRecipeId = _selectRecipeIdProvider();
            _selectRecipeIdSetter(recipeId);
            _settingManager.SetValue(EN_SETTING_TAB.Option, SelectRecipeSettingKey, recipeId);
            await _recipeManager.RecordSelection(previousRecipeId, recipeId);
            NotifyCommands();
            _setStatusMessage($"Recipe {recipeId}.csv selected.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
            CRecipeConfirmDialog.ShowCompletion("레시피 선택 완료", $"{recipeId}.csv 레시피를 SELECT 하였습니다.");
        }

        private async Task SelectCategory(object? parameter)
        {
            if (parameter is not string category || string.IsNullOrWhiteSpace(category))
            {
                return;
            }

            var selectedCategory = category.Trim().ToUpperInvariant();
            _selectedCategorySetter(selectedCategory);
            _selectedGroup = "ALL";
            _setStatusMessage($"Recipe category {selectedCategory} selected.");
            await _refreshCurrentScreen();
        }

        private async Task SelectGroup(object? parameter)
        {
            if (parameter is not string group || string.IsNullOrWhiteSpace(group))
            {
                return;
            }

            _selectedGroup = group.Trim().ToUpperInvariant();
            _setStatusMessage($"Recipe group {_selectedGroup} selected.");
            await _refreshCurrentScreen();
        }

        private async Task SelectCell(object? parameter)
        {
            var cellNo = parameter switch
            {
                int number => number,
                string text when int.TryParse(text, out var number) => number,
                _ => 0
            };
            if (cellNo <= 0 || cellNo > _recipeCells.Count)
            {
                return;
            }

            _selectedCellNo = cellNo;
            OnPropertyChanged(nameof(CurrentCellIndicatorText));
            OnPropertyChanged(nameof(SelectedCellNavigationText));
            OnPropertyChanged(nameof(SelectedCellShotGroupTitle));
            PreviousCellCommand.NotifyCanExecuteChanged();
            NextCellCommand.NotifyCanExecuteChanged();
            _setStatusMessage($"Recipe Cell{cellNo} selected.");
            await _refreshCurrentScreen();
        }

        private async Task SelectPreviewCell(object? parameter)
        {
            SetCellDetailVisible(true);
            await SelectCell(parameter);
        }

        public void SelectShotGroupAt(double designX, double designY)
        {
            var cell = GetSelectedRecipeCell();
            if (cell is null || !CRecipeCellPatternPreviewDrawing.TryResolveCellShotNo(designX, designY, cell.PatternLayout, out var cellShotNo) || cellShotNo < 1 || cellShotNo > ShotGroupRows.Count)
            {
                return;
            }

            SelectedShotGroup = ShotGroupRows[cellShotNo - 1];
        }

        public string GetShotGroupNameAt(double designX, double designY)
        {
            var cell = GetSelectedRecipeCell();
            if (cell is null || !CRecipeCellPatternPreviewDrawing.TryResolveCellShotNo(designX, designY, cell.PatternLayout, out var cellShotNo) || cellShotNo < 1 || cellShotNo > ShotGroupRows.Count)
            {
                return "";
            }

            return ShotGroupRows[cellShotNo - 1].MatrixPointName;
        }

        private async Task SelectAdjacentCell(int direction)
        {
            if (_recipeCells.Count == 0 || direction == 0)
            {
                return;
            }

            var nextCellNo = _selectedCellNo + direction;
            if (nextCellNo < 1 || nextCellNo > _recipeCells.Count)
            {
                return;
            }

            await SelectCell(nextCellNo);
        }

        private void NavigateShotGroup(object? parameter)
        {
            if (!TryGetNavigatedCellShotNo(parameter, out var cellShotNo))
            {
                return;
            }

            SelectedShotGroup = ShotGroupRows[cellShotNo - 1];
        }

        private bool CanNavigateShotGroup(object? parameter)
        {
            return TryGetNavigatedCellShotNo(parameter, out _);
        }

        private bool TryGetNavigatedCellShotNo(object? parameter, out int cellShotNo)
        {
            cellShotNo = 0;
            var cell = GetSelectedRecipeCell();
            var selected = SelectedShotGroup;
            if (cell is null || selected is null || ShotGroupRows.Count == 0)
            {
                return false;
            }

            var(columnDelta, rowDelta) = parameter?.ToString()?.Trim().ToUpperInvariant() switch
            {
                "UP" => (0, -1),
                "DOWN" => (0, 1),
                "LEFT" => (-1, 0),
                "RIGHT" => (1, 0),
                _ => (0, 0)};
            if (columnDelta == 0 && rowDelta == 0)
            {
                return false;
            }

            var nextColumn = selected.Column + columnDelta;
            var nextRow = selected.Row + rowDelta;
            if (nextColumn < 1 || nextColumn > cell.PatternLayout.GroupCountX || nextRow < 1 || nextRow > cell.PatternLayout.GroupCountY)
            {
                return false;
            }

            var candidateCellShotNo = ((nextRow - 1) * cell.PatternLayout.GroupCountX) + nextColumn;
            if (candidateCellShotNo < 1 || candidateCellShotNo > ShotGroupRows.Count)
            {
                return false;
            }

            cellShotNo = candidateCellShotNo;
            return true;
        }

        private void SetCellDetailVisible(bool isVisible)
        {
            if (_isCellDetailVisible == isVisible)
            {
                return;
            }

            _isCellDetailVisible = isVisible;
            OnPropertyChanged(nameof(IsCellDetailVisible));
            OnPropertyChanged(nameof(IsCellPreviewVisible));
        }

        public void ToggleCellPlacementExpanded()
        {
            _isCellPlacementExpanded = !_isCellPlacementExpanded;
            OnPropertyChanged(nameof(IsCellPlacementExpanded));
            OnPropertyChanged(nameof(CellPlacementExpandButtonText));
        }

        public ST_CELL_PLACEMENT_WIZARD_CONTEXT CreateCellPlacementWizardContext()
        {
            var cellCount = AllManagedItems.Count == 0 ? Math.Max(1, CellOverviewRows.Count) : GetCellCount(AllManagedItems);
            var modelTypeCount = AllManagedItems.Count == 0 ? MaximumCellModelTypeCount : GetCellModelTypeCount(AllManagedItems);
            return new ST_CELL_PLACEMENT_WIZARD_CONTEXT(
                cellCount, 
                modelTypeCount, 
                ReadManagedDouble(AllManagedItems, "GLASS_SIZE_X", 500.0), 
                ReadManagedDouble(AllManagedItems, "GLASS_SIZE_Y", 300.0), 
                ReadManagedDouble(AllManagedItems, "AK_MARGIN_X", 55.0), 
                ReadManagedDouble(AllManagedItems, "AK_MARGIN_Y", 45.0)
            );
        }

        public void ApplyCellPlacementWizard(ST_CELL_PLACEMENT_WIZARD_RESULT result)
        {
            if (result.CellCount <= 0 || AllManagedItems.Count == 0)
            {
                return;
            }

            var maxCellCount = GetCellCount(AllManagedItems);
            var modelTypeCount = GetCellModelTypeCount(AllManagedItems);
            var appliedCount = 0;
            _isBatchUpdatingCellPlacement = true;
            try
            {
                for (var index = 0; index < result.CellCount; index++)
                {
                    var cellNo = result.StartCell + index;
                    if (cellNo < 1 || cellNo > maxCellCount)
                    {
                        continue;
                    }

                    var(rowIndex, columnIndex) = CalculatePlacementWizardGridIndex(index, result.RowCount, result.ColumnCount, result.Direction);
                    var x = result.StartX + (columnIndex * result.PitchX);
                    var y = result.StartY + (rowIndex * result.PitchY);
                    var modelType = Math.Clamp(result.DefaultModelType, 1, modelTypeCount);
                    foreach (var modelOverride in result.ModelOverrides)
                    {
                        if (modelOverride.Scope.Equals("ROW", StringComparison.OrdinalIgnoreCase) && modelOverride.Index == rowIndex + 1 || modelOverride.Scope.Equals("COL", StringComparison.OrdinalIgnoreCase) && modelOverride.Index == columnIndex + 1)
                        {
                            modelType = Math.Clamp(modelOverride.ModelType, 1, modelTypeCount);
                        }
                    }

                    SetManagedValue($"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X", FormatWizardDouble(x));
                    SetManagedValue($"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y", FormatWizardDouble(y));
                    SetManagedValue($"CELL{cellNo}_MODEL_TYPE", modelType.ToString(CultureInfo.InvariantCulture));
                    appliedCount++;
                }
            }
            finally
            {
                _isBatchUpdatingCellPlacement = false;
            }

            RefreshCellPreviewFromManagedItems();
            _setStatusMessage($"Cell placement wizard applied to {appliedCount:N0} cells.");
        }

        private void SetManagedValue(string key, string value)
        {
            var item = FindManagedItem(AllManagedItems, key);
            if (item is not null)
            {
                item.Value = value;
            }
        }

        private static (int RowIndex, int ColumnIndex) CalculatePlacementWizardGridIndex(int zeroBasedIndex, int rowCount, int columnCount, string direction)
        {
            rowCount = Math.Max(1, rowCount);
            columnCount = Math.Max(1, columnCount);
            if (direction.Equals(ST_CELL_PLACEMENT_WIZARD_DIRECTION.VerticalFirst, StringComparison.OrdinalIgnoreCase))
            {
                return (zeroBasedIndex % rowCount, zeroBasedIndex / rowCount);
            }

            return (zeroBasedIndex / columnCount, zeroBasedIndex % columnCount);
        }

        private static string FormatWizardDouble(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private async Task Save(Func<string, string, bool>? confirm = null, Action<string, string>? warn = null, Action<string, string>? completed = null)
        {
            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message);
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            completed ??= (title, message) => CRecipeConfirmDialog.ShowCompletion(title, message);
            var recipeId = Path.GetFileNameWithoutExtension(SelectedRecipeFile);
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                _setStatusMessage("Recipe save skipped. No recipe is selected.");
                return;
            }

            if (!CanEditViewRecipe())
            {
                _setStatusMessage("선택된 레시피만 수정 가능합니다.");
                return;
            }

            var recipeParameters = BuildRecipeParameters(recipeId);
            var validationMessage = ValidateRecipeWithZeroDefence(recipeParameters);
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                _setStatusMessage(validationMessage);
                warn("레시피 저장 확인", validationMessage);
                return;
            }

            if (!confirm("레시피 저장", $"{recipeId}.csv 레시피를 저장하시겠습니까?"))
                return;
            var recipeName = GetEditedRecipeName(recipeParameters, recipeId);
            await _recipeManager.SaveRecipe(new ST_RECIPE_DEFINITION(recipeId, recipeName, recipeParameters, []));
            NotifyCommands();
            _setStatusMessage($"Recipe {recipeId}.csv saved and CSV verified.");
            _showLoadingScreen(EN_MENU.Recipe, "RECIPE");
            _refreshShellStatus();
            await _refreshCurrentScreen();
            completed("레시피 저장 완료", $"{recipeId}.csv 레시피를 저장하였습니다.");
        }

        private async Task Modify(
            Func<string, string, string, Func<string, string>, string?>? requestName = null, 
            Func<string, string, bool?>? confirmFolder = null, 
            Action<string, string>? warn = null
        )
        {
            requestName ??= (title, message, initial, validate) => ShowRecipeNameDialog(title, message, initial, validate);
            confirmFolder ??= CRecipeConfirmDialog.ShowRecipeFolderRename;
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            var oldRecipeId = GetRecipeIdFromParameter(SelectedRecipeFile);
            if (string.IsNullOrWhiteSpace(oldRecipeId))
            {
                _setStatusMessage("Recipe rename skipped. No recipe is selected.");
                return;
            }

            if (!CanEditViewRecipe())
            {
                _setStatusMessage("선택된 레시피만 수정 가능합니다.");
                return;
            }

            var recipes = await _recipeManager.LoadRecipes();
            string SelectValue(string value)
            {
                return ValidateRecipeId(NormalizeRecipeIdInput(value), recipes, oldRecipeId);
            }

            var newRecipeId = requestName("레시피 이름 변경", "변경할 레시피 이름을 입력하세요.", oldRecipeId, SelectValue);
            if (newRecipeId is null)
            {
                _setStatusMessage("Recipe rename canceled.");
                return;
            }

            if (newRecipeId.Equals(oldRecipeId, StringComparison.OrdinalIgnoreCase))
            {
                _setStatusMessage("Recipe rename skipped. Name was not changed.");
                return;
            }

            var validationMessage = ValidateRecipeId(newRecipeId, recipes, oldRecipeId);
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                _setStatusMessage(validationMessage);
                return;
            }

            var recipeParameters = BuildRecipeParameters(oldRecipeId);
            validationMessage = ValidateRecipeWithZeroDefence(recipeParameters);
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                _setStatusMessage(validationMessage);
                return;
            }

            bool IsIsEdited(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.IsEdited;
            }

            try
            {
                var folders = await _recipeManager.ListCompanionFolders(oldRecipeId, newRecipeId);
                var selectedFolders = new List<string>();
                foreach (var folder in folders)
                {
                    var choice = confirmFolder("레시피 부가 폴더 이름 변경", $"{oldRecipeId} 레시피에 연결된 {folder.SourceName} 폴더도\n{folder.TargetName} 이름으로 함께 변경하시겠습니까?\n\n폴더 유지: 기존 이름으로 남기고 레시피만 변경합니다.\n취소: 레시피와 모든 폴더의 이름 변경을 취소합니다.");
                    if (choice is null)
                    {
                        _setStatusMessage("Recipe rename canceled.");
                        return; // No draft save or filesystem changes until every choice has completed.
                    }

                    if (choice.Value)
                        selectedFolders.Add(folder.SourceName);
                }

                if (AllManagedItems.Any(IsIsEdited))
                    await _recipeManager.SaveRecipe(new ST_RECIPE_DEFINITION(oldRecipeId, oldRecipeId, recipeParameters, []));
                await _recipeManager.RenameRecipe(oldRecipeId, newRecipeId, selectedFolders);
            }
            catch (Exception error)
            {
                _setStatusMessage("Recipe rename failed: " + error.Message);
                warn("레시피 이름 변경 실패", error.Message);
                return;
            }

            _selectRecipeIdSetter(newRecipeId);
            _viewRecipeIdSetter(newRecipeId);
            _settingManager.SetValue(EN_SETTING_TAB.Option, SelectRecipeSettingKey, newRecipeId);
            _selectedCategorySetter("ALL");
            _selectedGroup = "ALL";
            NotifyCommands();
            _setStatusMessage($"Recipe {oldRecipeId}.csv renamed to {newRecipeId}.csv and CSV verified.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
        }

        private async Task Create(
            Func<string, string, string, Func<string, string>, string?>? requestName = null, 
            Func<string, string, bool>? confirmFolder = null, 
            Action<string, string>? warn = null
        )
        {
            requestName ??= (title, message, initial, validate) => ShowRecipeNameDialog(title, message, initial, validate);
            confirmFolder ??= CRecipeConfirmDialog.ShowRecipeFolderCopy;
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            if (AllManagedItems.Count == 0)
            {
                _setStatusMessage("Recipe create skipped. No source recipe is loaded.");
                return;
            }

            var recipes = await _recipeManager.LoadRecipes();
            string SelectValue(string value)
            {
                return ValidateRecipeId(NormalizeRecipeIdInput(value), recipes);
            }

            var sourceRecipeId = GetRecipeIdFromParameter(SelectedRecipeFile);
            var recipeId = requestName("레시피 생성", "새 레시피 이름을 입력하세요.", "", SelectValue);
            if (recipeId is null)
            {
                _setStatusMessage("Recipe create canceled.");
                return;
            }

            var recipeNameValidationMessage = ValidateRecipeId(recipeId, recipes);
            if (!string.IsNullOrWhiteSpace(recipeNameValidationMessage))
            {
                _setStatusMessage(recipeNameValidationMessage);
                return;
            }

            var recipeParameters = BuildRecipeParameters(recipeId);
            var validationMessage = ValidateRecipeWithZeroDefence(recipeParameters);
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                _setStatusMessage(validationMessage);
                return;
            }

            try
            {
                var companions = await _recipeManager.ListCompanionFolders(sourceRecipeId, recipeId);
                var selectedFolders = companions.Where(folder => confirmFolder("레시피 부가 폴더 복사", $"{sourceRecipeId} 레시피의 {folder.SourceName} 폴더 내용도\n새 레시피 {recipeId}의 {folder.TargetName} 폴더로 복사하시겠습니까?\n\n복사 안 함을 선택하면 해당 폴더 없이 레시피를 생성합니다.")).Select(folder => folder.SourceName).ToArray();
                await _recipeManager.CreateRecipeCopy(new ST_RECIPE_DEFINITION(recipeId, recipeId, recipeParameters, []), sourceRecipeId, selectedFolders);
            }
            catch (Exception error)
            {
                _setStatusMessage("Recipe create failed: " + error.Message);
                warn("레시피 생성 실패", error.Message);
                return;
            }

            _viewRecipeIdSetter(recipeId);
            _selectedCategorySetter("ALL");
            _selectedGroup = "ALL";
            NotifyCommands();
            _setStatusMessage($"Recipe {recipeId}.csv created from current recipe and CSV verified.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
        }

        private async Task Delete(
            Func<string, bool>? confirmRecipe = null, 
            Func<string, string, bool>? confirmFolder = null, 
            Action<string, string>? warn = null, 
            Action<string, string>? completed = null
        )
        {
            confirmRecipe ??= ConfirmRecipeDelete;
            confirmFolder ??= CRecipeConfirmDialog.ShowRecipeFolderDelete;
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            completed ??= (title, message) => CRecipeConfirmDialog.ShowCompletion(title, message);
            var recipeId = GetRecipeIdFromParameter(SelectedRecipeFile);
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                _setStatusMessage("Recipe delete skipped. No recipe is selected.");
                return;
            }

            if (!CanDeleteViewRecipe())
            {
                _setStatusMessage("사용 중인 레시피는 삭제할 수 없습니다.");
                return;
            }

            if (!confirmRecipe(recipeId))
            {
                _setStatusMessage($"Recipe {recipeId}.csv delete canceled.");
                return;
            }

            try
            {
                var folders = await _recipeManager.ListCompanionFolders(recipeId, recipeId);
                var selectedFolders = folders.Where(folder => confirmFolder("레시피 부가 폴더 삭제", $"{recipeId} 레시피에 연결된 {folder.SourceName} 폴더와\n안의 파일도 함께 삭제하시겠습니까?\n삭제한 폴더와 파일은 되돌릴 수 없습니다.\n\n폴더 유지를 선택하면 레시피 파일만 삭제하고 이 폴더는 남겨둡니다.")).Select(folder => folder.SourceName).ToArray();
                await _recipeManager.DeleteRecipe(recipeId, selectedFolders);
            }
            catch (Exception error)
            {
                _setStatusMessage("Recipe delete failed: " + error.Message);
                warn("레시피 삭제 실패", error.Message);
                return;
            }

            _viewRecipeIdSetter(_selectRecipeIdProvider());
            _selectedCategorySetter("ALL");
            _selectedGroup = "ALL";
            NotifyCommands();
            _setStatusMessage($"Recipe {recipeId}.csv deleted.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
            completed("레시피 삭제 완료", $"{recipeId}.csv 레시피를 삭제하였습니다.");
        }

        private void NotifyCommands()
        {
            SelectCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            ModifyCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
        }

        private void Apply(
            IReadOnlyList<ST_DISPLAY_ITEM> recipeList, 
            IReadOnlyList<ST_DISPLAY_ITEM> parameters, 
            IReadOnlyList<ST_DISPLAY_ITEM> history, 
            IReadOnlyList<ST_DISPLAY_ITEM> actions, 
            string selectedRecipeFile, 
            string selectRecipeFile, 
            bool isViewRecipeSelected, 
            IReadOnlyList<ST_RECIPE_FILE> recipeFiles, 
            IReadOnlyList<ST_RECIPE_CATEGORY_TAB> itemTabs, 
            IReadOnlyList<ST_RECIPE_GROUP_TAB> groupTabs, 
            IReadOnlyList<ST_RECIPE_CELL_OVERVIEW_ROW> cellOverviewRows, 
            bool isCellCategory, 
            ST_RECIPE_LAYOUT_PREVIEW? layoutPreview, 
            bool isHeadCategory, 
            ST_RECIPE_HEAD_PREVIEW? headPreview, 
            IReadOnlyList<ST_SYSTEM_PARAMETER> headPreviewSettings, 
            string selectedGroup, 
            IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> allManagedItems, 
            IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, 
            IReadOnlyList<ST_RECIPE_HISTORY_ROW> changeHistory, 
            IReadOnlyList<ST_RECIPE_STATE_ROW> stateRows
        )
        {
            RecipeList = recipeList;
            Parameters = parameters;
            History = history;
            Actions = actions;
            SelectedRecipeFile = selectedRecipeFile;
            SelectRecipeFile = selectRecipeFile;
            IsViewRecipeSelected = isViewRecipeSelected;
            RecipeFiles = recipeFiles;
            ItemTabs = itemTabs;
            GroupTabs = groupTabs;
            CellOverviewRows = cellOverviewRows;
            GlassSizeXItem = FindManagedItem(allManagedItems, "GLASS_SIZE_X");
            GlassSizeYItem = FindManagedItem(allManagedItems, "GLASS_SIZE_Y");
            MaxCellNumberItem = FindManagedItem(allManagedItems, "MAX_CELL_NUMBER");
            PixelSizeItem = FindManagedItem(allManagedItems, "PIXEL_SIZE");
            PixelCountXItem = FindManagedItem(allManagedItems, "MODEL1_NUM_OF_PIXEL_X");
            PixelCountYItem = FindManagedItem(allManagedItems, "MODEL1_NUM_OF_PIXEL_Y");
            ProcessPitchItem = FindManagedItem(allManagedItems, "MODEL1_PITCH");
            ChessItem = FindManagedItem(allManagedItems, "MODEL1_CHESS");
            foreach (var chessItem in allManagedItems.Where(item => IsModelChessKey(item.Key)))
            {
                var normalizedChess = NormalizeChessValue(chessItem.Value);
                chessItem.Value = normalizedChess;
                _lastValidChessValues[chessItem.Key] = normalizedChess;
            }

            SplitBeamCountItem = FindManagedItem(allManagedItems, "SPLITED_BEAM_COUNT");
            CellModelTypeCountItem = FindManagedItem(allManagedItems, "CELL_MODEL_TYPE_COUNT");
            ShotGroupPatternSummary = BuildShotGroupPatternSummary(allManagedItems);
            AkMarginXItem = FindManagedItem(allManagedItems, "AK_MARGIN_X");
            AkMarginYItem = FindManagedItem(allManagedItems, "AK_MARGIN_Y");
            EscEdgeToGlassEdgeItem = FindManagedItem(allManagedItems, "ESC_EDGE_TO_GLASS_EDGE");
            GlassCenterToScannerCenterItem = FindManagedItem(allManagedItems, "GLASS_CENTER_TO_SCANNER_CENTER");
            DistortionKeyItems = BuildDistortionKeyItems(allManagedItems, headPreviewSettings);
            IsCellCategory = isCellCategory;
            IsHeadCategory = isHeadCategory;
            CellPreviewImage = layoutPreview?.CellImage;
            CellPreviewLabels = layoutPreview?.CellLabels ?? [];
            CellPreviewSummaryText = layoutPreview?.SummaryText ?? "";
            HeadPreviewImage = headPreview?.Image;
            HeadPreviewLabels = headPreview?.Labels ?? [];
            HeadPreviewSummaryText = headPreview?.SummaryText ?? "";
            _headPreviewSettings = headPreviewSettings;
            SelectedGroup = selectedGroup;
            TrackPreviewItems(allManagedItems);
            AllManagedItems = allManagedItems;
            ManagedItems = managedItems;
            ChangeHistory = changeHistory;
            StateRows = stateRows;
            OnPropertyChanged(nameof(ViewRecipeFile));
            OnPropertyChanged(nameof(SelectRecipeFile));
            OnPropertyChanged(nameof(IsViewRecipeSelected));
            OnPropertyChanged(nameof(RecipeSelectionSummaryText));
            OnPropertyChanged(nameof(CellModelTypeCountItem));
            OnPropertyChanged(nameof(ShotGroupPatternSummary));
            OnPropertyChanged(nameof(IsZeroDefenceCategory));
            NotifyCommands();
        }

        private void TrackPreviewItems(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> items)
        {
            foreach (var item in _previewTrackedItems)
            {
                item.ValueChanged -= OnPreviewItemValueChanged;
            }

            _previewTrackedItems = items;
            foreach (var item in _previewTrackedItems)
            {
                item.ValueChanged += OnPreviewItemValueChanged;
            }
        }

        private void OnPreviewItemValueChanged(object? sender, EventArgs e)
        {
            if (_isBatchUpdatingCellPlacement)
            {
                return;
            }

            if (_isRevertingChessValue)
            {
                return;
            }

            if (IsHeadCategory)
            {
                ScheduleHeadPreviewRefresh();
                return;
            }

            if (!IsCellCategory)
            {
                return;
            }

            if (sender is ST_RECIPE_PARAMETER_EDITOR_ITEM shotGroupItem && IsShotGroupOverrideKey(shotGroupItem.Key))
            {
                return;
            }

            if (sender is ST_RECIPE_PARAMETER_EDITOR_ITEM item && IsModelChessKey(item.Key) && !TryAcceptChessValue(item))
            {
                return;
            }

            if (sender is ST_RECIPE_PARAMETER_EDITOR_ITEM item2 && item2.Key.Equals("MAX_CELL_NUMBER", StringComparison.OrdinalIgnoreCase))
            {
                ScheduleCellStructureRefresh();
            }
            else
            {
                SchedulePreviewRefresh();
            }
        }

        private void RefreshCellPreviewFromManagedItems()
        {
            var cells = BuildCells(AllManagedItems, GetCellCount(AllManagedItems));
            _recipeCells = cells;
            CellOverviewRows = BuildCellOverviewRows(cells, AllManagedItems);
            var preview = BuildLayoutPreview(AllManagedItems, cells, _selectedCellNo);
            CellPreviewImage = preview.CellImage;
            CellPreviewLabels = preview.CellLabels;
            CellPreviewSummaryText = preview.SummaryText;
            ShotGroupPatternSummary = BuildShotGroupPatternSummary(AllManagedItems);
            var selectedCell = cells.FirstOrDefault(cell => cell.CellNo == _selectedCellNo);
            UpdateShotGroupRows(_isCellDetailVisible && selectedCell is not null ? BuildShotGroupRows(AllManagedItems, selectedCell) : []);
            OnPropertyChanged(nameof(CellOverviewRows));
            OnPropertyChanged(nameof(CellPreviewImage));
            OnPropertyChanged(nameof(CellPreviewLabels));
            OnPropertyChanged(nameof(CellPreviewSummaryText));
            OnPropertyChanged(nameof(ShotGroupPatternSummary));
        }

        private bool TryAcceptChessValue(ST_RECIPE_PARAMETER_EDITOR_ITEM item)
        {
            var requestedValue = NormalizeChessValue(item.Value);
            item.Value = requestedValue;
            if (!TryFindChessEmptyRow(AllManagedItems, item.Key, requestedValue, out var groupCountX, out var groupCountY, out var emptyRow))
            {
                _lastValidChessValues[item.Key] = requestedValue;
                return true;
            }

            CRecipeConfirmDialog.ShowWarning("CHESS 설정 확인", $"CHESS {requestedValue} 적용 시 ROW {emptyRow:N0} 라인에 가공 SHOT이 없습니다.\n현재 SHOT MAP은 {groupCountX:N0} x {groupCountY:N0} 입니다.\n라인마다 최소 1개 이상의 SHOT이 남도록 더 작은 CHESS 값을 선택하세요.");
            _isRevertingChessValue = true;
            try
            {
                item.Value = _lastValidChessValues.GetValueOrDefault(item.Key, "1");
            }
            finally
            {
                _isRevertingChessValue = false;
            }

            return false;
        }

        private static bool TryFindChessEmptyRow(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, string chessKey, string chessValue, out int groupCountX, out int groupCountY, out int emptyRow)
        {
            groupCountX = 0;
            groupCountY = 0;
            emptyRow = 0;
            var chess = Math.Clamp(ReadIntText(chessValue, 1), 1, 4);
            if (chess <= 1)
            {
                return false;
            }

            var modelType = TryGetModelTypeFromModelKey(chessKey);
            var chessType = Math.Clamp(ReadManagedInt(managedItems, $"MODEL{modelType}_CHESS_TYPE", 1), 0, 1);
            if (chessType == 0)
            {
                return false;
            }

            var modelPattern = ReadManagedModelPattern(managedItems, modelType);
            var result = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(
                1, 
                0.0, 
                0.0, 
                0.0, 
                ReadManagedDouble(managedItems, "PIXEL_SIZE", 0.09), 
                modelPattern.PixelCountX, 
                modelPattern.PixelCountY, 
                modelPattern.ProcessPitch, 
                ReadManagedInt(managedItems, "SPLITED_BEAM_COUNT", 1)
            ));
            if (!result.IsValid || result.GroupCountX <= 0 || result.GroupCountY <= 0)
            {
                return false;
            }

            groupCountX = result.GroupCountX;
            groupCountY = result.GroupCountY;
            for (var zeroBasedRow = 0; zeroBasedRow < groupCountY; zeroBasedRow++)
            {
                var hasProcessShot = false;
                for (var zeroBasedColumn = 0; zeroBasedColumn < groupCountX; zeroBasedColumn++)
                {
                    if (PositiveModulo(zeroBasedColumn - zeroBasedRow, chess) != 0)
                    {
                        continue;
                    }

                    hasProcessShot = true;
                    break;
                }

                if (hasProcessShot)
                {
                    continue;
                }

                emptyRow = zeroBasedRow + 1;
                return true;
            }

            return false;
        }

        private static string NormalizeChessValue(string? value)
        {
            return Math.Clamp(ReadIntText(value, 1), 1, 4).ToString(CultureInfo.InvariantCulture);
        }

        private async void ScheduleCellStructureRefresh()
        {
            _previewRefreshCancellation?.Cancel();
            _previewRefreshCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _previewRefreshCancellation = cancellation;
            try
            {
                await Task.Delay(250, cancellation.Token);
                await _refreshCurrentScreen();
            }
            catch (OperationCanceledException)
            {
            // A newer edit restarted the debounce timer.
            }
        }

        private async void SchedulePreviewRefresh()
        {
            _previewRefreshCancellation?.Cancel();
            _previewRefreshCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _previewRefreshCancellation = cancellation;
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_CELL_VIEW_MODEL cell)
            {
                return cell.CellNo == _selectedCellNo;
            }

            try
            {
                await Task.Delay(400, cancellation.Token);
                var cells = BuildCells(AllManagedItems, GetCellCount(AllManagedItems));
                _recipeCells = cells;
                CellOverviewRows = BuildCellOverviewRows(cells, AllManagedItems);
                var preview = BuildLayoutPreview(AllManagedItems, cells, _selectedCellNo);
                CellPreviewImage = preview.CellImage;
                CellPreviewLabels = preview.CellLabels;
                CellPreviewSummaryText = preview.SummaryText;
                ShotGroupPatternSummary = BuildShotGroupPatternSummary(AllManagedItems);
                UpdateShotGroupRows(_isCellDetailVisible ? BuildShotGroupRows(AllManagedItems, cells.First(MatchesItem)) : []);
                OnPropertyChanged(nameof(CellPreviewImage));
                OnPropertyChanged(nameof(CellPreviewLabels));
                OnPropertyChanged(nameof(CellPreviewSummaryText));
                OnPropertyChanged(nameof(CellOverviewRows));
                OnPropertyChanged(nameof(ShotGroupPatternSummary));
            }
            catch (OperationCanceledException)
            {
            // A newer edit restarted the debounce timer.
            }
        }

        private async void ScheduleHeadPreviewRefresh()
        {
            _previewRefreshCancellation?.Cancel();
            _previewRefreshCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _previewRefreshCancellation = cancellation;
            try
            {
                await Task.Delay(400, cancellation.Token);
                var preview = BuildHeadPreview(AllManagedItems, _headPreviewSettings, SelectedGroup);
                HeadPreviewImage = preview.Image;
                HeadPreviewLabels = preview.Labels;
                HeadPreviewSummaryText = preview.SummaryText;
                OnPropertyChanged(nameof(HeadPreviewImage));
                OnPropertyChanged(nameof(HeadPreviewLabels));
                OnPropertyChanged(nameof(HeadPreviewSummaryText));
            }
            catch (OperationCanceledException)
            {
            // A newer edit restarted the debounce timer.
            }
        }

        private static ST_RECIPE_DEFINITION? GetSelectedRecipe(IReadOnlyList<ST_RECIPE_DEFINITION> recipes, string selectedRecipeId)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Id.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(selectedRecipeId))
            {
                var selectedRecipe = recipes.FirstOrDefault(MatchesItem);
                if (selectedRecipe is not null)
                {
                    return selectedRecipe;
                }
            }

            return recipes.FirstOrDefault();
        }

        private static IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> GetEditItems(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> loadedItems, CMenuRecipe? editScreen, string selectedRecipeFile)
        {
            return editScreen is not null && editScreen.SelectedRecipeFile.Equals(selectedRecipeFile, StringComparison.OrdinalIgnoreCase) && editScreen.AllManagedItems.Count > 0 ? editScreen.AllManagedItems : loadedItems;
        }

        private bool CanSelectViewRecipe()
        {
            return !string.IsNullOrWhiteSpace(SelectedRecipeFile) && !IsViewRecipeSelected;
        }

        private bool CanEditViewRecipe()
        {
            return IsViewRecipeSelected && !string.IsNullOrWhiteSpace(SelectedRecipeFile) && AllManagedItems.Count > 0;
        }

        private bool CanDeleteViewRecipe()
        {
            return !string.IsNullOrWhiteSpace(SelectedRecipeFile) && !IsViewRecipeSelected;
        }

        private bool HasEditedItems()
        {
            return AllManagedItems.Any(item => item.IsEdited);
        }

        private static IReadOnlyList<ST_DISPLAY_ITEM> BuildActions()
        {
            return[new("Select", "Apply"), new("Create", "Ready"), new("Save", "Ready"), new("Delete", "Ready"), new("Modify", "Rename")];
        }

        private static string GetRecipeFileName(ST_RECIPE_DEFINITION? recipe)
        {
            return recipe is null ? "" : $"{recipe.Id}.csv";
        }

        private void SyncRecipeFilesPage(IReadOnlyList<ST_RECIPE_DEFINITION> recipes, string selectedRecipeFile)
        {
            _recipeFilesPageCount = GetRecipeFilesPageCount(recipes.Count);
            if (!_lastRecipeListSelectedFile.Equals(selectedRecipeFile, StringComparison.OrdinalIgnoreCase))
            {
                var selectedRecipeIndex = FindRecipeFileIndex(recipes, selectedRecipeFile);
                if (selectedRecipeIndex >= 0)
                {
                    _recipeFilesPageIndex = selectedRecipeIndex / RecipeFilesPageSize;
                }
            }

            if (_recipeFilesPageCount <= 0)
            {
                _recipeFilesPageIndex = 0;
                RecipeFilesPageText = "PAGE 0 / 0";
            }
            else
            {
                if (_recipeFilesPageIndex < 0)
                {
                    _recipeFilesPageIndex = 0;
                }

                if (_recipeFilesPageIndex >= _recipeFilesPageCount)
                {
                    _recipeFilesPageIndex = _recipeFilesPageCount - 1;
                }

                RecipeFilesPageText = $"PAGE {_recipeFilesPageIndex + 1} / {_recipeFilesPageCount}";
            }

            _lastRecipeListSelectedFile = selectedRecipeFile;
            NotifyRecipeFilesPageCommands();
        }

        private static int GetRecipeFilesPageCount(int recipeFileCount)
        {
            if (recipeFileCount <= 0)
            {
                return 0;
            }

            return ((recipeFileCount - 1) / RecipeFilesPageSize) + 1;
        }

        private static int FindRecipeFileIndex(IReadOnlyList<ST_RECIPE_DEFINITION> recipes, string selectedRecipeFile)
        {
            for (var index = 0; index < recipes.Count; index++)
            {
                if (GetRecipeFileName(recipes[index]).Equals(selectedRecipeFile, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static IReadOnlyList<ST_RECIPE_FILE> BuildRecipeFiles(IReadOnlyList<ST_RECIPE_DEFINITION> recipes, ST_RECIPE_DEFINITION? viewRecipe, ST_RECIPE_DEFINITION? selectRecipe, int pageIndex)
        {
            var startIndex = Math.Max(0, pageIndex) * RecipeFilesPageSize;
            if (startIndex >= recipes.Count)
            {
                return[];
            }

            var itemCount = Math.Min(RecipeFilesPageSize, recipes.Count - startIndex);
            var recipeFiles = new List<ST_RECIPE_FILE>(itemCount);
            for (var index = 0; index < itemCount; index++)
            {
                var recipeIndex = startIndex + index;
                var recipe = recipes[recipeIndex];
                recipeFiles.Add(new ST_RECIPE_FILE(
                    (recipeIndex + 1).ToString("00"), 
                    GetRecipeFileName(recipe), 
                    viewRecipe is not null && recipe.Id.Equals(viewRecipe.Id, StringComparison.OrdinalIgnoreCase), 
                    selectRecipe is not null && recipe.Id.Equals(selectRecipe.Id, StringComparison.OrdinalIgnoreCase)
                ));
            }

            return recipeFiles;
        }

        private static IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> BuildManagedItems(ST_RECIPE_DEFINITION? recipe)
        {
            if (recipe is null)
            {
                return[];
            }

            bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Use && (parameter.Show || CellParameterKeys.Contains(parameter.Key, StringComparer.OrdinalIgnoreCase));
            }

            global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM BuildRecipe(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                var category = NormalizeRecipeText(parameter.Tab, "COMMON");
                var group = NormalizeRecipeText(parameter.Group, category);
                return new ST_RECIPE_PARAMETER_EDITOR_ITEM(
                    category, 
                    group, 
                    parameter.Name, 
                    parameter.Value, 
                    NormalizeUnit(parameter.Unit), 
                    parameter.Description, 
                    GetValueState(parameter), 
                    parameter.Key, 
                    group, 
                    parameter.DataType, 
                    parameter.ChangeLimit, 
                    parameter.Min, 
                    parameter.Max
                );
            }

            return recipe.Parameters.Where(ShouldIncludeItem).Select((parameter, index) => new { Parameter = parameter, Index = index }).OrderBy(item => item.Parameter.DisplayOrder <= 0 ? int.MaxValue : item.Parameter.DisplayOrder).ThenBy(item => item.Index).Select(item => BuildRecipe(item.Parameter)).ToArray();
        }

        private static IReadOnlyList<string> BuildCategories(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            string SelectCategory(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Category;
            }

            bool ShouldIncludeItem(string category)
            {
                return !string.IsNullOrWhiteSpace(category);
            }

            var categories = managedItems.Select(SelectCategory).Where(ShouldIncludeItem).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var result = categories.ToList();
            bool MatchesItem(string category)
            {
                return category.Equals("CELL", StringComparison.OrdinalIgnoreCase);
            }

            if (!result.Any(MatchesItem))
            {
                result.Add("CELL");
            }

            result.RemoveAll(category => category.Equals("ZERO_DEFENCE", StringComparison.OrdinalIgnoreCase));
            var headIndex = result.FindIndex(category => category.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
            result.Insert(headIndex >= 0 ? headIndex + 1 : result.Count, "ZERO_DEFENCE");
            return result;
        }

        private static int GetCellCount(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return string.Equals(item.Key, "MAX_CELL_NUMBER", StringComparison.OrdinalIgnoreCase);
            }

            var countItem = managedItems.FirstOrDefault(MatchesItem);
            var minimum = countItem is not null && countItem.Max > countItem.Min ? Math.Max(CCellPatternCalculator.MinCellCount, (int)Math.Ceiling(countItem.Min)) : CCellPatternCalculator.MinCellCount;
            var maximum = countItem is not null && countItem.Max >= minimum ? (int)Math.Floor(countItem.Max) : (int? )null;
            if (countItem is null || !int.TryParse(countItem.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return minimum;
            }

            return maximum.HasValue ? Math.Clamp(count, minimum, maximum.Value) : Math.Max(minimum, count);
        }

        private static IReadOnlyList<ST_RECIPE_CELL_VIEW_MODEL> BuildCells(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, int cellCount)
        {
            var akMarginX = ReadManagedDouble(managedItems, "AK_MARGIN_X", 55.0);
            var akMarginY = ReadManagedDouble(managedItems, "AK_MARGIN_Y", 45.0);
            var pixelSize = ReadManagedDouble(managedItems, "PIXEL_SIZE", 0.09);
            var splitBeamCount = ReadManagedInt(managedItems, "SPLITED_BEAM_COUNT", 1);
            var cellModelTypeCount = GetCellModelTypeCount(managedItems);
            var itemsByCellNo = managedItems.Select(item => new { Item = item, CellNo = TryGetCellNo(item.Key) }).Where(item => item.CellNo > 0).GroupBy(item => item.CellNo).ToDictionary(group => group.Key, group => (IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM>)group.Select(item => item.Item).ToArray());
            int SelectSortKey(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return GetCellItemGroupOrder(item.SourceGroup);
            }

            global::Drilling.UI.Menu.Menus.ST_RECIPE_CELL_VIEW_MODEL BuildRecipe(int cellNo)
            {
                var items = (itemsByCellNo.GetValueOrDefault(cellNo) ?? []).OrderBy(SelectSortKey).ToArray();
                var firstPixelX = ReadCellDouble(items, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X");
                var firstPixelY = ReadCellDouble(items, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y");
                var rotation = ReadCellDouble(items, $"CELL{cellNo}_ROTATION");
                var modelType = Math.Clamp(ReadCellInt(items, $"CELL{cellNo}_MODEL_TYPE", 1), 1, cellModelTypeCount);
                var modelPattern = ReadManagedModelPattern(managedItems, modelType);
                var result = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(cellNo, firstPixelX, firstPixelY, rotation, pixelSize, modelPattern.PixelCountX, modelPattern.PixelCountY, modelPattern.ProcessPitch, splitBeamCount, akMarginX, akMarginY), includeShotCenters: false);
                return new ST_RECIPE_CELL_VIEW_MODEL(cellNo, items, result, akMarginX + firstPixelX, akMarginY + firstPixelY, rotation, pixelSize, modelType, modelPattern.PixelCountX, modelPattern.PixelCountY);
            }

            return Enumerable.Range(1, cellCount).Select(BuildRecipe).ToArray();
        }

        private static int GetCellItemGroupOrder(string group)
        {
            if (group.Equals("CELL", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (group.Equals("SHOT_GROUP", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private static double ReadCellDouble(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> items, string key)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = items.FirstOrDefault(MatchesItem)?.Value;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.0;
        }

        private static int ReadCellInt(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> items, string key, int defaultValue)
        {
            bool MatchesCellParameterKey(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = items.FirstOrDefault(MatchesCellParameterKey)?.Value;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static int TryGetCellNo(string key)
        {
            if (!key.StartsWith("CELL", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var separatorIndex = key.IndexOf('_', 4);
            return separatorIndex > 4 && int.TryParse(key.AsSpan(4, separatorIndex - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var cellNo) ? cellNo : 0;
        }

        private static int TryGetModelTypeFromModelKey(string key)
        {
            if (!key.StartsWith("MODEL", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var separatorIndex = key.IndexOf('_', 5);
            return separatorIndex > 5 && int.TryParse(key.AsSpan(5, separatorIndex - 5), NumberStyles.None, CultureInfo.InvariantCulture, out var modelType) ? Math.Clamp(modelType, 1, MaximumCellModelTypeCount) : 0;
        }

        private static bool IsModelPitchKey(string key)
        {
            return TryGetModelTypeFromModelKey(key) > 0 && key.EndsWith("_PITCH", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelChessKey(string key)
        {
            return TryGetModelTypeFromModelKey(key) > 0 && key.EndsWith("_CHESS", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelChessTypeKey(string key)
        {
            return TryGetModelTypeFromModelKey(key) > 0 && key.EndsWith("_CHESS_TYPE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelPatternKey(string key)
        {
            return TryGetModelTypeFromModelKey(key) > 0 && (key.Contains("_NUM_OF_PIXEL_", StringComparison.OrdinalIgnoreCase) || IsModelPitchKey(key) || IsModelChessKey(key) || IsModelChessTypeKey(key));
        }

        private static bool IsMovedToCellTab(ST_RECIPE_PARAMETER_EDITOR_ITEM item)
        {
            return TryGetCellNo(item.Key) > 0 || CellParameterKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase) || PatternParameterKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase) || GlassSettingKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase);
        }

        private static ST_RECIPE_PARAMETER_EDITOR_ITEM? FindManagedItem(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, string key)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            return managedItems.FirstOrDefault(MatchesItem);
        }

        private static IReadOnlyList<ST_RECIPE_DISTORTION_KEY_ITEM> BuildDistortionKeyItems(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var height = ReadManagedDouble(managedItems, "GLASS_SIZE_Y", 0);
            var margin = ReadManagedDouble(managedItems, "AK_MARGIN_Y", 0);
            return Enumerable.Range(0, 3).Select(index =>
            {
                var text = settings.FirstOrDefault(p => p.Key.Equals(CAlignKeyGeometry.StagePositionKey(index), StringComparison.OrdinalIgnoreCase))?.Value;
                var stage = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value.ToString("0.000", CultureInfo.InvariantCulture) : "-";
                return new ST_RECIPE_DISTORTION_KEY_ITEM(index, stage);
            }).ToArray();
        }

        private static IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> EnsureCellPlacementItems(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, int cellCount)
        {
            var result = managedItems.ToList();
            var existingKeys = new HashSet<string>(managedItems.Select(item => item.Key), StringComparer.OrdinalIgnoreCase);
            global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM? SelectItem(string key)
            {
                bool ShouldIncludeItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
                {
                    return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
                }

                return managedItems.FirstOrDefault(ShouldIncludeItem);
            }

            bool ShouldIncludeItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM? item)
            {
                return item is not null;
            }

            string SelectKey(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key;
            }

            var templates = CellParameterKeys.Select(SelectItem).Where(ShouldIncludeItem).Cast<ST_RECIPE_PARAMETER_EDITOR_ITEM>().ToDictionary(SelectKey, StringComparer.OrdinalIgnoreCase);
            foreach (var cellNo in Enumerable.Range(1, cellCount))
            {
                foreach (var parameterKey in CellParameterKeys)
                {
                    var scopedKey = $"CELL{cellNo}_{GetCellScopedParameterName(parameterKey)}";
                    if (existingKeys.Contains(scopedKey) || !templates.TryGetValue(parameterKey, out var template))
                    {
                        continue;
                    }

                    result.Add(new ST_RECIPE_PARAMETER_EDITOR_ITEM(
                        "CELL", 
                        GetCellParameterGroup(parameterKey), 
                        template.Item, 
                        template.Value, 
                        template.Unit, 
                        template.Description, 
                        template.ValueState, 
                        scopedKey, 
                        GetCellParameterGroup(parameterKey), 
                        template.DataType, 
                        template.ChangeLimit, 
                        template.Min, 
                        template.Max
                    ));
                    existingKeys.Add(scopedKey);
                }
            }

            return result;
        }

        private static string GetCellParameterGroup(string parameterKey)
        {
            return "CELL";
        }

        private static string GetCellScopedParameterName(string parameterKey)
        {
            return parameterKey.StartsWith("CELL_", StringComparison.OrdinalIgnoreCase) ? parameterKey[5..] : parameterKey;
        }

        private static bool IsCellPlacementParameter(string parameterName)
        {
            return parameterName.Equals("ALIGN_TO_1ST_PIXEL_X", StringComparison.OrdinalIgnoreCase) || parameterName.Equals("ALIGN_TO_1ST_PIXEL_Y", StringComparison.OrdinalIgnoreCase) || parameterName.Equals("ROTATION", StringComparison.OrdinalIgnoreCase) || parameterName.Equals("MODEL_TYPE", StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<ST_RECIPE_CELL_OVERVIEW_ROW> BuildCellOverviewRows(IReadOnlyList<ST_RECIPE_CELL_VIEW_MODEL> cells, IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            var modelOptions = BuildCellModelOptions(managedItems);
            global::Drilling.UI.Menu.Menus.ST_RECIPE_CELL_OVERVIEW_ROW BuildRecipe(global::Drilling.UI.Menu.Menus.ST_RECIPE_CELL_VIEW_MODEL cell)
            {
                return new ST_RECIPE_CELL_OVERVIEW_ROW(cell, cell.CellNo == _selectedCellNo, modelOptions);
            }

            return cells.Select(BuildRecipe).ToArray();
        }

        private static IReadOnlyList<string> BuildCellModelOptions(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            var count = GetCellModelTypeCount(managedItems);
            return Enumerable.Range(1, count).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
        }

        private static int GetCellModelTypeCount(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            return Math.Clamp(ReadManagedInt(managedItems, "CELL_MODEL_TYPE_COUNT", MaximumCellModelTypeCount), 1, MaximumCellModelTypeCount);
        }

        private void RefreshCellPatternViewer()
        {
            var cell = GetSelectedRecipeCell();
            var splitBeamCount = ReadManagedInt(AllManagedItems, "SPLITED_BEAM_COUNT", 1);
            var maskingAreas = cell is null ? [] : BuildMaskingAreas(AllManagedItems, cell.ModelType);
            var edgeMaskingArea = cell is null ? null : BuildEdgeMaskingArea(AllManagedItems, cell.ModelType, cell.PatternLayout);
            CellShotMapImage = cell is null ? null : CRecipeCellPatternPreviewDrawing.CreateCellMap(cell, maskingAreas, edgeMaskingArea, ShotGroupRows, SelectedShotGroup);
            SelectedShotDetailImage = cell is null || SelectedShotGroup is null ? null : CRecipeCellPatternPreviewDrawing.CreateShotDetail(cell.PatternLayout.GroupPitchMillimeters, splitBeamCount, SelectedShotGroup.MaskedBeamNumbers, SelectedShotGroup.IsMasked);
            OnPropertyChanged(nameof(CellShotMapImage));
            OnPropertyChanged(nameof(SelectedShotDetailImage));
            OnPropertyChanged(nameof(SelectedCellShotGroupTitle));
            OnPropertyChanged(nameof(IsSelectedCellShotMapLandscape));
            OnPropertyChanged(nameof(CellShotMapPanelHeight));
            OnPropertyChanged(nameof(CellShotMapSurfaceWidth));
            OnPropertyChanged(nameof(CellShotMapSurfaceHeight));
            OnPropertyChanged(nameof(CellShotMapGridColumn));
            OnPropertyChanged(nameof(CellShotMapGridColumnSpan));
            OnPropertyChanged(nameof(CellShotMapGridRow));
            OnPropertyChanged(nameof(CellShotMapGridRowSpan));
            OnPropertyChanged(nameof(CellShotDetailGridColumn));
            OnPropertyChanged(nameof(CellShotDetailGridColumnSpan));
            OnPropertyChanged(nameof(CellShotDetailGridRow));
            OnPropertyChanged(nameof(CellShotDetailGridRowSpan));
            OnPropertyChanged(nameof(CellShotDetailVerticalGap));
            OnPropertyChanged(nameof(SelectedShotDetailGridColumn));
            OnPropertyChanged(nameof(SelectedShotDetailGridColumnSpan));
            OnPropertyChanged(nameof(SelectedShotDetailGridRow));
            OnPropertyChanged(nameof(ShotInformationGridColumn));
            OnPropertyChanged(nameof(ShotInformationGridColumnSpan));
            OnPropertyChanged(nameof(ShotInformationGridRow));
            OnPropertyChanged(nameof(SelectedCellExcludedTargetText));
            OnPropertyChanged(nameof(SelectedCellHasExcludedTargets));
            OnPropertyChanged(nameof(SelectedShotGroupGridText));
            OnPropertyChanged(nameof(SelectedShotGroupCenterText));
            OnPropertyChanged(nameof(SelectedShotGroupBeamText));
            OnPropertyChanged(nameof(SelectedShotGroupPixelWindowText));
            OnPropertyChanged(nameof(SelectedShotGroupPitchText));
            OnPropertyChanged(nameof(SelectedShotGroupStateText));
        }

        private ST_RECIPE_CELL_VIEW_MODEL? GetSelectedRecipeCell()
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_CELL_VIEW_MODEL cell)
            {
                return cell.CellNo == _selectedCellNo;
            }

            return _recipeCells.FirstOrDefault(MatchesItem);
        }

        private ST_RECIPE_LAYOUT_PREVIEW BuildLayoutPreview(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, IReadOnlyList<ST_RECIPE_CELL_VIEW_MODEL> cells, int selectedCellNo, IReadOnlyList<ST_SYSTEM_PARAMETER>? settings = null)
        {
            const double canvasWidth = 860.0;
            const double canvasHeight = 430.0;
            const double maxLeft = 44.0;
            const double maxTop = 50.0;
            const double maxWidth = 772.0;
            const double maxHeight = 340.0;
            var glassWidth = ReadManagedDouble(managedItems, "GLASS_SIZE_X", 500.0);
            var glassHeight = ReadManagedDouble(managedItems, "GLASS_SIZE_Y", 300.0);
            var akMarginX = ReadManagedDouble(managedItems, "AK_MARGIN_X", 55.0);
            var akMarginY = ReadManagedDouble(managedItems, "AK_MARGIN_Y", 45.0);
            var pixelSize = ReadManagedDouble(managedItems, "PIXEL_SIZE", 0.09);
            double? SelectValue(string key)
            {
                var text = (settings ?? _headPreviewSettings).FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
            }

            var distortionKeys = CCellPreviewDrawing.CreateDistortionKeyPreviews(glassWidth, glassHeight, akMarginX, akMarginY, SelectValue);
            if (glassWidth <= 0 || glassHeight <= 0)
            {
                return new ST_RECIPE_LAYOUT_PREVIEW(null, [], "Glass size is not valid.");
            }

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
            var expandedCellCount = 0;
            using (var context = drawing.Open())
            {
                // Keep the DrawingImage coordinate space identical to Main's 860x430 Canvas.
                // Keep the fixed preview coordinate space even when only shot groups are visible.
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, canvasWidth, canvasHeight));
                context.PushClip(new RectangleGeometry(frameBounds));
                foreach (var cell in cells)
                {
                    if (!cell.PatternLayout.IsValid)
                    {
                        invalidCellCount++;
                    }

                    totalShotGroupCount += Math.Max(0L, (long)cell.PatternLayout.GroupCountX * cell.PatternLayout.GroupCountY);
                    var boundary = BuildCellBoundaryGeometry(cell, frameLeft, frameTop, scale, pixelSize, cell.PixelCountX, cell.PixelCountY, akMarginX, akMarginY);
                    var boundaryBounds = boundary?.Bounds ?? Rect.Empty;
                    var displayBounds = boundaryBounds.IsEmpty ? boundaryBounds : ExpandPreviewBounds(boundaryBounds, MinimumLayoutPatternWidth, MinimumLayoutPatternHeight, frameBounds);
                    var useDisplayBounds = ShouldUseExpandedPreviewBounds(boundaryBounds, displayBounds);
                    if (useDisplayBounds)
                    {
                        expandedCellCount++;
                    }

                    var isSelectedCell = selectedCellNo > 0 && cell.CellNo == selectedCellNo;
                    var cellBrush = CMenuMain.CreateHeadBrush(((cell.CellNo - 1) % 8) + 1, (byte)(selectedCellNo <= 0 || isSelectedCell ? 230 : 105));
                    if (boundary is not null)
                    {
                        var boundaryBrush = CMenuMain.CreateHeadBrush(((cell.CellNo - 1) % 8) + 1, (byte)(isSelectedCell ? 255 : 150));
                        var boundaryPen = new Pen(boundaryBrush, isSelectedCell ? 2.4 : 1.2);
                        boundaryPen.Freeze();
                        var labelBounds = useDisplayBounds ? displayBounds : boundary.Bounds;
                        context.DrawGeometry(cellBrush, null, boundary);
                        drawnCellBoundaries.Add((boundary, boundaryPen));
                        var label = CCellPreviewDrawing.CreateCellLabel(cell.CellNo, labelBounds, canvasWidth, canvasHeight, isSelectedCell);
                        if (label is not null)
                        {
                            cellLabels.Add(label);
                        }
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
            global::Drilling.UI.Menu.Menus.ST_CELL_PREVIEW_LABEL SelectItem(global::Drilling.UI.Menu.Menus.ST_CELL_PREVIEW_LABEL label)
            {
                return label with
                {
                    CanvasCenterX = label.CanvasCenterX - frameLeft + paddingX,
                    CanvasCenterY = label.CanvasCenterY - frameTop + paddingY,
                    DesignWidth = cellRect.Width,
                    DesignHeight = cellRect.Height
                };
            }

            var translatedCellLabels = cellLabels.Select(SelectItem).ToArray();
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
                DrawPreviewAxisIndicator(cellContext, glassRect);
            }

            cellDrawing.Freeze();
            var cellImage = new DrawingImage(cellDrawing);
            cellImage.Freeze();
            var summaryText = BuildLayoutPreviewSummary(cells.Count, totalShotGroupCount, glassWidth, glassHeight, invalidCellCount, expandedCellCount);
            return new ST_RECIPE_LAYOUT_PREVIEW(cellImage, translatedCellLabels, summaryText);
        }

        private static IReadOnlyList<ST_RECIPE_MASKING_AREA> BuildMaskingAreas(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, int modelType)
        {
            var parameterPrefix = $"MODEL{Math.Clamp(modelType, 1, MaximumCellModelTypeCount)}_";
            var count = Math.Clamp(ReadManagedInt(managedItems, $"{parameterPrefix}MASKING_HOLE_NUMBER", 0), 0, MaximumMaskingHoleCount);
            if (count <= 0)
            {
                return[];
            }

            var areas = new List<ST_RECIPE_MASKING_AREA>(count);
            for (var holeNo = 1; holeNo <= count; holeNo++)
            {
                var x = ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}MASKING_HOLE{holeNo}_X", double.NaN);
                var y = ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}MASKING_HOLE{holeNo}_Y", double.NaN);
                var sizeX = ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}MASKING_HOLE{holeNo}_SIZE_X", 0.0);
                var sizeY = ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}MASKING_HOLE{holeNo}_SIZE_Y", 0.0);
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(sizeX) || !double.IsFinite(sizeY) || sizeX <= 0.0 || sizeY <= 0.0)
                {
                    continue;
                }

                areas.Add(new ST_RECIPE_MASKING_AREA(holeNo, new Rect(x, y, sizeX, sizeY)));
            }

            return areas;
        }

        private static ST_RECIPE_EDGE_MASKING_AREA? BuildEdgeMaskingArea(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, int modelType, ST_CELL_PATTERN_LAYOUT patternLayout)
        {
            const double tolerance = 0.001;
            if (!patternLayout.IsValid || patternLayout.GroupCountX <= 0 || patternLayout.GroupCountY <= 0 || !double.IsFinite(patternLayout.GroupPitchMillimeters) || patternLayout.GroupPitchMillimeters <= 0.0)
            {
                return null;
            }

            var pixelSize = ReadManagedFiniteDouble(managedItems, "PIXEL_SIZE", 0.0);
            if (patternLayout.PixelCountX <= 0 || patternLayout.PixelCountY <= 0 || !double.IsFinite(pixelSize) || pixelSize <= 0.0 || !double.IsFinite(patternLayout.BeamHalfSpanMillimeters) || patternLayout.BeamHalfSpanMillimeters < 0.0)
            {
                return null;
            }

            var parameterPrefix = $"MODEL{Math.Clamp(modelType, 1, MaximumCellModelTypeCount)}_";
            var upLeft = CreatePositiveOffsetPoint(ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_UP_LEFT_ROUND_X", 0.0), ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_UP_LEFT_ROUND_Y", 0.0));
            var upRight = CreatePositiveOffsetPoint(ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_UP_RIGHT_ROUND_X", 0.0), ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_UP_RIGHT_ROUND_Y", 0.0));
            var downLeft = CreatePositiveOffsetPoint(ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_DOWN_LEFT_ROUND_X", 0.0), ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_DOWN_LEFT_ROUND_Y", 0.0));
            var downRight = CreatePositiveOffsetPoint(ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_DOWN_RIGHT_ROUND_X", 0.0), ReadManagedFiniteDouble(managedItems, $"{parameterPrefix}CELL_DOWN_RIGHT_ROUND_Y", 0.0));
            var values = new[]
            {
                upLeft.X,
                upLeft.Y,
                upRight.X,
                upRight.Y,
                downLeft.X,
                downLeft.Y,
                downRight.X,
                downRight.Y
            };
            if (values.All(MatchesDouble))
            {
                return null;
            }

            var hasInvalidNumber = values.Any(MatchesDouble2);
            var leftPixelX = -patternLayout.BeamHalfSpanMillimeters;
            var topPixelY = -patternLayout.BeamHalfSpanMillimeters;
            var rightPixelX = leftPixelX + (Math.Max(0, patternLayout.PixelCountX - 1) * pixelSize);
            var bottomPixelY = topPixelY + (Math.Max(0, patternLayout.PixelCountY - 1) * pixelSize);
            var bounds = new List<Rect>(4);
            AddCorner(bounds, leftPixelX, leftPixelX + upLeft.X, topPixelY, topPixelY + upLeft.Y);
            AddCorner(bounds, rightPixelX - upRight.X, rightPixelX, topPixelY, topPixelY + upRight.Y);
            AddCorner(bounds, leftPixelX, leftPixelX + downLeft.X, bottomPixelY - downLeft.Y, bottomPixelY);
            AddCorner(bounds, rightPixelX - downRight.X, rightPixelX, bottomPixelY - downRight.Y, bottomPixelY);
            return hasInvalidNumber ? null : bounds.Count == 0 ? null : new ST_RECIPE_EDGE_MASKING_AREA(bounds, upLeft, upRight, downLeft, downRight);
            static Point CreatePositiveOffsetPoint(double x, double y)
            {
                return new Point(Math.Abs(x), Math.Abs(y));
            }

            static void AddCorner(List<Rect> bounds, double left, double right, double top, double bottom)
            {
                if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom) || Math.Abs(right - left) <= tolerance || Math.Abs(bottom - top) <= tolerance)
                {
                    return;
                }

                bounds.Add(new Rect(new Point(Math.Min(left, right), Math.Min(top, bottom)), new Point(Math.Max(left, right), Math.Max(top, bottom))));
            }

            bool MatchesDouble(double value)
            {
                return Math.Abs(value) <= tolerance;
            }

            bool MatchesDouble2(double value)
            {
                return !double.IsFinite(value);
            }
        }

        private static bool ShouldUseExpandedPreviewBounds(Rect actualBounds, Rect displayBounds)
        {
            return !actualBounds.IsEmpty && (Math.Abs(actualBounds.Width - displayBounds.Width) > 0.001 || Math.Abs(actualBounds.Height - displayBounds.Height) > 0.001);
        }

        private static Rect ExpandPreviewBounds(Rect bounds, double minimumWidth, double minimumHeight, Rect limitBounds)
        {
            var left = ClampPreviewCoordinate(bounds.Left, limitBounds.Left, limitBounds.Right);
            var top = ClampPreviewCoordinate(bounds.Top, limitBounds.Top, limitBounds.Bottom);
            var maximumWidth = Math.Max(1.0, limitBounds.Right - left);
            var maximumHeight = Math.Max(1.0, limitBounds.Bottom - top);
            var width = Math.Min(Math.Max(bounds.Width, minimumWidth), maximumWidth);
            var height = Math.Min(Math.Max(bounds.Height, minimumHeight), maximumHeight);
            return new Rect(left, top, width, height);
        }

        private static double ClampPreviewCoordinate(double value, double minimum, double maximum)
        {
            var safeMaximum = Math.Max(minimum, maximum);
            return Math.Clamp(value, minimum, safeMaximum);
        }

        private static string BuildLayoutPreviewSummary(int cellCount, long shotGroupCount, double glassWidth, double glassHeight, int invalidCellCount, int expandedCellCount)
        {
            var summary = $"{cellCount:N0} Cells / {shotGroupCount:N0} Shot Centers / Glass {FormatPreviewDouble(glassWidth)} x {FormatPreviewDouble(glassHeight)} mm";
            if (invalidCellCount > 0)
            {
                summary += $" / {invalidCellCount:N0} Invalid Cells";
            }

            if (expandedCellCount > 0)
            {
                summary += $" / {expandedCellCount:N0} Small Cells";
            }

            return summary;
        }

        private static ST_RECIPE_HEAD_PREVIEW BuildHeadPreview(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, string selectedGroup)
        {
            const double maxWidth = 772.0;
            const double maxHeight = 340.0;
            const double paddingX = 44.0;
            const double paddingTop = 42.0;
            const double paddingBottom = 28.0;
            var glassWidth = ReadManagedDouble(managedItems, "GLASS_SIZE_X", 500.0);
            var glassHeight = ReadManagedDouble(managedItems, "GLASS_SIZE_Y", 300.0);
            var akMarginX = ReadManagedDouble(managedItems, "AK_MARGIN_X", 55.0);
            var akMarginY = ReadManagedDouble(managedItems, "AK_MARGIN_Y", 45.0);
            var headCount = Math.Clamp(ReadManagedInt(managedItems, "HEAD_COUNT", 8), 1, 8);
            var head1AkPositionX = ReadSettingDouble(settings, "H01_AK_POSITION_X", -5.0);
            var headGapX = ReadSettingDouble(settings, "HeadGapX", 200.0);
            var headGapY = ReadSettingDouble(settings, "HeadGapY", 0.0);
            if (glassWidth <= 0.0 || glassHeight <= 0.0)
            {
                return new ST_RECIPE_HEAD_PREVIEW(null, [], "Glass size is not valid.");
            }

            var scale = Math.Min(maxWidth / glassWidth, maxHeight / glassHeight);
            var frameWidth = glassWidth * scale;
            var frameHeight = glassHeight * scale;
            var previewWidth = frameWidth + (paddingX * 2.0);
            var previewHeight = frameHeight + paddingTop + paddingBottom;
            var previewRect = new Rect(0.0, 0.0, previewWidth, previewHeight);
            var glassRect = new Rect(paddingX, paddingTop, frameWidth, frameHeight);
            var frame = new ST_GLASS_PREVIEW_FRAME(glassRect.Left, glassRect.Top, glassRect.Width, glassRect.Height);
            var labels = new List<ST_HEAD_COVERAGE_PREVIEW_LABEL>();
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, previewRect);
                context.DrawRectangle(CStatusBrush.CardBackground, null, glassRect);
                DrawHeadPreviewScale(context, glassRect);
                DrawAkCenterGuides(context, glassRect, glassWidth, glassHeight, akMarginX, akMarginY, scale);
                for (var headNo = 1; headNo <= headCount; headNo++)
                {
                    var defaultOffsetX = ReadManagedDouble(managedItems, $"H{headNo:00}_DEFAULT_OFFSET_X", 0.0);
                    var defaultOffsetY = ReadManagedDouble(managedItems, $"H{headNo:00}_DEFAULT_OFFSET_Y", 0.0);
                    var firstBeamOffsetX = ReadManagedDouble(managedItems, $"H{headNo:00}_FIRST_BEAM_OFFSET_X", 0.0);
                    var firstBeamOffsetY = ReadManagedDouble(managedItems, $"H{headNo:00}_FIRST_BEAM_OFFSET_Y", 0.0);
                    var centerX = akMarginX + head1AkPositionX + ((headNo - 1) * headGapX) + defaultOffsetX + firstBeamOffsetX;
                    var laneY = CalculateHeadPreviewLaneY(glassHeight, headGapY, headNo) + defaultOffsetY + firstBeamOffsetY;
                    var canvasCenterX = glassRect.Left + (centerX * scale);
                    var canvasCenterY = glassRect.Top + (laneY * scale);
                    labels.Add(CreateHeadPreviewLabel(headNo, canvasCenterX, canvasCenterY, previewWidth, previewHeight, IsSelectedHeadPreviewGroup(selectedGroup, headNo)));
                }

                context.DrawRectangle(null, new Pen(CStatusBrush.PrimaryText, 1.8), glassRect);
                CCellPreviewDrawing.DrawAlignKeys(context, frame, glassWidth, glassHeight, akMarginX, akMarginY);
                DrawPreviewAxisIndicator(context, glassRect);
            }

            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            var summaryText = $"Glass {FormatPreviewDouble(glassWidth)} x {FormatPreviewDouble(glassHeight)} mm / " + $"AK To Head1 Center {FormatPreviewDouble(head1AkPositionX)} mm / " + $"Head Gap X {FormatPreviewDouble(headGapX)} mm / " + $"Head Gap Y {FormatPreviewDouble(headGapY)} mm";
            return new ST_RECIPE_HEAD_PREVIEW(image, labels, summaryText);
        }

        private static double CalculateHeadPreviewLaneY(double glassHeight, double headGapY, int headNo)
        {
            var centerY = glassHeight / 2.0;
            var oddLaneY = centerY - (headGapY / 2.0);
            var evenLaneY = oddLaneY + headGapY;
            return headNo % 2 == 0 ? evenLaneY : oddLaneY;
        }

        private static bool IsSelectedHeadPreviewGroup(string selectedGroup, int headNo)
        {
            return selectedGroup.Equals("ALL", StringComparison.OrdinalIgnoreCase) || selectedGroup.Equals($"H{headNo:00}", StringComparison.OrdinalIgnoreCase);
        }

        private static void DrawHeadPreviewScale(DrawingContext context, Rect glassRect)
        {
            var gridPen = new Pen(CStatusBrush.Muted, 0.8);
            gridPen.Freeze();
            for (var index = 1; index < 6; index++)
            {
                var x = glassRect.Left + (glassRect.Width * index / 6.0);
                context.DrawLine(gridPen, new Point(x, glassRect.Top), new Point(x, glassRect.Bottom));
            }
        }

        private static void DrawAkCenterGuides(DrawingContext context, Rect glassRect, double glassWidth, double glassHeight, double akMarginX, double akMarginY, double scale)
        {
            var guideBrush = CStatusBrush.PrimaryText;
            var guidePen = new Pen(guideBrush, 1.35)
            {
                DashStyle = DashStyles.Dash,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            guidePen.Freeze();
            var centerCanvasX = glassRect.Left + ((glassWidth / 2.0) * scale);
            var centerCanvasY = glassRect.Top + ((glassHeight / 2.0) * scale);
            context.DrawLine(guidePen, new Point(centerCanvasX, glassRect.Top), new Point(centerCanvasX, glassRect.Bottom));
            context.DrawLine(guidePen, new Point(glassRect.Left, centerCanvasY), new Point(glassRect.Right, centerCanvasY));
            var akCenterDistanceX = Math.Abs((glassWidth / 2.0) - akMarginX);
            var akCenterDistanceY = Math.Abs((glassHeight / 2.0) - akMarginY);
            DrawAkCenterGuideText(context, $"X {FormatPreviewDouble(akCenterDistanceX)} mm", new Point(centerCanvasX, Math.Max(6.0, glassRect.Top - 26.0)), true, guideBrush);
            DrawAkCenterGuideText(context, $"Y {FormatPreviewDouble(akCenterDistanceY)} mm", new Point(glassRect.Left + 8.0, centerCanvasY - 26.0), false, guideBrush);
        }

        private static void DrawAkCenterGuideText(DrawingContext context, string text, Point anchor, bool centerAligned, Brush brush)
        {
            var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 12.0, brush, 1.0);
            var left = centerAligned ? anchor.X - (label.Width / 2.0) : anchor.X;
            context.DrawText(label, new Point(left, anchor.Y));
        }

        private static void DrawPreviewAxisIndicator(DrawingContext context, Rect glassRect)
        {
            var axisBrush = CStatusBrush.PrimaryText;
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
            DrawPreviewAxisText(context, "X+", new Point(xEnd.X + 4.0, Math.Max(2.0, xEnd.Y - 10.0)), axisBrush);
            DrawPreviewAxisText(context, "Y+", new Point(yEnd.X + 4.0, yEnd.Y - 4.0), axisBrush);
        }

        private static void DrawPreviewAxisText(DrawingContext context, string text, Point point, Brush brush)
        {
            var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13.0, brush, 1.0);
            context.DrawText(label, point);
        }

        private static ST_HEAD_COVERAGE_PREVIEW_LABEL CreateHeadPreviewLabel(int headNo, double centerX, double centerY, double designWidth, double designHeight, bool isSelected)
        {
            var label = new FormattedText($"H{headNo:00}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 12.0, Brushes.Black, 1.0);
            var badgeWidth = Math.Max(48.0, label.Width + 18.0);
            return new ST_HEAD_COVERAGE_PREVIEW_LABEL(headNo, centerX, centerY, badgeWidth, designWidth, designHeight, isSelected);
        }

        private static StreamGeometry? BuildCellBoundaryGeometry(
            ST_RECIPE_CELL_VIEW_MODEL cell, 
            double frameLeft, 
            double frameTop, 
            double scale, 
            double pixelSize, 
            int pixelCountX, 
            int pixelCountY, 
            double originX, 
            double originY
        )
        {
            if (pixelCountX <= 0 || pixelCountY <= 0 || !double.IsFinite(pixelSize) || pixelSize <= 0.0)
            {
                return null;
            }

            var firstX = originX + ReadCellDouble(cell.Items, $"CELL{cell.CellNo}_ALIGN_TO_1ST_PIXEL_X");
            var firstY = originY + ReadCellDouble(cell.Items, $"CELL{cell.CellNo}_ALIGN_TO_1ST_PIXEL_Y");
            var radians = ReadCellDouble(cell.Items, $"CELL{cell.CellNo}_ROTATION") * Math.PI / 180.0;
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
            Point BuildPoint(Point local)
            {
                var x = firstX + (local.X * cos) - (local.Y * sin);
                var y = firstY + (local.X * sin) + (local.Y * cos);
                return new Point(frameLeft + (x * scale), frameTop + (y * scale));
            }

            var canvasCorners = localCorners.Select(BuildPoint).ToArray();
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(canvasCorners[0], false, true);
                geometryContext.PolyLineTo(canvasCorners.Skip(1).ToArray(), true, false);
            }

            geometry.Freeze();
            return geometry;
        }

        private static void AddPointCircle(StreamGeometryContext context, double x, double y, double size)
        {
            var radius = size / 2.0;
            var control = radius * 0.5522847498;
            context.BeginFigure(new Point(x + radius, y), true, true);
            context.BezierTo(new Point(x + radius, y + control), new Point(x + control, y + radius), new Point(x, y + radius), true, false);
            context.BezierTo(new Point(x - control, y + radius), new Point(x - radius, y + control), new Point(x - radius, y), true, false);
            context.BezierTo(new Point(x - radius, y - control), new Point(x - control, y - radius), new Point(x, y - radius), true, false);
            context.BezierTo(new Point(x + control, y - radius), new Point(x + radius, y - control), new Point(x + radius, y), true, false);
        }

        private IReadOnlyList<ST_SHOT_GROUP_EDITOR_ROW> BuildShotGroupRows(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, ST_RECIPE_CELL_VIEW_MODEL cell)
        {
            var detailedLayout = CalculateCellPatternLayout(managedItems, cell, includeShotCenters: true);
            if (!detailedLayout.IsValid || detailedLayout.ShotCenters.Count == 0)
            {
                return[];
            }

            cell = cell with
            {
                PatternLayout = detailedLayout
            };
            var glassWidth = ReadManagedDouble(managedItems, "GLASS_SIZE_X", 500.0);
            var glassHeight = ReadManagedDouble(managedItems, "GLASS_SIZE_Y", 300.0);
            var splitBeamCount = ReadManagedInt(managedItems, "SPLITED_BEAM_COUNT", 1);
            var modelPattern = ReadManagedModelPattern(managedItems, cell.ModelType);
            var chess = modelPattern.Chess;
            var chessType = modelPattern.ChessType;
            var maskingAreas = BuildMaskingAreas(managedItems, cell.ModelType);
            var edgeMaskingArea = BuildEdgeMaskingArea(managedItems, cell.ModelType, cell.PatternLayout);
            var beamHalfSpan = cell.PatternLayout.BeamHalfSpanMillimeters;
            var finalShotOffsets = BuildFinalShotOffsets(managedItems);
            bool MatchesRecipe(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return TryGetCellNo(item.Key) == cell.CellNo && IsShotGroupOverrideKey(item.Key);
            }

            string SelectOverrideParameterKey(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key;
            }

            string SelectOverrideGroupKey(global::System.Linq.IGrouping<string, global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM> group)
            {
                return group.Last().Value;
            }

            var overrideValues = managedItems.Where(MatchesRecipe).GroupBy(SelectOverrideParameterKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectOverrideGroupKey, SelectValue, StringComparer.OrdinalIgnoreCase);
            global::Drilling.UI.Menu.Menus.ST_SHOT_GROUP_EDITOR_ROW BuildShot(global::Drilling.Common.Recipe.ST_SHOT_GROUP_CENTER shotCenter)
            {
                var shotGroupName = CReviewShotNameFormatter.ToMatrixName(shotCenter.CellShotNo, Math.Max(1, cell.PatternLayout.GroupCountX));
                var recipeKeyPrefix = $"CELL{cell.CellNo}_{shotGroupName}_RECIPE_OFFSET_";
                void SetShotGroupOverrideValueCallback(string parameterName, string value)
                {
                    SetShotGroupOverrideValue(cell.CellNo, shotGroupName, parameterName, value);
                }

                var cellLocalCenterX = shotCenter.GroupColumn * cell.PatternLayout.GroupPitchMillimeters;
                var cellLocalCenterY = shotCenter.GroupRow * cell.PatternLayout.GroupPitchMillimeters;
                finalShotOffsets.TryGetValue((cell.CellNo, shotCenter.CellShotNo), out var finalShotOffset);
                return new ST_SHOT_GROUP_EDITOR_ROW(
                    shotCenter.CellShotNo, 
                    shotCenter.GroupRow + 1, 
                    shotCenter.GroupColumn + 1, 
                    shotCenter.DesignX, 
                    shotCenter.DesignY, 
                    cellLocalCenterX, 
                    cellLocalCenterY, 
                    finalShotOffset.OffsetX, 
                    finalShotOffset.OffsetY, 
                    beamHalfSpan, 
                    glassWidth, 
                    glassHeight, 
                    splitBeamCount, 
                    cell.PatternLayout.BeamPitchMillimeters, 
                    chess, 
                    chessType, 
                    maskingAreas, 
                    edgeMaskingArea, 
                    overrideValues.GetValueOrDefault($"{recipeKeyPrefix}X", "0"), 
                    overrideValues.GetValueOrDefault($"{recipeKeyPrefix}Y", "0"), 
                    SetShotGroupOverrideValueCallback
                );
            }

            return cell.ShotCenters.Select(BuildShot).ToArray();
        }

        private static ST_CELL_PATTERN_LAYOUT CalculateCellPatternLayout(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, ST_RECIPE_CELL_VIEW_MODEL cell, bool includeShotCenters)
        {
            var akMarginX = ReadManagedDouble(managedItems, "AK_MARGIN_X", 55.0);
            var akMarginY = ReadManagedDouble(managedItems, "AK_MARGIN_Y", 45.0);
            var pixelSize = ReadManagedDouble(managedItems, "PIXEL_SIZE", 0.09);
            var modelPattern = ReadManagedModelPattern(managedItems, cell.ModelType);
            var splitBeamCount = ReadManagedInt(managedItems, "SPLITED_BEAM_COUNT", 1);
            var firstPixelX = ReadCellDouble(cell.Items, $"CELL{cell.CellNo}_ALIGN_TO_1ST_PIXEL_X");
            var firstPixelY = ReadCellDouble(cell.Items, $"CELL{cell.CellNo}_ALIGN_TO_1ST_PIXEL_Y");
            var rotation = ReadCellDouble(cell.Items, $"CELL{cell.CellNo}_ROTATION");
            return CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(cell.CellNo, firstPixelX, firstPixelY, rotation, pixelSize, modelPattern.PixelCountX, modelPattern.PixelCountY, modelPattern.ProcessPitch, splitBeamCount, akMarginX, akMarginY), includeShotCenters);
        }

        private static IReadOnlyDictionary<(int CellNo, int CellShotNo), (double OffsetX, double OffsetY)> BuildFinalShotOffsets(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            string SelectManagedParameterKey(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key;
            }

            string SelectManagedParameterGroupKey(global::System.Linq.IGrouping<string, global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM> group)
            {
                return group.Last().Value;
            }

            try
            {
                var parameters = managedItems.GroupBy(SelectManagedParameterKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectManagedParameterGroupKey, SelectValue, StringComparer.OrdinalIgnoreCase);
                var coordinatePlan = CShotCoordinatePlanBuilder.Build(parameters);
                return coordinatePlan.Shots.GroupBy(shot => (shot.CellNo, shot.CellShotNo)).ToDictionary(group => group.Key, group =>
                {
                    var shot = group.First();
                    return (shot.FinalScannerOffsetX - shot.RecipeOffsetX, shot.FinalScannerOffsetY - shot.RecipeOffsetY);
                });
            }
            catch
            {
                return new Dictionary<(int CellNo, int CellShotNo), (double OffsetX, double OffsetY)>();
            }
        }

        private void UpdateShotGroupRows(IReadOnlyList<ST_SHOT_GROUP_EDITOR_ROW> rows)
        {
            ShotGroupRows = rows;
            OnPropertyChanged(nameof(ShotGroupRows));
            OnPropertyChanged(nameof(SelectedCellShotGroupTitle));
            if (rows.Count == 0)
            {
                _selectedCellShotNo = 1;
                SelectedShotGroup = null;
                RefreshCellPatternViewer();
                return;
            }

            _selectedCellShotNo = Math.Clamp(_selectedCellShotNo, 1, rows.Count);
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_SHOT_GROUP_EDITOR_ROW row)
            {
                return row.CellShotNo == _selectedCellShotNo;
            }

            SelectedShotGroup = rows.FirstOrDefault(MatchesItem) ?? rows[0];
        }

        private void SetShotGroupOverrideValue(int cellNo, string shotGroupName, string parameterName, string value)
        {
            var normalizedParameter = parameterName.Trim().ToUpperInvariant();
            if (normalizedParameter is not ("OFFSET_X" or "OFFSET_Y"))
            {
                return;
            }

            var axis = normalizedParameter.EndsWith("_Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "X";
            var key = $"CELL{cellNo}_{shotGroupName}_RECIPE_OFFSET_{axis}";
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM candidate)
            {
                return candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var item = AllManagedItems.FirstOrDefault(MatchesItem);
            if (item is null)
            {
                var displayName = $"DOE Center {shotGroupName} Recipe Offset {axis}";
                item = new ST_RECIPE_PARAMETER_EDITOR_ITEM("CELL", "SHOT_GROUP", displayName, "0", "mm", "DOE center correction", "Normal", key, "SHOT_GROUP", EN_RECIPE_DATA_TYPE.Double, 0.0, -100000.0, 100000.0);
                AllManagedItems = AllManagedItems.Append(item).ToArray();
                TrackPreviewItems(AllManagedItems);
                OnPropertyChanged(nameof(AllManagedItems));
            }

            item.Value = value;
            RefreshCellPatternViewer();
        }

        private static bool IsShotGroupOverrideKey(string key)
        {
            if (TryGetCellNo(key) <= 0)
            {
                return false;
            }

            return key.EndsWith("_RECIPE_OFFSET_X", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_RECIPE_OFFSET_Y", StringComparison.OrdinalIgnoreCase);
        }

        private static double ReadManagedDouble(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, string key, double defaultValue)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = managedItems.FirstOrDefault(MatchesItem)?.Value;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static double ReadManagedFiniteDouble(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, string key, double defaultValue)
        {
            var value = ReadManagedDouble(managedItems, key, defaultValue);
            return double.IsFinite(value) ? value : defaultValue;
        }

        private static double? ReadManagedNullableDouble(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, string key)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = managedItems.FirstOrDefault(MatchesItem)?.Value;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static int ReadManagedInt(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, string key, int defaultValue)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = managedItems.FirstOrDefault(MatchesItem)?.Value;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static ST_RECIPE_MODEL_PIXEL_COUNT ReadManagedModelPixelCount(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, int modelType)
        {
            var prefix = $"MODEL{Math.Clamp(modelType, 1, MaximumCellModelTypeCount)}_";
            return new ST_RECIPE_MODEL_PIXEL_COUNT(ReadManagedInt(managedItems, $"{prefix}NUM_OF_PIXEL_X", 0), ReadManagedInt(managedItems, $"{prefix}NUM_OF_PIXEL_Y", 0));
        }

        private static ST_RECIPE_MODEL_PATTERN ReadManagedModelPattern(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems, int modelType)
        {
            var prefix = $"MODEL{Math.Clamp(modelType, 1, MaximumCellModelTypeCount)}_";
            return new ST_RECIPE_MODEL_PATTERN(
                ReadManagedInt(managedItems, $"{prefix}NUM_OF_PIXEL_X", 0), 
                ReadManagedInt(managedItems, $"{prefix}NUM_OF_PIXEL_Y", 0), 
                ReadManagedInt(managedItems, $"{prefix}PITCH", 1), 
                Math.Clamp(ReadManagedInt(managedItems, $"{prefix}CHESS", 1), 1, 4), 
                Math.Clamp(ReadManagedInt(managedItems, $"{prefix}CHESS_TYPE", 1), 0, 1)
            );
        }

        private static int ReadIntText(string? value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static int PositiveModulo(int value, int divisor)
        {
            return ((value % divisor) + divisor) % divisor;
        }

        private static string BuildShotGroupPatternSummary(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            var modelPattern = ReadManagedModelPattern(managedItems, 1);
            var result = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(
                1, 
                0.0, 
                0.0, 
                0.0, 
                ReadManagedDouble(managedItems, "PIXEL_SIZE", 0.09), 
                modelPattern.PixelCountX, 
                modelPattern.PixelCountY, 
                modelPattern.ProcessPitch, 
                ReadManagedInt(managedItems, "SPLITED_BEAM_COUNT", 1)
            ));
            if (!result.IsValid)
            {
                return result.ValidationMessage;
            }

            var chess = modelPattern.Chess;
            var splitBeamCount = ReadManagedInt(managedItems, "SPLITED_BEAM_COUNT", 1);
            var excludedSummary = result.HasExcludedTargets ? $"TRAILING PIXEL X+ {result.ExcludedTargetCountX:N0} / Y+ {result.ExcludedTargetCountY:N0}  |  " : "";
            return $"DOE CENTER {result.GroupCountX:N0} x {result.GroupCountY:N0}  |  " + excludedSummary + $"{splitBeamCount:N0} x {splitBeamCount:N0} BEAMS / SHOT  |  " + $"CENTER PITCH {result.GroupPitchMillimeters:0.######} mm  |  " + $"BEAM PITCH {result.BeamPitchMillimeters:0.######} mm  |  CHESS {chess}";
        }

        private static double ReadSettingDouble(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, string key, double defaultValue)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER item)
            {
                return item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || item.Name.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = settings.FirstOrDefault(MatchesItem)?.Value;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static string FormatPreviewDouble(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static IReadOnlyList<ST_RECIPE_CATEGORY_TAB> BuildCategoryTabs(IReadOnlyList<string> categories, string selectedCategory)
        {
            global::Drilling.UI.Menu.Menus.ST_RECIPE_CATEGORY_TAB BuildRecipe(string category)
            {
                return new ST_RECIPE_CATEGORY_TAB(category, category.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            return categories.Select(BuildRecipe).ToArray();
        }

        private static IReadOnlyList<string> BuildGroups(IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            string SelectSourceGroup(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.SourceGroup;
            }

            bool ShouldIncludeItem(string group)
            {
                return !string.IsNullOrWhiteSpace(group);
            }

            var groups = managedItems.Select(SelectSourceGroup).Where(ShouldIncludeItem).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new[]
            {
                "ALL"
            }.Concat(groups).ToArray();
        }

        private static IReadOnlyList<ST_RECIPE_GROUP_TAB> BuildGroupTabs(IReadOnlyList<string> groups, string selectedGroup)
        {
            global::Drilling.UI.Menu.Menus.ST_RECIPE_GROUP_TAB BuildRecipe(string group)
            {
                return new ST_RECIPE_GROUP_TAB(group, group.Equals(selectedGroup, StringComparison.OrdinalIgnoreCase));
            }

            return groups.Select(BuildRecipe).ToArray();
        }

        private static IReadOnlyList<ST_RECIPE_HISTORY_ROW> BuildChangeHistory(IReadOnlyList<ST_RECIPE_HISTORY> history)
        {
            global::Drilling.UI.Menu.Menus.ST_RECIPE_HISTORY_ROW BuildRecipe(global::Drilling.Common.Managers.ST_RECIPE_HISTORY item)
            {
                return new ST_RECIPE_HISTORY_ROW(item.ChangedAt.ToString("HH:mm:ss"), string.IsNullOrWhiteSpace(item.Action) ? item.OperatorId : item.Action, item.Tab, item.Group, item.ItemName, item.OldValue, item.NewValue, item.RecipeName);
            }

            return history.Select(BuildRecipe).ToArray();
        }

        private static IReadOnlyList<ST_RECIPE_STATE_ROW> BuildStateRows(ST_RECIPE_DEFINITION? recipe, string selectedRecipeFile, IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> managedItems)
        {
            if (recipe is null)
            {
                return[new("Modified Items", "0"), new("Recipe File", "-"), new("Edit State", "No Recipe")];
            }

            bool IsIsEdited(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.IsEdited;
            }

            var modifiedCount = managedItems.Count(IsIsEdited);
            return[
                new("Modified Items", modifiedCount.ToString(), modifiedCount > 0 ? "Warn" : "Ok"), 
                new("Recipe File", selectedRecipeFile, "Accent"), 
                new("Edit State", modifiedCount > 0 ? "Modified" : "Loaded", modifiedCount > 0 ? "Warn" : "Ok")
            ];
        }

        private static string NormalizeCategory(string category, IReadOnlyList<string> categories)
        {
            if (categories.Count == 0)
            {
                return "";
            }

            var normalized = NormalizeRecipeText(category, categories[0]);
            bool MatchesItem(string item)
            {
                return item.Equals(normalized, StringComparison.OrdinalIgnoreCase);
            }

            return categories.Any(MatchesItem) ? normalized : categories[0];
        }

        private static string NormalizeGroup(string group, IReadOnlyList<string> groups)
        {
            var normalized = NormalizeRecipeText(group, "ALL");
            bool MatchesItem(string item)
            {
                return item.Equals(normalized, StringComparison.OrdinalIgnoreCase);
            }

            return groups.Any(MatchesItem) ? normalized : "ALL";
        }

        private static string NormalizeRecipeText(string value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim().ToUpperInvariant();
        }

        private static string NormalizeUnit(string unit)
        {
            return string.IsNullOrWhiteSpace(unit) ? "-" : unit;
        }

        private static string GetValueState(ST_RECIPE_PARAMETER parameter)
        {
            if (IsModified(parameter))
            {
                return "Warn";
            }

            return parameter.Key.Equals("RECIPE_NAME", StringComparison.OrdinalIgnoreCase) || IsOnOffValue(parameter.Value) ? "Accent" : "Normal";
        }

        private static bool IsModified(ST_RECIPE_PARAMETER parameter)
        {
            return !string.Equals(NormalizeRecipeValue(parameter.Value), NormalizeRecipeValue(parameter.DefaultValue), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOnOffValue(string value)
        {
            var normalized = value.Trim().ToUpperInvariant();
            return normalized is "ON" or "OFF";
        }

        private static string NormalizeRecipeValue(string value)
        {
            return value.Trim();
        }

        private static string GetRecipeIdFromParameter(object? parameter)
        {
            var value = parameter switch
            {
                ST_RECIPE_FILE recipeFile => recipeFile.FileName,
                string text => text,
                _ => ""
            };
            return Path.GetFileNameWithoutExtension(value.Trim());
        }

        private static ST_RECIPE_PARAMETER CreateRecipeParameterFromRow(ST_RECIPE_PARAMETER_EDITOR_ITEM item, string recipeId)
        {
            var value = item.Key.Equals("RECIPE_NAME", StringComparison.OrdinalIgnoreCase) ? recipeId : item.Value;
            return new ST_RECIPE_PARAMETER(
                item.Item, 
                value, 
                NormalizeRecipeUnit(item.Unit), 
                "", 
                item.OriginalValue, 
                item.Category, 
                item.SourceGroup, 
                item.Key, 
                item.Description, 
                true, 
                true, 
                0, 
                item.DataType, 
                item.ChangeLimit, 
                item.Min, 
                item.Max
            );
        }

        private IReadOnlyList<ST_RECIPE_PARAMETER> BuildRecipeParameters(string recipeId)
        {
            global::Drilling.Common.Managers.ST_RECIPE_PARAMETER SelectItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return CreateRecipeParameterFromRow(item, recipeId);
            }

            return AllManagedItems.Select(SelectItem).ToArray();
        }

        private void WriteZeroDefenceDraft(IReadOnlyDictionary<string, string> values)
        {
            if (!CanEditViewRecipe())
                throw new InvalidOperationException("선택된 레시피만 수정 가능합니다.");
            var updates = values.Select(pair => (Item: AllManagedItems.SingleOrDefault(item => item.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"레시피 파라미터가 없습니다: {pair.Key}"), pair.Value)).ToArray();
            foreach (var update in updates)
                update.Item.Value = update.Value;
            NotifyCommands();
        }

        private string ValidateRecipeWithZeroDefence(IReadOnlyList<ST_RECIPE_PARAMETER> parameters)
        {
            var error = ValidateRecipeParameters(parameters);
            if (error.Length > 0)
                return error;
            var count = parameters.FirstOrDefault(p => p.Key == CZeroDefenceRecipePlan.CountKey)?.Value ?? "2";
            if (count is not ("2" or "4"))
                return "0선방어 검사 개수는 2 또는 4만 가능합니다.";
            if (!parameters.Any(p => p.Key.StartsWith("ZD_H", StringComparison.OrdinalIgnoreCase) && p.Value != "0"))
                return "";
            try
            {
                var settings = _settingManager.LoadSection(EN_SETTING_TAB.Option).Concat(_settingManager.LoadSection(EN_SETTING_TAB.Motor)).ToArray();
                return new CZeroDefenceRecipePlan(new("DRAFT", "DRAFT", parameters, []), settings).ValidateForSave();
            }
            catch (Exception exception)when (exception is InvalidOperationException or ArgumentException)
            {
                return exception.Message;
            }
        }

        private static string? ShowRecipeNameDialog(string title, string message, string initialValue, Func<string, string>? validate = null)
        {
            var dialog = new CRecipeNameDialog(title, message, initialValue, validate)
            {
                Owner = GetActiveWindow()
            };
            return dialog.ShowDialog() == true ? NormalizeRecipeIdInput(dialog.RecipeName) : null;
        }

        private static bool ConfirmRecipeDelete(string recipeId)
        {
            var dialog = new CRecipeConfirmDialog("레시피 삭제", $"{recipeId}.csv 레시피를 삭제하시겠습니까?\n삭제한 레시피 파일은 복구할 수 없습니다.", "삭제")
            {
                Owner = GetActiveWindow()
            };
            return dialog.ShowDialog() == true;
        }

        private static Window? GetActiveWindow()
        {
            return Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        }

        private static string NormalizeRecipeIdInput(string value)
        {
            var normalized = value.Trim();
            if (normalized.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^4];
            }

            return normalized.Trim();
        }

        private static string ValidateRecipeId(string recipeId, IReadOnlyList<ST_RECIPE_DEFINITION> recipes, string currentRecipeId = "")
        {
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                return "레시피 이름을 입력하세요.";
            }

            foreach (var character in recipeId)
            {
                if (Path.GetInvalidFileNameChars().Contains(character))
                {
                    return $"레시피 이름에는 '{character}' 문자를 사용할 수 없습니다.";
                }
            }

            if (recipeId is "." or ".." || recipeId.EndsWith(".", StringComparison.Ordinal))
            {
                return "파일 이름으로 사용할 수 없는 레시피 이름입니다.";
            }

            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9"
            };
            if (reservedNames.Contains(recipeId))
            {
                return "Windows에서 예약된 이름은 사용할 수 없습니다.";
            }

            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase) && !recipe.Id.Equals(currentRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            var exists = recipes.Any(MatchesItem);
            return exists ? $"{recipeId}.csv 레시피가 이미 존재합니다." : "";
        }

        private static string ValidateRecipeParameters(IReadOnlyList<ST_RECIPE_PARAMETER> parameters)
        {
            foreach (var parameter in parameters)
            {
                var value = parameter.Value.Trim();
                if (parameter.Key.Equals("RECIPE_NAME", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(value))
                {
                    return "Recipe save blocked. Recipe Name cannot be empty.";
                }

                var validationMessage = parameter.DataType switch
                {
                    EN_RECIPE_DATA_TYPE.Int => ValidateIntParameter(parameter, value),
                    EN_RECIPE_DATA_TYPE.Double => ValidateDoubleParameter(parameter, value),
                    EN_RECIPE_DATA_TYPE.Bool => ValidateBoolParameter(parameter, value),
                    _ => ""
                };
                if (!string.IsNullOrWhiteSpace(validationMessage))
                {
                    return validationMessage;
                }
            }

            var cellModelValidationMessage = ValidateCellModelTypes(parameters);
            if (!string.IsNullOrWhiteSpace(cellModelValidationMessage))
            {
                return cellModelValidationMessage;
            }

            var modelTypeCount = Math.Clamp(ReadRecipeParameterInt(parameters, "CELL_MODEL_TYPE_COUNT"), 1, MaximumCellModelTypeCount);
            for (var modelType = 1; modelType <= modelTypeCount; modelType++)
            {
                var shotGroupResult = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(
                    1, 
                    0.0, 
                    0.0, 
                    0.0, 
                    ReadRecipeParameterDouble(parameters, "PIXEL_SIZE"), 
                    ReadRecipeParameterInt(parameters, $"MODEL{modelType}_NUM_OF_PIXEL_X"), 
                    ReadRecipeParameterInt(parameters, $"MODEL{modelType}_NUM_OF_PIXEL_Y"), 
                    ReadRecipeParameterInt(parameters, $"MODEL{modelType}_PITCH"), 
                    ReadRecipeParameterInt(parameters, "SPLITED_BEAM_COUNT")
                ));
                if (!shotGroupResult.IsValid)
                {
                    return $"Recipe save blocked. MODEL{modelType} {shotGroupResult.ValidationMessage}";
                }
            }

            return "";
        }

        private static string ValidateCellModelTypes(IReadOnlyList<ST_RECIPE_PARAMETER> parameters)
        {
            var modelTypeCount = Math.Clamp(ReadRecipeParameterInt(parameters, "CELL_MODEL_TYPE_COUNT"), 1, MaximumCellModelTypeCount);
            foreach (var parameter in parameters)
            {
                if (TryGetCellNo(parameter.Key) <= 0 || !parameter.Key.EndsWith("_MODEL_TYPE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!int.TryParse(parameter.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modelType) || modelType < 1 || modelType > modelTypeCount)
                {
                    return $"Recipe save blocked. {parameter.Name} must be between 1 and CELL_MODEL_TYPE_COUNT ({modelTypeCount}).";
                }
            }

            return "";
        }

        private static string ValidateIntParameter(ST_RECIPE_PARAMETER parameter, string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"Recipe save blocked. {parameter.Name} must be an integer.";
            }

            return ValidateNumericRange(parameter, parsed);
        }

        private static string ValidateDoubleParameter(ST_RECIPE_PARAMETER parameter, string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
            {
                return $"Recipe save blocked. {parameter.Name} must be numeric.";
            }

            return ValidateNumericRange(parameter, parsed);
        }

        private static int ReadRecipeParameterInt(IReadOnlyList<ST_RECIPE_PARAMETER> parameters, string key)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = parameters.FirstOrDefault(MatchesItem)?.Value;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        private static double ReadRecipeParameterDouble(IReadOnlyList<ST_RECIPE_PARAMETER> parameters, string key)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = parameters.FirstOrDefault(MatchesItem)?.Value;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : 0.0;
        }

        private static string ValidateBoolParameter(ST_RECIPE_PARAMETER parameter, string value)
        {
            var normalized = value.Trim().ToUpperInvariant();
            return normalized is "ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0" or "YES" or "NO" ? "" : $"Recipe save blocked. {parameter.Name} must be ON/OFF or TRUE/FALSE.";
        }

        private static string ValidateNumericRange(ST_RECIPE_PARAMETER parameter, double value)
        {
            if (!parameter.Min.Equals(parameter.Max) && (value < parameter.Min || value > parameter.Max))
            {
                return $"Recipe save blocked. {parameter.Name} must be between {parameter.Min:0.###} and {parameter.Max:0.###}.";
            }

            if (parameter.ChangeLimit > 0 && double.TryParse(parameter.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var oldValue) && Math.Abs(value - oldValue) > parameter.ChangeLimit)
            {
                return $"Recipe save blocked. {parameter.Name} change limit is +/-{parameter.ChangeLimit:0.###}.";
            }

            return "";
        }

        private static string GetEditedRecipeName(IReadOnlyList<ST_RECIPE_PARAMETER> parameters, string fallbackRecipeId)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER item)
            {
                return item.Key.Equals("RECIPE_NAME", StringComparison.OrdinalIgnoreCase) || item.Name.Equals("Recipe Name", StringComparison.OrdinalIgnoreCase);
            }

            return parameters.FirstOrDefault(MatchesItem)?.Value ?? fallbackRecipeId;
        }

        private static string NormalizeRecipeUnit(string unit)
        {
            return unit == "-" ? "" : unit;
        }
    }

    public sealed record ST_RECIPE_CATEGORY_TAB(string Category, bool IsSelected)
    {
        public string DisplayName => Category.Equals("ZERO_DEFENCE", StringComparison.OrdinalIgnoreCase) ? "0선방어" : Category;
    }

    public sealed record ST_RECIPE_GROUP_TAB(string Group, bool IsSelected);
    public sealed record ST_RECIPE_CELL_VIEW_MODEL(
        int CellNo, 
        IReadOnlyList<ST_RECIPE_PARAMETER_EDITOR_ITEM> Items, 
        ST_CELL_PATTERN_LAYOUT PatternLayout, 
        double GlassFirstPixelX, 
        double GlassFirstPixelY, 
        double RotationDegrees, 
        double PixelSizeMillimeters, 
        int ModelType, 
        int PixelCountX, 
        int PixelCountY
    )
    {
        public IReadOnlyList<ST_SHOT_GROUP_CENTER> ShotCenters
        {
            get
            {
                return PatternLayout.ShotCenters;
            }
        }
    }

    public sealed record ST_RECIPE_MASKING_AREA(int HoleNo, Rect Bounds)
    {
        private const double Epsilon = 0.000000001;
        public string DisplayText
        {
            get
            {
                return $"MASK {HoleNo}";
            }
        }

        public bool Contains(Point point)
        {
            return point.X >= Bounds.Left && point.X <= Bounds.Right && point.Y >= Bounds.Top && point.Y <= Bounds.Bottom;
        }

        public bool Intersects(Rect bounds)
        {
            return Bounds.Left <= bounds.Right + Epsilon && Bounds.Right >= bounds.Left - Epsilon && Bounds.Top <= bounds.Bottom + Epsilon && Bounds.Bottom >= bounds.Top - Epsilon;
        }
    }

    public sealed record ST_RECIPE_EDGE_MASKING_AREA(IReadOnlyList<Rect> Bounds, Point UpLeft, Point UpRight, Point DownLeft, Point DownRight)
    {
        private const double Epsilon = 0.000000001;
        public bool Contains(Point point)
        {
            return Bounds.Any(bounds => point.X >= bounds.Left - Epsilon && point.X <= bounds.Right + Epsilon && point.Y >= bounds.Top - Epsilon && point.Y <= bounds.Bottom + Epsilon);
        }

        public bool Touches(Rect bounds)
        {
            return Bounds.Any(area => area.Left <= bounds.Right + Epsilon && area.Right >= bounds.Left - Epsilon && area.Top <= bounds.Bottom + Epsilon && area.Bottom >= bounds.Top - Epsilon);
        }
    }

    public sealed record ST_RECIPE_SHOT_MASKING_STATE(IReadOnlyList<int> BeamNumbers, IReadOnlyList<int> HoleNumbers, bool IsEdgeMasked)
    {
        public static ST_RECIPE_SHOT_MASKING_STATE Empty { get; } = new([], [], false);

        public bool IsMasked
        {
            get
            {
                return BeamNumbers.Count > 0;
            }
        }

        public string SummaryText
        {
            get
            {
                if (!IsMasked)
                {
                    return "";
                }

                var parts = new List<string>();
                if (IsEdgeMasked)
                {
                    parts.Add("EDGE");
                }

                if (HoleNumbers.Count > 0)
                {
                    parts.Add(FormatList(HoleNumbers, "MASK"));
                }

                parts.Add(FormatList(BeamNumbers, "BEAM"));
                return string.Join(" / ", parts);
            }
        }

        private static string FormatList(IReadOnlyList<int> numbers, string prefix)
        {
            if (numbers.Count == 0)
            {
                return prefix;
            }

            var ordered = numbers.Distinct().OrderBy(number => number).ToArray();
            var ranges = new List<string>();
            var start = ordered[0];
            var previous = ordered[0];
            for (var index = 1; index < ordered.Length; index++)
            {
                var current = ordered[index];
                if (current == previous + 1)
                {
                    previous = current;
                    continue;
                }

                ranges.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{previous}");
                start = current;
                previous = current;
            }

            ranges.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{previous}");
            return $"{prefix} {string.Join(",", ranges)}";
        }
    }

    public sealed record ST_RECIPE_DISTORTION_KEY_ITEM(int PairIndex, string StageY)
    {
        public string DisplayText
        {
            get
            {
                return $"Group {PairIndex} · DK{PairIndex * 2 + 1}/{PairIndex * 2 + 2}";
            }
        }

        public string YLabel
        {
            get
            {
                return $"Setting > Motor > ALIGN > DK{PairIndex} Stage Y Position (mm); AK2/AK4 촬영 위치가 0 mm";
            }
        }
    }

    public sealed class ST_RECIPE_CELL_OVERVIEW_ROW : CBindingBase
    {
        private readonly IReadOnlyDictionary<string, ST_RECIPE_PARAMETER_EDITOR_ITEM> _items;
        public ST_RECIPE_CELL_OVERVIEW_ROW(ST_RECIPE_CELL_VIEW_MODEL cell, bool isCurrent, IReadOnlyList<string> modelOptions)
        {
            CellNo = cell.CellNo;
            IsCurrent = isCurrent;
            ModelOptions = modelOptions;
            var keyPrefix = $"CELL{CellNo}_";
            bool ShouldIncludeItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase);
            }

            string SelectDictionaryKey(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.Key[keyPrefix.Length..];
            }

            global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM SelectItem(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item;
            }

            _items = cell.Items.Where(ShouldIncludeItem).ToDictionary(SelectDictionaryKey, SelectItem, StringComparer.OrdinalIgnoreCase);
        }

        public int CellNo { get; }
        public bool IsCurrent { get; }
        public IReadOnlyList<string> ModelOptions { get; }

        public string FirstX
        {
            get
            {
                return Get("ALIGN_TO_1ST_PIXEL_X");
            }

            set
            {
                Set("ALIGN_TO_1ST_PIXEL_X", value);
            }
        }

        public string FirstY
        {
            get
            {
                return Get("ALIGN_TO_1ST_PIXEL_Y");
            }

            set
            {
                Set("ALIGN_TO_1ST_PIXEL_Y", value);
            }
        }

        public string Rotation
        {
            get
            {
                return Get("ROTATION");
            }

            set
            {
                Set("ROTATION", value);
            }
        }

        public string ModelType
        {
            get
            {
                return Get("MODEL_TYPE");
            }

            set
            {
                Set("MODEL_TYPE", value);
            }
        }

        public ST_RECIPE_PARAMETER_EDITOR_ITEM? GetItem(string parameterName)
        {
            return _items.TryGetValue(parameterName, out var item) ? item : null;
        }

        private string Get(string parameterName)
        {
            return _items.TryGetValue(parameterName, out var item) ? item.Value : "";
        }

        private void Set(string parameterName, string value)
        {
            if (!_items.TryGetValue(parameterName, out var item) || item.Value == value)
            {
                return;
            }

            item.Value = value;
            OnPropertyChanged(parameterName switch
            {
                "ALIGN_TO_1ST_PIXEL_X" => nameof(FirstX),
                "ALIGN_TO_1ST_PIXEL_Y" => nameof(FirstY),
                "ROTATION" => nameof(Rotation),
                "MODEL_TYPE" => nameof(ModelType),
                _ => parameterName
            });
        }
    }

    public sealed class ST_SHOT_GROUP_EDITOR_ROW : CBindingBase
    {
        private readonly Action<string, string> _overrideChanged;
        private readonly double _beamHalfSpan;
        private readonly double _glassWidth;
        private readonly double _glassHeight;
        private readonly int _splitBeamCount;
        private readonly double _beamPitch;
        private readonly int _chess;
        private readonly int _chessType;
        private readonly double _baseCellCenterX;
        private readonly double _baseCellCenterY;
        private readonly double _additionalFinalOffsetX;
        private readonly double _additionalFinalOffsetY;
        private readonly IReadOnlyList<ST_RECIPE_MASKING_AREA> _maskingAreas;
        private readonly ST_RECIPE_EDGE_MASKING_AREA? _edgeMaskingArea;
        private string _offsetX;
        private string _offsetY;
        private bool _isSelected;
        private ST_RECIPE_SHOT_MASKING_STATE _maskingState = ST_RECIPE_SHOT_MASKING_STATE.Empty;
        public ST_SHOT_GROUP_EDITOR_ROW(
            int cellShotNo, 
            int row, 
            int column, 
            double baseCenterX, 
            double baseCenterY, 
            double baseCellCenterX, 
            double baseCellCenterY, 
            double additionalFinalOffsetX, 
            double additionalFinalOffsetY, 
            double beamHalfSpan, 
            double glassWidth, 
            double glassHeight, 
            int splitBeamCount, 
            double beamPitch, 
            int chess, 
            int chessType, 
            IReadOnlyList<ST_RECIPE_MASKING_AREA> maskingAreas, 
            ST_RECIPE_EDGE_MASKING_AREA? edgeMaskingArea, 
            string offsetX, 
            string offsetY, 
            Action<string, string> overrideChanged
        )
        {
            CellShotNo = cellShotNo;
            Row = row;
            Column = column;
            BaseCenterX = baseCenterX;
            BaseCenterY = baseCenterY;
            _baseCellCenterX = baseCellCenterX;
            _baseCellCenterY = baseCellCenterY;
            _additionalFinalOffsetX = additionalFinalOffsetX;
            _additionalFinalOffsetY = additionalFinalOffsetY;
            _beamHalfSpan = beamHalfSpan;
            _glassWidth = glassWidth;
            _glassHeight = glassHeight;
            _splitBeamCount = splitBeamCount;
            _beamPitch = beamPitch;
            _chess = Math.Clamp(chess, 1, 4);
            _chessType = Math.Clamp(chessType, 0, 1);
            _maskingAreas = maskingAreas;
            _edgeMaskingArea = edgeMaskingArea;
            _offsetX = offsetX;
            _offsetY = offsetY;
            _overrideChanged = overrideChanged;
            RefreshMaskingState();
        }

        public int CellShotNo { get; }
        public int Row { get; }
        public int Column { get; }

        public string MatrixPointName
        {
            get
            {
                return $"{ToColumnLetter(Column)}{Row}";
            }
        }

        public string OffsetCoordinateText
        {
            get
            {
                return $"({NormalizeOffsetText(OffsetX)}, {NormalizeOffsetText(OffsetY)})";
            }
        }

        public double BaseCenterX { get; }
        public double BaseCenterY { get; }

        public double CorrectedCenterX
        {
            get
            {
                return BaseCenterX + ParseOffset(OffsetX) + _additionalFinalOffsetX;
            }
        }

        public double CorrectedCenterY
        {
            get
            {
                return BaseCenterY + ParseOffset(OffsetY) + _additionalFinalOffsetY;
            }
        }

        public string CenterXText
        {
            get
            {
                return CorrectedCenterX.ToString("0.######", CultureInfo.InvariantCulture);
            }
        }

        public string CenterYText
        {
            get
            {
                return CorrectedCenterY.ToString("0.######", CultureInfo.InvariantCulture);
            }
        }

        public bool IsInsideGlass
        {
            get
            {
                return CorrectedCenterX - _beamHalfSpan >= 0.0 && CorrectedCenterX + _beamHalfSpan <= _glassWidth && CorrectedCenterY - _beamHalfSpan >= 0.0 && CorrectedCenterY + _beamHalfSpan <= _glassHeight;
            }
        }

        public bool IsMasked
        {
            get
            {
                return _maskingState.IsMasked;
            }
        }

        public bool IsChessSkipped
        {
            get
            {
                if (_chess <= 1)
                {
                    return false;
                }

                return _chessType == 0 ? PositiveModulo(Column - 1, _chess) != 0 || PositiveModulo(Row - 1, _chess) != 0 : PositiveModulo((Column - 1) - (Row - 1), _chess) != 0;
            }
        }

        public IReadOnlyList<int> MaskedBeamNumbers
        {
            get
            {
                return _maskingState.BeamNumbers;
            }
        }

        public string MaskingStateText
        {
            get
            {
                return _maskingState.IsMasked ? _maskingState.SummaryText : "NONE";
            }
        }

        public string PlacementState
        {
            get
            {
                if (!IsInsideGlass)
                {
                    return "OUTSIDE";
                }

                if (IsMasked)
                {
                    return "MASKED / LASER OFF";
                }

                return IsChessSkipped ? "CHESS / LASER OFF" : "IN GLASS";
            }
        }

        public bool IsSelected
        {
            get
            {
                return _isSelected;
            }

            set
            {
                SetProperty(ref _isSelected, value);
            }
        }

        public string OffsetX
        {
            get
            {
                return _offsetX;
            }

            set
            {
                SetOverride(ref _offsetX, value, "OFFSET_X");
            }
        }

        public string OffsetY
        {
            get
            {
                return _offsetY;
            }

            set
            {
                SetOverride(ref _offsetY, value, "OFFSET_Y");
            }
        }

        private void SetOverride(ref string field, string value, string parameterName)
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(parameterName == "OFFSET_X" ? nameof(OffsetX) : nameof(OffsetY));
                OnPropertyChanged(nameof(OffsetCoordinateText));
                OnPropertyChanged(nameof(CorrectedCenterX));
                OnPropertyChanged(nameof(CorrectedCenterY));
                OnPropertyChanged(nameof(CenterXText));
                OnPropertyChanged(nameof(CenterYText));
                OnPropertyChanged(nameof(IsInsideGlass));
                RefreshMaskingState();
                OnPropertyChanged(nameof(IsMasked));
                OnPropertyChanged(nameof(MaskedBeamNumbers));
                OnPropertyChanged(nameof(MaskingStateText));
                OnPropertyChanged(nameof(PlacementState));
                _overrideChanged(parameterName, value);
            }
        }

        private void RefreshMaskingState()
        {
            if ((_maskingAreas.Count == 0 && _edgeMaskingArea is null) || _splitBeamCount <= 0 || !double.IsFinite(_beamPitch) || _beamPitch <= 0.0 || !double.IsFinite(_baseCellCenterX) || !double.IsFinite(_baseCellCenterY))
            {
                _maskingState = ST_RECIPE_SHOT_MASKING_STATE.Empty;
                return;
            }

            var maskedBeamNumbers = new List<int>();
            var maskedHoleNumbers = new SortedSet<int>();
            var isEdgeMasked = false;
            var centerIndex = (_splitBeamCount - 1) / 2.0;
            var centerX = _baseCellCenterX + ParseOffset(OffsetX) + _additionalFinalOffsetX;
            var centerY = _baseCellCenterY + ParseOffset(OffsetY) + _additionalFinalOffsetY;
            for (var row = 0; row < _splitBeamCount; row++)
            {
                for (var column = 0; column < _splitBeamCount; column++)
                {
                    var localX = (column - centerIndex) * _beamPitch;
                    var localY = (row - centerIndex) * _beamPitch;
                    var point = new Point(centerX + localX, centerY + localY);
                    var beamBounds = new Rect(point.X - (_beamPitch / 2.0), point.Y - (_beamPitch / 2.0), _beamPitch, _beamPitch);
                    var isHoleMasked = false;
                    foreach (var area in _maskingAreas)
                    {
                        if (!area.Intersects(beamBounds))
                        {
                            continue;
                        }

                        maskedHoleNumbers.Add(area.HoleNo);
                        isHoleMasked = true;
                        break;
                    }

                    var isOutsideEdge = _edgeMaskingArea is not null && _edgeMaskingArea.Touches(beamBounds);
                    if (!isHoleMasked && !isOutsideEdge)
                    {
                        continue;
                    }

                    maskedBeamNumbers.Add((row * _splitBeamCount) + column + 1);
                    isEdgeMasked = isEdgeMasked || isOutsideEdge;
                }
            }

            _maskingState = maskedBeamNumbers.Count == 0 ? ST_RECIPE_SHOT_MASKING_STATE.Empty : new ST_RECIPE_SHOT_MASKING_STATE(maskedBeamNumbers, maskedHoleNumbers.ToArray(), isEdgeMasked);
        }

        private static string NormalizeOffsetText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "0" : value.Trim();
        }

        private static double ParseOffset(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : 0.0;
        }

        private static int PositiveModulo(int value, int divisor)
        {
            return ((value % divisor) + divisor) % divisor;
        }

        private static string ToColumnLetter(int oneBasedColumn)
        {
            var value = Math.Max(1, oneBasedColumn);
            var text = "";
            while (value > 0)
            {
                value--;
                text = (char)('A' + (value % 26)) + text;
                value /= 26;
            }

            return text;
        }
    }

    public sealed record ST_RECIPE_LAYOUT_PREVIEW(ImageSource? CellImage, IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellLabels, string SummaryText);
    public sealed record ST_RECIPE_HEAD_PREVIEW(ImageSource? Image, IReadOnlyList<ST_HEAD_COVERAGE_PREVIEW_LABEL> Labels, string SummaryText);
    public sealed record ST_HEAD_COVERAGE_PREVIEW_LABEL(int HeadNo, double CanvasCenterX, double CanvasCenterY, double Width, double DesignWidth, double DesignHeight, bool IsSelected)
    {
        public string DisplayText
        {
            get
            {
                return $"H{HeadNo:00}";
            }
        }

        public double Height
        {
            get
            {
                return 24.0;
            }
        }

        public Brush BackgroundBrush
        {
            get
            {
                return IsSelected ? CMenuMain.CreateHeadBrush(HeadNo, 230) : CStatusBrush.CardBackground;
            }
        }

        public Brush BorderBrush
        {
            get
            {
                return IsSelected ? CMenuMain.CreateHeadBrush(HeadNo, 255) : CStatusBrush.CommandDarkBorder;
            }
        }

        public Brush TextBrush
        {
            get
            {
                return IsSelected ? Brushes.White : CStatusBrush.PrimaryText;
            }
        }
    }

    public sealed record ST_RECIPE_FILE(string No, string FileName, bool IsView, bool IsSelect)
    {
        public bool IsSelected
        {
            get
            {
                return IsView;
            }
        }

        public Brush TextBrush
        {
            get
            {
                return IsView || IsSelect ? Brushes.White : CStatusBrush.PrimaryText;
            }
        }
    }

    public sealed class ST_RECIPE_PARAMETER_EDITOR_ITEM : CBindingBase
    {
        private readonly string _initialValue;
        private readonly string _initialValueState;
        private string _value;
        private string _valueState;
        private IReadOnlyList<string>? _valueOptions;
        public ST_RECIPE_PARAMETER_EDITOR_ITEM(
            string category, 
            string group, 
            string item, 
            string value, 
            string unit, 
            string description, 
            string valueState = "Normal", 
            string key = "", 
            string sourceGroup = "", 
            EN_RECIPE_DATA_TYPE dataType = EN_RECIPE_DATA_TYPE.String, 
            double changeLimit = 0.0, 
            double min = 0.0, 
            double max = 0.0, 
            IReadOnlyList<string>? valueOptions = null
        )
        {
            Category = category;
            Group = group;
            Item = item;
            Unit = unit;
            Description = description;
            Key = key;
            SourceGroup = sourceGroup;
            DataType = dataType;
            ChangeLimit = changeLimit;
            Min = min;
            Max = max;
            _valueOptions = valueOptions;
            _value = value;
            _valueState = valueState;
            _initialValue = value;
            _initialValueState = valueState;
        }

        public string Category { get; }
        public string Group { get; }
        public string Item { get; }

        public string Value
        {
            get
            {
                return _value;
            }

            set
            {
                if (!SetProperty(ref _value, value))
                {
                    return;
                }

                ValueState = IsEdited ? "Warn" : _initialValueState;
                OnPropertyChanged(nameof(IsEdited));
            }
        }

        public string Unit { get; }
        public string Description { get; }
        public string Key { get; }
        public string SourceGroup { get; }

        public string OriginalValue
        {
            get
            {
                return _initialValue;
            }
        }

        public EN_RECIPE_DATA_TYPE DataType { get; }

        public bool UsesSelectionEditor
        {
            get
            {
                return _valueOptions is not null || DataType == EN_RECIPE_DATA_TYPE.Bool || Key.Equals("SPLITED_BEAM_COUNT", StringComparison.OrdinalIgnoreCase) || IsModelChessKey(Key) || IsModelChessTypeKey(Key) || IsCellModelSelectionKey(Key);
            }
        }

        public IReadOnlyList<string> ValueOptions
        {
            get
            {
                if (_valueOptions is not null)
                {
                    return _valueOptions;
                }

                if (Key.Equals("SPLITED_BEAM_COUNT", StringComparison.OrdinalIgnoreCase))
                {
                    return["1", "4"];
                }

                if (IsModelChessKey(Key))
                {
                    return["1", "2", "3", "4"];
                }

                if (IsModelChessTypeKey(Key))
                {
                    return["0", "1"];
                }

                if (IsCellModelSelectionKey(Key))
                {
                    return["1", "2", "3", "4", "5"];
                }

                return Value.Trim()is "0" or "1" ? ["0", "1"] : ["OFF", "ON"];
            }
        }

        public void SetValueOptions(IReadOnlyList<string> valueOptions)
        {
            _valueOptions = valueOptions;
            OnPropertyChanged(nameof(UsesSelectionEditor));
            OnPropertyChanged(nameof(ValueOptions));
        }

        public double ChangeLimit { get; }
        public double Min { get; }
        public double Max { get; }

        public bool IsEdited
        {
            get
            {
                return !NormalizeValue(Value).Equals(NormalizeValue(_initialValue), StringComparison.OrdinalIgnoreCase);
            }
        }

        public string ValueState
        {
            get
            {
                return _valueState;
            }

            private set
            {
                if (SetProperty(ref _valueState, value))
                {
                    OnPropertyChanged(nameof(ValueBrush));
                }
            }
        }

        public Brush ValueBrush
        {
            get
            {
                return ValueState switch
                {
                    "Accent" => CStatusBrush.Simul,
                    "Warn" => CStatusBrush.Wait,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }

        private static string NormalizeValue(string value)
        {
            return value.Trim();
        }

        private static bool IsCellModelSelectionKey(string key)
        {
            if (key.Equals("CELL_MODEL_TYPE_COUNT", StringComparison.OrdinalIgnoreCase) || key.Equals("CELL_MODEL_TYPE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return key.StartsWith("CELL", StringComparison.OrdinalIgnoreCase) && key.EndsWith("_MODEL_TYPE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelChessKey(string key)
        {
            if (!key.StartsWith("MODEL", StringComparison.OrdinalIgnoreCase) || !key.EndsWith("_CHESS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var start = "MODEL".Length;
            var end = key.IndexOf('_', start);
            return end > start && int.TryParse(key.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var modelType) && modelType is >= 1 and <= 5;
        }

        private static bool IsModelChessTypeKey(string key)
        {
            if (!key.StartsWith("MODEL", StringComparison.OrdinalIgnoreCase) || !key.EndsWith("_CHESS_TYPE", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var start = "MODEL".Length;
            var end = key.IndexOf('_', start);
            return end > start && int.TryParse(key.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var modelType) && modelType is >= 1 and <= 5;
        }
    }

    public sealed record ST_RECIPE_HISTORY_ROW(string Time, string Action, string Tab, string Group, string Item, string Before, string After, string Recipe = "")
    {
        public Brush AfterBrush
        {
            get
            {
                return CStatusBrush.Wait;
            }
        }
    }

    public sealed record ST_RECIPE_STATE_ROW(string Name, string Value, string State = "Normal")
    {
        public Brush ValueBrush
        {
            get
            {
                return State switch
                {
                    "Accent" => CStatusBrush.Simul,
                    "Warn" => CStatusBrush.Wait,
                    "Ok" => CStatusBrush.Online,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    /// <summary>Recipe edit draft only. All geometry/timing checks are shared with SAVE; no device/file polling.</summary>
    public sealed class CRecipeZeroDefencePanel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private readonly Action<IReadOnlyDictionary<string, string>> _write;
        private CZeroDefenceRecipePlan? _plan;
        private string _signature = "";
        private readonly Dictionary<int, ST_ZERO_DEFENCE_POINT> _points = new();
        private readonly Dictionary<int, string> _invalid = new();
        private readonly Dictionary<int, IReadOnlyDictionary<string, ShotAvailability>> _availability = new();
        private IReadOnlyList<ST_SYSTEM_PARAMETER> _settings = [];
        private ST_HEAD_SHOT_POINT? _candidate;
        private int _head = 1, _cell, _count = 2, _group = 1;
        private IReadOnlyDictionary<string, ShotAvailability> _visibleAvailability = new Dictionary<string, ShotAvailability>();
        public CRecipeZeroDefencePanel(Action<IReadOnlyDictionary<string, string>> write)
        {
            _write = write;
            SelectGroupCommand = new(p => SelectGroup(Convert.ToInt32(p, CultureInfo.InvariantCulture)));
            SelectCellCommand = new(p => SelectCell(Convert.ToInt32(p, CultureInfo.InvariantCulture)), _ => !IsAll);
            SelectShotCommand = new(p => SelectShot(p?.ToString() ?? ""), p => CanEdit && !IsAll && p is string key && _visibleAvailability.TryGetValue(key, out var shot) && shot.CanSelect);
            SelectBeamCommand = new(p => SelectBeam(Convert.ToInt32(p, CultureInfo.InvariantCulture)), p => CanEdit && !IsAll && int.TryParse(p?.ToString(), out var beam) && Beams.Any(b => b.Number == beam && b.CanSelect));
            SelectCountCommand = new(p => ChangeCount(Convert.ToInt32(p, CultureInfo.InvariantCulture)), _ => CanEdit && !IsAll);
            ClearGroupCommand = new(_ => ClearGroup(), _ => CanEdit && !IsAll);
            BackToGlassCommand = new(_ =>
            {
                _cell = 0;
                RefreshView();
            });
        }

        public CButtonCommand SelectCellCommand { get; }
        public CButtonCommand SelectGroupCommand { get; }
        public CButtonCommand SelectShotCommand { get; }
        public CButtonCommand SelectBeamCommand { get; }
        public CButtonCommand SelectCountCommand { get; }
        public CButtonCommand ClearGroupCommand { get; }
        public CButtonCommand BackToGlassCommand { get; }
        public bool CanEdit { get; private set; }
        public int PointCount => _count;
        public bool IsTwo => _count == 2;
        public bool IsFour => _count == 4;
        public string Status { get; private set; } = "그룹 카드를 선택한 뒤 Cell·Shot·Beam을 지정해 주세요.";
        public Brush StatusBrush { get; private set; } = Brushes.DarkGoldenrod;
        public string TimingSummary { get; private set; } = "";
        public string Summary => $"{_points.Count} / 8 Heads 지정 · {_count}개씩 {8 / Math.Max(1, _count)}장 순환 · 저장: Recipe SAVE";
        private int GroupSize => _count == 4 ? 4 : 2;
        public bool IsAll => _group == 0;
        public int SelectedGroupNo => _group;
        private IReadOnlyList<int> ScopeHeads => IsAll ? Enumerable.Range(1, 8).ToArray() : Enumerable.Range((_group - 1) * GroupSize + 1, GroupSize).ToArray();
        private string ScopeTitle => IsAll ? "ALL HEADS · 조회 전용" : $"HEAD {string.Join("/", ScopeHeads)} · GROUP{_group}";
        public string PreviewTitle => _cell == 0 ? ScopeTitle + " · GLASS PREVIEW" : $"{ScopeTitle} · Cell{_cell:00} · SHOT";
        public string CandidateText => IsAll ? "ALL · Cell 선택 없이 전체 지정 현황을 확인합니다." : _candidate is null ? "Shot을 고르면 담당 Head와 Beam별 선택 가능 여부가 표시됩니다." : $"HEAD {_candidate.HeadNo:00} · Cell{_candidate.CellNo:00}-{CReviewShotNameFormatter.ToMatrixName(_candidate.CellShotNo, _candidate.GroupCountX)} · Beam 선택 시 반영";
        public Visibility GlassVisibility => _cell == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShotVisibility => _cell > 0 ? Visibility.Visible : Visibility.Collapsed;
        public ImageSource? PreviewImage { get; private set; }
        public ST_GLASS_PREVIEW_FRAME GlassFrame { get; private set; } = new(44, 62, 772, 420);
        public IReadOnlyList<CZeroDefenceCell> Cells { get; private set; } = [];
        public IReadOnlyList<CZeroDefenceShotRow> ShotRows { get; private set; } = [];
        public IReadOnlyList<CZeroDefenceHeadRow> HeadRows { get; private set; } = [];
        public IReadOnlyList<CZeroDefenceBeam> Beams { get; private set; } = [];
        public IReadOnlyList<CZeroDefenceGroupCard> GroupCards { get; private set; } = [];
        public IReadOnlyList<CZeroDefenceSelectionLabel> SelectionLabels { get; private set; } = [];
        private bool SideLabelsFit => Math.Min(GlassFrame.CanvasLeft, 860 - GlassFrame.CanvasLeft - GlassFrame.Width) >= 150;
        public double PreviewCanvasHeight => IsAll && !SideLabelsFit ? 636 : 520;

        private void SelectGroup(int group)
        {
            if (group < 0 || group > 8 / GroupSize)
                return;
            _group = group;
            _head = IsAll ? 0 : ScopeHeads[0];
            _cell = 0;
            _candidate = null;
            UpdateStatus();
            RefreshView();
        }

        public void Update(ST_RECIPE_DEFINITION? recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, bool canEdit)
        {
            CanEdit = canEdit;
            var signature = recipe is null ? "" : recipe.Id + "|" + string.Join(";", recipe.Parameters.Select(p => p.Key + "=" + p.Value)) + "|" + string.Join(";", settings.Select(p => p.Key + "=" + p.Value));
            if (_signature == signature && _plan is not null)
            {
                RefreshView();
                return;
            }

            _signature = signature;
            _settings = settings;
            _points.Clear();
            _invalid.Clear();
            _availability.Clear();
            _candidate = null;
            try
            {
                _plan = recipe is null ? null : new(recipe, settings);
                if (_plan is null)
                {
                    Status = "레시피를 선택해 주세요.";
                    RefreshView();
                    return;
                }

                _count = _plan.Count;
                _group = Math.Clamp(_group, 0, 8 / GroupSize);
                if (!ScopeHeads.Contains(_head))
                    _head = IsAll ? 0 : ScopeHeads[0];
                for (var head = 1; head <= 8; head++)
                {
                    var cell = _plan.Int(CZeroDefenceRecipePlan.Key(head, "CELL"));
                    var shot = _plan.Int(CZeroDefenceRecipePlan.Key(head, "SHOT"));
                    var beam = _plan.Int(CZeroDefenceRecipePlan.Key(head, "BEAM"));
                    if (cell == 0 && shot == 0 && beam == 0)
                        continue;
                    try
                    {
                        _points[head] = _plan.GetPoint(head, cell, shot, beam);
                    }
                    catch (Exception error)when (error is InvalidOperationException or ArgumentException)
                    {
                        _invalid[head] = error.Message;
                    }
                }

                RestoreCandidate();
                var p = _plan.Profile;
                TimingSummary = $"Stage Y {p.StageSpeed:0.###} mm/sec · Acc {p.StageAcceleration:0.###} mm/sec² · MCC Stroke {p.StageStroke:0} mm\n" + $"Review X {p.ReviewSpeed:0.###} mm/sec · Acc {p.ReviewAcceleration:0.###} mm/sec²\n" + "안정화 50 ms · 선도착 20 ms · 20 FPS · 노출 10 µs";
                UpdateStatus();
            }
            catch (Exception error)when (error is InvalidOperationException or ArgumentException)
            {
                _plan = null;
                Status = error.Message;
                StatusBrush = Brushes.Firebrick;
            }

            RefreshView();
        }

        private void ChangeCount(int count)
        {
            if (!CanEdit || IsAll || _plan is null || count is not (2 or 4))
                return;
            var result = CZeroDefenceTiming.Validate(count, _points.Values, _plan.Profile);
            if (!result.IsValid)
            {
                Reject(result.Message);
                return;
            }

            try
            {
                _write(new Dictionary<string, string> { [CZeroDefenceRecipePlan.CountKey] = count.ToString(CultureInfo.InvariantCulture) });
                var firstHead = ScopeHeads[0];
                _count = count;
                _group = (firstHead - 1) / GroupSize + 1;
                _head = ScopeHeads[0];
                _cell = 0;
                _candidate = null;
                _availability.Clear();
                UpdateStatus();
                RefreshView();
            }
            catch (InvalidOperationException error)
            {
                Reject(error.Message);
            }
        }

        private void RestoreCandidate()
        {
            var selected = _points.GetValueOrDefault(_head);
            _candidate = selected is null ? null : _plan?.Plan.Shots.FirstOrDefault(p => p.ShotKey == selected.ShotKey);
        }

        private void SelectCell(int cell)
        {
            if (IsAll || !Cells.Any(c => c.Number == cell))
                return;
            _cell = cell;
            _candidate = null;
            RefreshView();
        }

        private void SelectShot(string key)
        {
            if (IsAll || !CanEdit)
                return;
            var scope = ScopeHeads.ToHashSet();
            var point = _plan?.Plan.Shots.FirstOrDefault(p => scope.Contains(p.HeadNo) && p.ShotKey == key);
            if (point is null || !_plan!.CanProcess(point))
                return;
            var availability = GetShotAvailability(point.HeadNo).GetValueOrDefault(key);
            if (availability is not { CanSelect: true })
            {
                Reject(availability?.Reason ?? "선택할 수 없는 Shot입니다.");
                return;
            }

            _head = point.HeadNo;
            _candidate = point;
            RefreshView();
            if (Beams.All(b => !b.CanSelect))
                Reject(Beams.FirstOrDefault()?.Reason ?? "선택할 수 있는 Beam이 없습니다.");
        }

        private void SelectBeam(int beam)
        {
            if (!CanEdit || IsAll || _plan is null || _candidate is null)
                return;
            try
            {
                var point = _plan.GetPoint(_head, _candidate.CellNo, _candidate.CellShotNo, beam);
                var validation = ValidateCandidate(point);
                if (!validation.IsValid)
                {
                    Reject(validation.Message);
                    return;
                }

                _write(new Dictionary<string, string> { [CZeroDefenceRecipePlan.Key(_head, "CELL")] = point.CellNo.ToString(CultureInfo.InvariantCulture), [CZeroDefenceRecipePlan.Key(_head, "SHOT")] = point.ShotNo.ToString(CultureInfo.InvariantCulture), [CZeroDefenceRecipePlan.Key(_head, "BEAM")] = beam.ToString(CultureInfo.InvariantCulture) });
                _points[_head] = point;
                _invalid.Remove(_head);
                InvalidateOtherHeadsInGroup();
                UpdateStatus();
                RefreshView();
            }
            catch (Exception error)when (error is InvalidOperationException or ArgumentException)
            {
                Reject(error.Message);
            }
        }

        private ST_ZERO_DEFENCE_TIMING_RESULT ValidateCandidate(ST_ZERO_DEFENCE_POINT point)
        {
            var result = CZeroDefenceTiming.Validate(_count, _points.Values.Where(p => p.HeadNo != point.HeadNo).Append(point), _plan!.Profile);
            var group = (point.HeadNo - 1) / Math.Max(1, _count) + 1;
            // Permit repairing one glass group while another stored group still needs correction.
            // SAVE always validates all groups together.
            return new(result.Issues.Where(i => i.GroupNo == 0 || i.GroupNo == group).ToArray(), result.Transitions.Where(t => t.GroupNo == group).ToArray());
        }

        private void InvalidateOtherHeadsInGroup()
        {
            var groupSize = Math.Max(1, _count);
            var group = (_head - 1) / groupSize;
            foreach (var head in _availability.Keys.Where(head => head != _head && (head - 1) / groupSize == group).ToArray())
                _availability.Remove(head);
        }

        private IReadOnlyDictionary<string, ShotAvailability> GetShotAvailability(int head)
        {
            if (_plan is null)
                return new Dictionary<string, ShotAvailability>();
            if (_availability.TryGetValue(head, out var cached))
                return cached;
            // Evaluate a Head once per recipe/settings/group-selection change. Cell navigation
            // and candidate highlighting reuse this snapshot; no timer or device reads are involved.
            var result = new Dictionary<string, ShotAvailability>();
            foreach (var shot in _plan.Plan.Shots.Where(shot => shot.HeadNo == head))
            {
                var mask = _plan.Mask(shot);
                if (mask.ShouldSkipLaser)
                {
                    result[shot.ShotKey] = new(false, mask.IsMasked ? "MASKING" : "CHESS");
                    continue;
                }

                var reason = "선택 가능한 Beam이 없습니다.";
                var canSelect = false;
                for (var beam = 1; beam <= 16; beam++)
                {
                    try
                    {
                        var point = _plan.GetPoint(head, shot.CellNo, shot.CellShotNo, beam);
                        var check = ValidateCandidate(point);
                        if (check.IsValid)
                        {
                            canSelect = true;
                            break;
                        }

                        if (beam == 1)
                            reason = check.Message;
                    }
                    catch (Exception error)when (error is InvalidOperationException or ArgumentException)
                    {
                        if (beam == 1)
                            reason = error.Message;
                    }
                }

                result[shot.ShotKey] = new(canSelect, canSelect ? "" : reason);
            }

            _availability[head] = result;
            return result;
        }

        private sealed record ShotAvailability(bool CanSelect, string Reason);
        private void ClearGroup()
        {
            if (!CanEdit || IsAll)
                return;
            try
            {
                _write(ScopeHeads.SelectMany(head => new[] { "CELL", "SHOT", "BEAM" }.Select(field => CZeroDefenceRecipePlan.Key(head, field))).ToDictionary(key => key, _ => "0"));
                foreach (var head in ScopeHeads)
                {
                    _points.Remove(head);
                    _invalid.Remove(head);
                    _availability.Remove(head);
                }

                _candidate = null;
                UpdateStatus();
                RefreshView();
            }
            catch (InvalidOperationException error)
            {
                Reject(error.Message);
            }
        }

        private void UpdateStatus()
        {
            if (_plan is null)
                return;
            var check = CZeroDefenceTiming.Validate(_count, _points.Values, _plan.Profile);
            Status = _invalid.Count > 0 ? string.Join("\n", _invalid.Values) : !check.IsValid ? check.Message : _points.Count < 8 ? $"{8 - _points.Count}개 Head가 미지정입니다. 각 Head의 Shot·Beam을 지정해 주세요." : "전체 8개 Head의 MCC 이동·촬영 조건이 유효합니다.";
            StatusBrush = _invalid.Count > 0 || !check.IsValid ? Brushes.Firebrick : _points.Count == 8 ? Brushes.SeaGreen : Brushes.DarkGoldenrod;
        }

        private void Reject(string reason)
        {
            Status = reason;
            StatusBrush = Brushes.Firebrick;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusBrush));
        }

        private void OnPropertyChanged(string? property = null) => PropertyChanged?.Invoke(this, new(property));
        private void RefreshView()
        {
            var result = _plan is null ? new ST_ZERO_DEFENCE_TIMING_RESULT([], []) : CZeroDefenceTiming.Validate(_count, _points.Values, _plan.Profile);
            HeadRows = Enumerable.Range(1, 8).Select(head =>
            {
                var point = _points.GetValueOrDefault(head);
                var bad = _invalid.GetValueOrDefault(head) ?? result.Issues.FirstOrDefault(i => i.FromHead == head || i.ToHead == head)?.Reason;
                return new CZeroDefenceHeadRow(head, (head - 1) / Math.Max(1, _count) + 1, point, bad is not null ? "확인 필요" : point is null ? "미지정" : "지정 완료", bad ?? "", _candidate?.HeadNo == head);
            }).ToArray();
            GroupCards = Enumerable.Range(1, 8 / GroupSize).Select(group =>
            {
                var heads = Enumerable.Range((group - 1) * GroupSize + 1, GroupSize).ToArray();
                return new CZeroDefenceGroupCard(group, "HEAD " + string.Join("/", heads), $"GROUP{group} · {heads.Count(head => _points.ContainsKey(head))}/{GroupSize}", group == _group);
            }).Append(new CZeroDefenceGroupCard(0, "ALL", $"조회 전용 · {_points.Count}/8", IsAll)).ToArray();
            if (_plan is not null)
            {
                var scope = ScopeHeads.ToHashSet();
                var headShots = _plan.Plan.Shots.Where(p => scope.Contains(p.HeadNo)).ToArray();
                _visibleAvailability = IsAll ? new Dictionary<string, ShotAvailability>() : ScopeHeads.SelectMany(head => GetShotAvailability(head)).ToDictionary(pair => pair.Key, pair => pair.Value);
                var cellCounts = headShots.GroupBy(shot => shot.CellNo).ToDictionary(group => group.Key, group => (Total: group.Count(), Available: IsAll ? group.Count() : group.Count(shot => _visibleAvailability[shot.ShotKey].CanSelect), Heads: group.Select(shot => shot.HeadNo).Distinct().Order().ToArray()));
                var visible = cellCounts.Keys.ToHashSet();
                var preview = CHeadAssignmentPreviewBuilder.BuildRecipePreview(_plan.Parameters, scope, CHeadAssignmentPreviewBuilder.BuildHeadLayout(_settings), showKeys: false, visibleCellNos: visible);
                PreviewImage = preview.Image;
                GlassFrame = preview.Frame;
                Cells = preview.CellLabels.Select(c => new CZeroDefenceCell(
                    c.CellNo, 
                    c.CellBounds.IsEmpty ? new Rect(c.CanvasCenterX - 4, c.CanvasCenterY - 4, 8, 8) : c.CellBounds, 
                    _points.Values.Any(p => scope.Contains(p.HeadNo) && p.CellNo == c.CellNo), 
                    cellCounts[c.CellNo].Available, 
                    cellCounts[c.CellNo].Total, 
                    cellCounts[c.CellNo].Heads, 
                    string.Join("/", _points.Values.Where(p => scope.Contains(p.HeadNo) && p.CellNo == c.CellNo).OrderBy(p => p.HeadNo).Select(p => $"H{p.HeadNo}")), 
                    !IsAll
                )).ToArray();
                SelectionLabels = !IsAll ? [] : _points.Values.OrderBy(point => point.HeadNo).Select(point =>
                {
                    var cell = Cells.FirstOrDefault(cell => cell.Number == point.CellNo);
                    var anchor = cell is null ? new Point(GlassFrame.CanvasLeft + GlassFrame.Width / 2, GlassFrame.CanvasTop) : new Point(cell.Bounds.X + cell.Bounds.Width / 2, cell.Bounds.Y + cell.Bounds.Height / 2);
                    var onLeft = point.HeadNo <= 4;
                    var width = SideLabelsFit ? Math.Min(210, Math.Min(GlassFrame.CanvasLeft, 860 - GlassFrame.CanvasLeft - GlassFrame.Width) - 40) : 202;
                    var left = SideLabelsFit ? onLeft ? GlassFrame.CanvasLeft - width - 24 : GlassFrame.CanvasLeft + GlassFrame.Width + 24 : 14 + ((point.HeadNo - 1) % 4) * 212;
                    var top = SideLabelsFit ? GlassFrame.CanvasTop + ((point.HeadNo - 1) % 4) * Math.Max(0, (GlassFrame.Height - 44) / 3) : 526 + ((point.HeadNo - 1) / 4) * 52;
                    var endpoint = SideLabelsFit ? new Point(onLeft ? left + width : left, top + 22) : new Point(left + width / 2, top);
                    return new CZeroDefenceSelectionLabel($"HEAD {point.HeadNo:00} · Cell{point.CellNo:00}\n{point.ShotName} · Beam{point.BeamNo}", left, top, width, new PointCollection { anchor, new(endpoint.X, anchor.Y), endpoint });
                }).ToArray();
                ShotRows = headShots.Where(p => !IsAll && p.CellNo == _cell).GroupBy(p => p.GroupRow).OrderBy(g => g.Key).Select(g => new CZeroDefenceShotRow(g.OrderBy(p => p.GroupColumn).Select(p =>
                {
                    var mask = _plan.Mask(p);
                    var eligibility = _visibleAvailability[p.ShotKey];
                    return new CZeroDefenceShot(
                        p.ShotKey, 
                        CReviewShotNameFormatter.ToMatrixName(p.CellShotNo, p.GroupCountX), 
                        eligibility.CanSelect, 
                        mask.IsMasked, 
                        mask.IsChessSkipped, 
                        eligibility.Reason, 
                        _candidate?.ShotKey == p.ShotKey, 
                        _points.GetValueOrDefault(p.HeadNo)?.ShotKey == p.ShotKey, 
                        p.HeadNo
                    );
                }).ToArray())).ToArray();
            }
            else
            {
                PreviewImage = null;
                Cells = [];
                ShotRows = [];
                SelectionLabels = [];
                _visibleAvailability = new Dictionary<string, ShotAvailability>();
            }

            Beams = Enumerable.Range(1, 16).Select(beam =>
            {
                if (_plan is null || _candidate is null)
                    return new CZeroDefenceBeam(beam, null, false, "Shot을 선택해 주세요.", false);
                try
                {
                    var point = _plan.GetPoint(_head, _candidate.CellNo, _candidate.CellShotNo, beam);
                    var check = ValidateCandidate(point);
                    return new CZeroDefenceBeam(beam, point, CanEdit && check.IsValid, check.Message, _points.GetValueOrDefault(_head)is { } selected && selected.ShotKey == point.ShotKey && selected.BeamNo == beam);
                }
                catch (Exception error)when (error is InvalidOperationException or ArgumentException)
                {
                    return new CZeroDefenceBeam(beam, null, false, error.Message, false);
                }
            }).ToArray();
            foreach (var command in new[]
            {
                SelectCellCommand,
                SelectBeamCommand,
                SelectShotCommand,
                SelectCountCommand,
                ClearGroupCommand
            }

            )
                command.NotifyCanExecuteChanged();
            OnPropertyChanged(null);
        }
    }

    public sealed record CZeroDefenceHeadRow(int HeadNo, int GroupNo, ST_ZERO_DEFENCE_POINT? Point, string State, string Detail, bool IsSelected)
    {
        public string Head => $"H{HeadNo:00}";
        public string Cell => Point is null ? "-" : $"Cell{Point.CellNo:00}";
        public string Shot => Point?.ShotName ?? "-";
        public string Beam => Point?.BeamNo.ToString(CultureInfo.InvariantCulture) ?? "-";
        public string Coordinates => Point is null ? "" : $"Review X {Point.ReviewX:0.000} / Stage Y {Point.StageY:0.000} mm";
        public string Tooltip => Coordinates + (Detail.Length == 0 ? "" : "\n" + Detail);
    }

    public sealed record CZeroDefenceGroupCard(int Number, string Name, string Detail, bool IsSelected)
    {
        public Brush Background => IsSelected ? Brushes.SteelBlue : Brushes.AliceBlue;
        public Brush Foreground => IsSelected ? Brushes.White : Brushes.Black;
    }

    public sealed record CZeroDefenceSelectionLabel(string Text, double Left, double Top, double Width, PointCollection Leader)
    {
        public double FontSize => Math.Min(16, Width / 12);
    }

    public sealed record CZeroDefenceCell(int Number, Rect Bounds, bool HasSelection, int SelectableShotCount, int TotalShotCount, IReadOnlyList<int> HeadNos, string SelectedHeads, bool CanOpen)
    {
        public bool IsUnavailable => CanOpen && SelectableShotCount == 0;
        public string Detail => $"Cell{Number:00} · HEAD {string.Join("/", HeadNos)}" + (CanOpen ? $" · 선택 가능 {SelectableShotCount:N0} / {TotalShotCount:N0} Shot" : " · 조회 전용") + (HasSelection ? "\n지정: " + SelectedHeads : "") + (IsUnavailable ? "\n선택 가능한 Beam이 있는 Shot이 없습니다. Cell을 눌러 Shot별 사유를 확인해 주세요." : "");
    }

    public sealed record CZeroDefenceShotRow(IReadOnlyList<CZeroDefenceShot> Shots);
    public sealed record CZeroDefenceShot(string Key, string Name, bool CanSelect, bool IsMasked, bool IsChess, string UnavailableReason, bool IsCandidate, bool IsSelected, int HeadNo)
    {
        public string Label => $"{Name}\nH{HeadNo:00}";
        public Brush Background => IsMasked ? Brushes.LightCoral : IsChess ? Brushes.Plum : !CanSelect ? Brushes.LightGray : IsSelected ? Brushes.MediumSeaGreen : IsCandidate ? Brushes.LightSkyBlue : Brushes.White;
        public string Detail => IsMasked ? "MASKING" : IsChess ? "CHESS" : !CanSelect ? "선택 불가: 선택 가능한 Beam이 없습니다.\n" + UnavailableReason : IsSelected ? "등록된 Shot" : "Shot 선택 후 Beam을 선택해 주세요.";
    }

    public sealed record CZeroDefenceBeam(int Number, ST_ZERO_DEFENCE_POINT? Point, bool CanSelect, string Reason, bool IsSelected)
    {
        public string Name => $"Beam{Number}";
        public string Coordinates => Point is null ? "X -\nY -" : $"X {Point.ReviewX:0.000}\nY {Point.StageY:0.000}";
        public Brush Background => IsSelected ? Brushes.SeaGreen : CanSelect ? Brushes.White : Brushes.MistyRose;
        public Brush Foreground => IsSelected ? Brushes.White : Brushes.Black;
        public string Detail => CanSelect ? "Review X / Stage Y · 선택 가능" : Reason;
    }
}
