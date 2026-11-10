namespace Drilling.UI.Menu.Menus
{
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Windows.Media;
    using Drilling.Common.Managers;
    using Drilling.Common.Recipe;
    using Drilling.Common.Review;
    using Drilling.UI.Menu;

    public sealed partial class CMenuCorrection
    {
        public sealed partial class CReviewDataPanel : INotifyPropertyChanged
        {
            private readonly CReviewResultFileBase _reviewResultFile;
            private readonly CReviewOffsetFileBase _reviewOffsetFile;
            private readonly CReviewManager _reviewManager;
            private readonly CRecipeManager _recipeManager;
            private readonly CSettingManager _settingManager;
            private readonly Func<string> _selectedRecipeIdProvider;
            private readonly Action<string> _statusReporter;
            private readonly Func<Task> _rollbackReviewOffset;
            private bool _isBusy;
            private ST_REVIEW_RESULT_TREE_NODE? _selectedNode;

            public CReviewDataPanel(
                CReviewResultFileBase reviewResultFile,
                CReviewOffsetFileBase reviewOffsetFile,
                CReviewManager reviewManager,
                CRecipeManager recipeManager,
                CSettingManager settingManager,
                Func<string> selectedRecipeIdProvider,
                Action<string> statusReporter,
                Func<Task> rollbackReviewOffset)
            {
                _reviewResultFile = reviewResultFile;
                _reviewOffsetFile = reviewOffsetFile;
                _reviewManager = reviewManager;
                _recipeManager = recipeManager;
                _settingManager = settingManager;
                _selectedRecipeIdProvider = selectedRecipeIdProvider;
                _statusReporter = statusReporter;
                _rollbackReviewOffset = rollbackReviewOffset;
                RefreshCommand = new CButtonCommand(RefreshRequested, _ => !IsBusy);
                LoadDataCommand = new CButtonCommand(LoadDataRequested, _ => CanLoadData);
                SelectCellCommand = new CButtonCommand(SelectCell);
                OffsetApplyCommand = new CButtonCommand(OffsetApplyRequested, _ => CanApplyOffset);
                OffsetRollbackCommand = new CButtonCommand(OffsetRollbackRequested, _ => CanEditOffsets);
                OffsetInitCommand = new CButtonCommand(OffsetInitRequested, _ => CanEditOffsets);
                OffsetViewCommand = new CButtonCommand(OffsetViewRequested, _ => CanEditOffsets);
                Nodes = BuildTree([]);
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public CButtonCommand RefreshCommand { get; }
            public CButtonCommand LoadDataCommand { get; }
            public CButtonCommand SelectCellCommand { get; }
            public CButtonCommand OffsetApplyCommand { get; }
            public CButtonCommand OffsetRollbackCommand { get; }
            public CButtonCommand OffsetInitCommand { get; }
            public CButtonCommand OffsetViewCommand { get; }
            public IReadOnlyList<ST_REVIEW_RESULT_TREE_NODE> Nodes { get; private set; }
            public IReadOnlyList<ST_REVIEW_DATA_DETAIL_ITEM> SelectedDetails { get; private set; } = [];
            public string StatusText { get; private set; } = "0 RESULTS";
            public string SelectedTitle { get; private set; } = "NO RESULT SELECTED";
            public string SelectedStatus { get; private set; } = "WAIT";
            public string SelectedPath { get; private set; } = "";
            public Brush SelectedStatusBrush => StatusBrush(SelectedStatus);

            public bool IsBusy
            {
                get => _isBusy;
                private set
                {
                    if (_isBusy == value)
                    {
                        return;
                    }

                    _isBusy = value;
                    Notify(nameof(IsBusy));
                    RefreshCommand.NotifyCanExecuteChanged();
                    NotifyLoadCommandState();
                }
            }

            public Task Reload(CancellationToken cancellationToken = default)
            {
                return Refresh(cancellationToken);
            }

            public void Select(ST_REVIEW_RESULT_TREE_NODE node)
            {
                _selectedNode = node;
                SelectedTitle = node.Name;
                SelectedStatus = node.Result?.ResultStatus ?? node.Kind;
                SelectedPath = node.Result?.FilePath ?? "";
                SelectedDetails = CreateDetails(node);
                Notify(nameof(SelectedTitle));
                Notify(nameof(SelectedStatus));
                Notify(nameof(SelectedStatusBrush));
                Notify(nameof(SelectedPath));
                Notify(nameof(SelectedDetails));
                PrepareSelectedResult(node.Result);
                if (node.Result is not null)
                {
                    _statusReporter($"Review Result selected: {node.Result.FileName}.");
                }
            }

            private async void RefreshRequested(object? parameter)
            {
                await Refresh();
            }

            private async Task Refresh(CancellationToken cancellationToken = default)
            {
                if (IsBusy)
                {
                    return;
                }

                IsBusy = true;
                try
                {
                    var selectedPath = _selectedNode?.Result?.FilePath;
                    var selectedRecipeId = NormalizeRecipeId(_selectedRecipeIdProvider());
                    var allResults = await _reviewResultFile.ListCorrectionResults(cancellationToken);
                    var results = string.IsNullOrWhiteSpace(selectedRecipeId)
                        ? Array.Empty<ST_REVIEW_RESULT_CATALOG_ITEM>()
                        : allResults
                            .Where(result => NormalizeRecipeId(result.RecipeId).Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    Nodes = BuildTree(results);
                    StatusText = $"{results.Length:N0} RESULTS";
                    Notify(nameof(Nodes));
                    Notify(nameof(StatusText));

                    if (!string.IsNullOrWhiteSpace(selectedPath))
                    {
                        var selected = Flatten(Nodes)
                            .FirstOrDefault(node => node.Result?.FilePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase) == true);
                        if (selected is not null)
                        {
                            Select(selected);
                            return;
                        }
                    }

                    ClearSelection();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    StatusText = "LOAD FAILED";
                    Notify(nameof(StatusText));
                    _statusReporter($"Review Result list load failed: {exception.Message}");
                }
                finally
                {
                    IsBusy = false;
                }
            }

            private void ClearSelection()
            {
                _selectedNode = null;
                SelectedTitle = "NO RESULT SELECTED";
                SelectedStatus = "WAIT";
                SelectedPath = "";
                SelectedDetails = [];
                PrepareSelectedResult(null);
                Notify(nameof(SelectedTitle));
                Notify(nameof(SelectedStatus));
                Notify(nameof(SelectedStatusBrush));
                Notify(nameof(SelectedPath));
                Notify(nameof(SelectedDetails));
            }

            private static IReadOnlyList<ST_REVIEW_RESULT_TREE_NODE> BuildTree(
                IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM> results)
            {
                var manualResults = results.Where(result => result.Source.Equals("MANUAL", StringComparison.OrdinalIgnoreCase)).ToArray();
                var manualRecipes = manualResults
                    .GroupBy(result => result.RecipeId, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(recipe => new ST_REVIEW_RESULT_TREE_NODE(
                        recipe.Key,
                        "RECIPE",
                        $"{recipe.Count():N0}",
                        new[] { "ALL_SHOT", "ONE_SHOT", "BEAM_INSPECTION", "ROUGH", "FINE" }
                            .Select(type =>
                            {
                                var typeResults = recipe.Where(result => result.ResultType.Equals(type, StringComparison.OrdinalIgnoreCase)).OrderByDescending(result => result.SavedAt).ToArray();
                                return new ST_REVIEW_RESULT_TREE_NODE(type.Replace('_', ' '), "TYPE", $"{typeResults.Length:N0}", typeResults.Select(CreateResultNode).ToArray(), null, false);
                            })
                            .ToArray(),
                        null,
                        false))
                    .ToArray();

                var autoResults = results.Where(result => result.Source.Equals("AUTO", StringComparison.OrdinalIgnoreCase)).ToArray();
                var autoRecipes = autoResults
                    .GroupBy(result => result.RecipeId, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(recipe => new ST_REVIEW_RESULT_TREE_NODE(recipe.Key, "RECIPE", $"{recipe.Count():N0}", recipe.OrderByDescending(result => result.SavedAt).Select(CreateResultNode).ToArray(), null, false))
                    .ToArray();

                var zeroResults = results.Where(result => result.Source.Equals("ZERO_DEFENCE", StringComparison.OrdinalIgnoreCase)).ToArray();
                var zeroRecipes = zeroResults
                    .GroupBy(result => result.RecipeId, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(recipe => new ST_REVIEW_RESULT_TREE_NODE(
                        recipe.Key,
                        "RECIPE",
                        $"{recipe.Count():N0}",
                        recipe.GroupBy(result => result.GlassId, StringComparer.OrdinalIgnoreCase)
                            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(glass => new ST_REVIEW_RESULT_TREE_NODE(glass.Key, "GLASS", $"{glass.Count():N0}", glass.OrderByDescending(result => result.SavedAt).Select(CreateResultNode).ToArray(), null, false))
                            .ToArray(),
                        null,
                        false))
                    .ToArray();

                var sourceNodes = new[]
                {
                    new ST_REVIEW_RESULT_TREE_NODE("MANUAL", "SOURCE", $"{manualResults.Length:N0}", manualRecipes, null, true),
                    new ST_REVIEW_RESULT_TREE_NODE("AUTO", "SOURCE", $"{autoResults.Length:N0}", autoRecipes, null, true),
                    new ST_REVIEW_RESULT_TREE_NODE("ZERO DEFENCE", "SOURCE", $"{zeroResults.Length:N0}", zeroRecipes, null, true)
                };

                return
                [
                    new ST_REVIEW_RESULT_TREE_NODE(
                        "REVIEW RESULTS",
                        "ROOT",
                        $"{results.Count:N0}",
                        sourceNodes,
                        null,
                        true)
                ];
            }

            private static ST_REVIEW_RESULT_TREE_NODE CreateResultNode(ST_REVIEW_RESULT_CATALOG_ITEM result)
            {
                return new ST_REVIEW_RESULT_TREE_NODE(result.FileName, "RESULT", "", [], result, false);
            }

            private static IReadOnlyList<ST_REVIEW_DATA_DETAIL_ITEM> CreateDetails(ST_REVIEW_RESULT_TREE_NODE node)
            {
                if (node.Result is not { } result)
                {
                    return [new("Result Count", CountResults(node).ToString("N0", CultureInfo.CurrentCulture))];
                }

                var details = new List<ST_REVIEW_DATA_DETAIL_ITEM>
                {
                    new("Source", result.Source),
                    new("Type", result.ResultType),
                    new("Recipe", result.RecipeId),
                    new("Saved At", result.SavedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    new("Glass", result.GlassId),
                    new("Run Type", result.RunType),
                    new("Method", result.Method),
                    new("Total Count", result.TotalCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "-")
                };
                if (!string.IsNullOrWhiteSpace(result.ResultMessage))
                {
                    details.Add(new("Message", result.ResultMessage));
                }

                return details;
            }

            private static IEnumerable<ST_REVIEW_RESULT_TREE_NODE> Flatten(
                IEnumerable<ST_REVIEW_RESULT_TREE_NODE> nodes)
            {
                foreach (var node in nodes)
                {
                    yield return node;
                    foreach (var child in Flatten(node.Children))
                    {
                        yield return child;
                    }
                }
            }

            private static int CountResults(ST_REVIEW_RESULT_TREE_NODE node)
            {
                return node.Result is null ? node.Children.Sum(CountResults) : 1;
            }

            internal static Brush StatusBrush(string status)
            {
                var displayState = status.ToUpperInvariant() switch
                {
                    "COMPLETED" or "DONE" => "OK",
                    "FAILED" or "INVALID" => "ERROR",
                    "STOPPED" or "NG_PAUSED" => "WARN",
                    _ => status
                };
                return CStatusBrush.ForDisplayState(displayState);
            }

            private void Notify(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public sealed record ST_REVIEW_RESULT_TREE_NODE(
            string Name,
            string Kind,
            string Detail,
            IReadOnlyList<ST_REVIEW_RESULT_TREE_NODE> Children,
            ST_REVIEW_RESULT_CATALOG_ITEM? Result,
            bool IsExpanded)
        {
            public Brush DetailBrush => CReviewDataPanel.StatusBrush(Result?.ResultStatus ?? Kind);
            public bool IsDirectory => Result is null;
        }

        public sealed record ST_REVIEW_DATA_DETAIL_ITEM(string Name, string Value);
    }
}
