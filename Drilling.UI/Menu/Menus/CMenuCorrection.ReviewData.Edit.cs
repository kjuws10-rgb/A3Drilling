namespace Drilling.UI.Menu.Menus
{
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Windows.Media;
    using Drilling.Common.Review;
    using Drilling.UI.Menu;
    using Drilling.UI.Popup;

    public sealed partial class CMenuCorrection
    {
        public sealed partial class CReviewDataPanel
        {
            private const string ResultEditKeyInMode = "KEY_IN";
            private const string ResultEditCopyMode = "COPY";
            private static readonly IReadOnlyList<string> ResultJudgeOptions = ["OK", "NG"];
            private string _resultEditMode = ResultEditKeyInMode;
            private IReadOnlyList<ST_REVIEW_DATA_EDIT_ITEM> _resultEditKeyInItems = [];
            private ST_REVIEW_DATA_EDIT_ITEM? _selectedResultEditKeyInItem;
            private ST_REVIEW_DATA_EDIT_ITEM? _resultEditCopySource;
            private IReadOnlyList<ST_REVIEW_DATA_EDIT_ITEM> _resultEditCopyTargets = [];
            private ST_REVIEW_DATA_EDIT_ITEM? _selectedResultEditCopyTarget;
            private string _resultEditStatusText = "NO EDIT ITEMS";
            private bool _isResultEditSaving;

            public bool IsResultEditKeyInMode => _resultEditMode == ResultEditKeyInMode;
            public bool IsResultEditCopyMode => _resultEditMode == ResultEditCopyMode;
            public IReadOnlyList<string> ResultEditJudgeOptions => ResultJudgeOptions;
            public IReadOnlyList<ST_REVIEW_DATA_EDIT_ITEM> ResultEditKeyInItems => _resultEditKeyInItems;
            public IReadOnlyList<ST_REVIEW_DATA_EDIT_ITEM> ResultEditCopySourceRows => _resultEditCopySource is null ? [] : [_resultEditCopySource];
            public IReadOnlyList<ST_REVIEW_DATA_EDIT_ITEM> ResultEditCopyTargets => _resultEditCopyTargets;
            public string ResultEditSelectionSummary => $"{_allRows.Count(row => row.IsResultEditSelected):N0} SELECTED";
            public string ResultEditKeyInSummary => $"{_resultEditKeyInItems.Count:N0} SHOTS";
            public string ResultEditCopySummary => $"{_resultEditCopyTargets.Count:N0} TARGETS";
            public string ResultEditStatusText => _resultEditStatusText;
            public bool IsResultEditSaving => _isResultEditSaving;
            public bool CanAddResultEditKeyIn => _isResultEditMode && IsResultEditKeyInMode && SelectedRow is not null;
            public bool CanAddSelectedResultEditKeyIn => _isResultEditMode && IsResultEditKeyInMode && _allRows.Any(row => row.IsResultEditSelected);
            public bool CanAddNgResultEditKeyIn => _isResultEditMode && IsResultEditKeyInMode && _allRows.Any(IsNgResultRow);
            public bool CanRemoveResultEditKeyIn => _isResultEditMode && _selectedResultEditKeyInItem is not null;
            public bool CanClearResultEditKeyIn => _isResultEditMode && _resultEditKeyInItems.Count > 0;
            public bool CanAddResultEditCopySource => _isResultEditMode && ResolveResultEditSelection().Count > 0;
            public bool CanAddResultEditCopyTargets => _isResultEditMode && _resultEditCopySource is not null && ResolveResultEditSelection().Count > 0;
            public bool CanRemoveResultEditCopyTarget => _isResultEditMode && _selectedResultEditCopyTarget is not null;
            public bool CanClearResultEditCopyTargets => _isResultEditMode && _resultEditCopyTargets.Count > 0;
            public bool CanApplyResultEditCopy => _isResultEditMode && _resultEditCopySource is not null && _resultEditCopyTargets.Count > 0;
            public bool CanSaveResultEdit => _isResultEditMode
                && !_isResultEditSaving
                && _loadedResultFile is not null
                && !IsBeamInspectionResult
                && (IsResultEditKeyInMode ? _resultEditKeyInItems.Count > 0 : _resultEditCopyTargets.Count > 0);

            public ST_REVIEW_DATA_EDIT_ITEM? SelectedResultEditKeyInItem
            {
                get => _selectedResultEditKeyInItem;
                set
                {
                    if (ReferenceEquals(_selectedResultEditKeyInItem, value))
                    {
                        return;
                    }

                    _selectedResultEditKeyInItem = value;
                    Notify(nameof(SelectedResultEditKeyInItem));
                    NotifyResultEditCommandState();
                }
            }

            public ST_REVIEW_DATA_EDIT_ITEM? SelectedResultEditCopyTarget
            {
                get => _selectedResultEditCopyTarget;
                set
                {
                    if (ReferenceEquals(_selectedResultEditCopyTarget, value))
                    {
                        return;
                    }

                    _selectedResultEditCopyTarget = value;
                    Notify(nameof(SelectedResultEditCopyTarget));
                    NotifyResultEditCommandState();
                }
            }

            public CButtonCommand SelectResultEditSubModeCommand { get; private set; } = null!;
            public CButtonCommand AddResultEditKeyInCommand { get; private set; } = null!;
            public CButtonCommand AddSelectedResultEditKeyInCommand { get; private set; } = null!;
            public CButtonCommand AddNgResultEditKeyInCommand { get; private set; } = null!;
            public CButtonCommand RemoveResultEditKeyInCommand { get; private set; } = null!;
            public CButtonCommand ClearResultEditKeyInCommand { get; private set; } = null!;
            public CButtonCommand AddResultEditCopySourceCommand { get; private set; } = null!;
            public CButtonCommand AddResultEditCopyTargetsCommand { get; private set; } = null!;
            public CButtonCommand RemoveResultEditCopyTargetCommand { get; private set; } = null!;
            public CButtonCommand ClearResultEditCopyTargetsCommand { get; private set; } = null!;
            public CButtonCommand CopyResultEditXyCommand { get; private set; } = null!;
            public CButtonCommand CopyResultEditJudgeCommand { get; private set; } = null!;
            public CButtonCommand CopyResultEditAllCommand { get; private set; } = null!;
            public CButtonCommand SaveResultEditCommand { get; private set; } = null!;

            private void InitializeResultEditCommands()
            {
                SelectResultEditSubModeCommand = new CButtonCommand(SelectResultEditSubMode);
                AddResultEditKeyInCommand = new CButtonCommand(AddResultEditKeyIn, _ => CanAddResultEditKeyIn);
                AddSelectedResultEditKeyInCommand = new CButtonCommand(AddSelectedResultEditKeyIn, _ => CanAddSelectedResultEditKeyIn);
                AddNgResultEditKeyInCommand = new CButtonCommand(AddNgResultEditKeyIn, _ => CanAddNgResultEditKeyIn);
                RemoveResultEditKeyInCommand = new CButtonCommand(RemoveResultEditKeyIn, _ => CanRemoveResultEditKeyIn);
                ClearResultEditKeyInCommand = new CButtonCommand(ClearResultEditKeyIn, _ => CanClearResultEditKeyIn);
                AddResultEditCopySourceCommand = new CButtonCommand(AddResultEditCopySource, _ => CanAddResultEditCopySource);
                AddResultEditCopyTargetsCommand = new CButtonCommand(AddResultEditCopyTargets, _ => CanAddResultEditCopyTargets);
                RemoveResultEditCopyTargetCommand = new CButtonCommand(RemoveResultEditCopyTarget, _ => CanRemoveResultEditCopyTarget);
                ClearResultEditCopyTargetsCommand = new CButtonCommand(ClearResultEditCopyTargets, _ => CanClearResultEditCopyTargets);
                CopyResultEditXyCommand = new CButtonCommand(CopyResultEditXy, _ => CanApplyResultEditCopy);
                CopyResultEditJudgeCommand = new CButtonCommand(CopyResultEditJudge, _ => CanApplyResultEditCopy);
                CopyResultEditAllCommand = new CButtonCommand(CopyResultEditAll, _ => CanApplyResultEditCopy);
                SaveResultEditCommand = new CButtonCommand(SaveResultEdit, _ => CanSaveResultEdit);
            }

            private void SelectResultEditSubMode(object? parameter)
            {
                var mode = parameter?.ToString()?.Trim().ToUpperInvariant();
                if (mode is not (ResultEditKeyInMode or ResultEditCopyMode) || _resultEditMode == mode)
                {
                    return;
                }

                _resultEditMode = mode;
                _resultEditStatusText = mode == ResultEditKeyInMode ? "KEY IN MODE READY" : "COPY MODE READY";
                NotifyResultEditState();
            }

            public void SetResultEditRowSelection(ST_REVIEW_DATA_GRID_ROW row, bool isSelected)
            {
                if (!_isResultEditMode || row is null)
                {
                    return;
                }

                row.IsResultEditSelected = isSelected;
                NotifyResultEditSelectionChanged();
            }

            public void SelectCurrentShotForResultEdit()
            {
                if (!_isResultEditMode || SelectedRow is null)
                {
                    return;
                }

                ReplaceResultEditSelection([SelectedRow]);
            }

            public void SelectResultEditShotsInMap(double left, double top, double right, double bottom)
            {
                if (!_isResultEditMode || !_shotsByCell.TryGetValue(_selectedCellNo, out var shots) || shots.Count == 0)
                {
                    return;
                }

                var first = shots[0];
                var mapRect = GetReviewDataShotMapRect(first.GroupCountX, first.GroupCountY);
                var selection = new System.Windows.Rect(
                    Math.Min(left, right),
                    Math.Min(top, bottom),
                    Math.Abs(right - left),
                    Math.Abs(bottom - top));
                var cellWidth = mapRect.Width / first.GroupCountX;
                var cellHeight = mapRect.Height / first.GroupCountY;
                var keys = shots
                    .Where(shot => selection.Contains(new System.Windows.Point(
                        mapRect.Left + (shot.GroupColumn + 0.5) * cellWidth,
                        mapRect.Top + (shot.GroupRow + 0.5) * cellHeight)))
                    .Select(shot => shot.ShotKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                ReplaceResultEditSelection(_allRows.Where(row => row.CellNo == _selectedCellNo && keys.Contains(row.ShotKey)));
            }

            public void SelectResultEditShotsInScatter(double left, double top, double right, double bottom)
            {
                if (!_isResultEditMode)
                {
                    return;
                }

                var selection = new System.Windows.Rect(
                    Math.Min(left, right),
                    Math.Min(top, bottom),
                    Math.Abs(right - left),
                    Math.Abs(bottom - top));
                ReplaceResultEditSelection(_scatterPlotHits.Where(hit => selection.Contains(hit.Point)).Select(hit => hit.Row));
            }

            private void ReplaceResultEditSelection(IEnumerable<ST_REVIEW_DATA_GRID_ROW> rows)
            {
                foreach (var row in _allRows)
                {
                    row.IsResultEditSelected = false;
                }

                foreach (var row in rows.Distinct())
                {
                    row.IsResultEditSelected = true;
                }

                NotifyResultEditSelectionChanged();
            }

            private void ClearResultEditSelection()
            {
                foreach (var row in _allRows)
                {
                    row.IsResultEditSelected = false;
                }

                NotifyResultEditSelectionChanged();
            }

            private void NotifyResultEditSelectionChanged()
            {
                Rows = _allRows.Where(row => row.CellNo == _selectedCellNo).ToArray();
                Notify(nameof(Rows));
                Notify(nameof(ResultEditSelectionSummary));
                RefreshScatterPlot();
                QueueShotMapRender(_selectedCellNo, _selectedShotKey);
                NotifyResultEditCommandState();
            }

            private IReadOnlyList<ST_REVIEW_DATA_GRID_ROW> ResolveResultEditSelection()
            {
                var selected = _allRows.Where(row => row.IsResultEditSelected).ToArray();
                return selected.Length > 0 ? selected : SelectedRow is null ? [] : [SelectedRow];
            }

            private void AddResultEditKeyIn(object? parameter)
            {
                if (SelectedRow is not { } selectedRow)
                {
                    return;
                }

                AddResultEditKeyInRows([selectedRow], "CURRENT SHOT");
            }

            private void AddSelectedResultEditKeyIn(object? parameter)
            {
                AddResultEditKeyInRows(
                    _allRows.Where(row => row.IsResultEditSelected),
                    "SELECTED SHOTS");
            }

            private void AddNgResultEditKeyIn(object? parameter)
            {
                AddResultEditKeyInRows(
                    _allRows.Where(IsNgResultRow),
                    "NG SHOTS");
            }

            private void AddResultEditKeyInRows(
                IEnumerable<ST_REVIEW_DATA_GRID_ROW> sourceRows,
                string sourceName)
            {
                var items = _resultEditKeyInItems.ToList();
                var addedCount = 0;
                foreach (var row in sourceRows.Distinct())
                {
                    if (items.All(item => !item.Matches(row)))
                    {
                        items.Add(new ST_REVIEW_DATA_EDIT_ITEM(row, items.Count + 1));
                        addedCount++;
                    }
                }

                _resultEditKeyInItems = Renumber(items);
                _selectedResultEditKeyInItem = _resultEditKeyInItems.LastOrDefault();
                _resultEditStatusText = $"{addedCount:N0} {sourceName} ADDED / {_resultEditKeyInItems.Count:N0} TOTAL";
                NotifyResultEditState();
            }

            private void RemoveResultEditKeyIn(object? parameter)
            {
                if (_selectedResultEditKeyInItem is null)
                {
                    return;
                }

                var items = _resultEditKeyInItems.Where(item => !ReferenceEquals(item, _selectedResultEditKeyInItem)).ToArray();
                _resultEditKeyInItems = Renumber(items);
                _selectedResultEditKeyInItem = _resultEditKeyInItems.FirstOrDefault();
                _resultEditStatusText = "KEY IN ITEM REMOVED";
                NotifyResultEditState();
            }

            private void ClearResultEditKeyIn(object? parameter)
            {
                _resultEditKeyInItems = [];
                _selectedResultEditKeyInItem = null;
                _resultEditStatusText = "KEY IN LIST CLEARED";
                NotifyResultEditState();
            }

            private void AddResultEditCopySource(object? parameter)
            {
                var selected = ResolveResultEditSelection();
                if (selected.Count != 1)
                {
                    CRecipeConfirmDialog.ShowWarning(
                        "Result Edit Source 확인",
                        selected.Count == 0
                            ? "SOURCE로 지정할 Shot을 하나 선택해 주세요."
                            : $"SOURCE는 하나의 Shot만 지정할 수 있습니다. 현재 {selected.Count:N0}개가 선택되었습니다.");
                    return;
                }

                _resultEditCopySource = new ST_REVIEW_DATA_EDIT_ITEM(selected[0], 1);
                _resultEditCopyTargets = _resultEditCopyTargets.Where(item => !item.Matches(selected[0])).ToArray();
                _selectedResultEditCopyTarget = _resultEditCopyTargets.FirstOrDefault();
                _resultEditStatusText = $"SOURCE: {_resultEditCopySource.Identity}";
                NotifyResultEditState();
            }

            private void AddResultEditCopyTargets(object? parameter)
            {
                if (_resultEditCopySource is null)
                {
                    CRecipeConfirmDialog.ShowWarning("Result Edit Copy 확인", "먼저 ADD TO SOURCE로 기준 Shot을 지정해 주세요.");
                    return;
                }

                var targets = _resultEditCopyTargets.ToList();
                foreach (var row in ResolveResultEditSelection())
                {
                    if (_resultEditCopySource.Matches(row) || targets.Any(item => item.Matches(row)))
                    {
                        continue;
                    }

                    targets.Add(new ST_REVIEW_DATA_EDIT_ITEM(row, targets.Count + 1));
                }

                _resultEditCopyTargets = Renumber(targets);
                _selectedResultEditCopyTarget = _resultEditCopyTargets.LastOrDefault();
                _resultEditStatusText = $"{_resultEditCopyTargets.Count:N0} COPY TARGETS READY";
                NotifyResultEditState();
            }

            private void RemoveResultEditCopyTarget(object? parameter)
            {
                if (_selectedResultEditCopyTarget is null)
                {
                    return;
                }

                var targets = _resultEditCopyTargets.Where(item => !ReferenceEquals(item, _selectedResultEditCopyTarget)).ToArray();
                _resultEditCopyTargets = Renumber(targets);
                _selectedResultEditCopyTarget = _resultEditCopyTargets.FirstOrDefault();
                _resultEditStatusText = "COPY TARGET REMOVED";
                NotifyResultEditState();
            }

            private void ClearResultEditCopyTargets(object? parameter)
            {
                _resultEditCopyTargets = [];
                _selectedResultEditCopyTarget = null;
                _resultEditStatusText = "COPY TARGET LIST CLEARED";
                NotifyResultEditState();
            }

            private void CopyResultEditXy(object? parameter)
            {
                ApplyResultEditCopy(copyXy: true, copyJudge: false, "RESULT X/Y COPIED");
            }

            private void CopyResultEditJudge(object? parameter)
            {
                ApplyResultEditCopy(copyXy: false, copyJudge: true, "JUDGE COPIED");
            }

            private void CopyResultEditAll(object? parameter)
            {
                ApplyResultEditCopy(copyXy: true, copyJudge: true, "RESULT X/Y AND JUDGE COPIED");
            }

            private void ApplyResultEditCopy(bool copyXy, bool copyJudge, string status)
            {
                if (_resultEditCopySource is null)
                {
                    return;
                }

                foreach (var target in _resultEditCopyTargets)
                {
                    if (copyXy)
                    {
                        target.EditedXText = _resultEditCopySource.OriginalXText;
                        target.EditedYText = _resultEditCopySource.OriginalYText;
                    }

                    if (copyJudge)
                    {
                        target.EditedJudge = _resultEditCopySource.OriginalJudge;
                    }
                }

                _resultEditStatusText = status;
                NotifyResultEditState();
            }

            private async void SaveResultEdit(object? parameter)
            {
                if (!CanSaveResultEdit || _loadedResultFile is not { } source)
                {
                    return;
                }

                var sourceItems = IsResultEditKeyInMode
                    ? _resultEditKeyInItems
                    : _resultEditCopyTargets;
                var updates = new List<ST_REVIEW_RESULT_EDIT_UPDATE>();
                foreach (var item in sourceItems)
                {
                    if (!TryParseResultEditValue(item.EditedXText, out var editedX)
                        || !TryParseResultEditValue(item.EditedYText, out var editedY))
                    {
                        CRecipeConfirmDialog.ShowWarning(
                            "결과 수정 저장 불가",
                            $"{item.Identity}의 EDIT X/Y는 유한한 숫자로 입력해 주세요.");
                        return;
                    }

                    _ = double.TryParse(item.OriginalXText, NumberStyles.Float, CultureInfo.InvariantCulture, out var displayedOriginalX);
                    _ = double.TryParse(item.OriginalYText, NumberStyles.Float, CultureInfo.InvariantCulture, out var displayedOriginalY);
                    var xChanged = !editedX.Equals(displayedOriginalX);
                    var yChanged = !editedY.Equals(displayedOriginalY);
                    var judgeChanged = !item.EditedJudge.Equals(item.OriginalJudge, StringComparison.OrdinalIgnoreCase);
                    if (!xChanged && !yChanged && !judgeChanged)
                    {
                        continue;
                    }

                    updates.Add(new ST_REVIEW_RESULT_EDIT_UPDATE(
                        item.Source.CellNo,
                        item.Source.ShotKey,
                        item.Source.ShotName,
                        item.Source.BeamNo,
                        xChanged ? editedX : item.Source.ResultX,
                        yChanged ? editedY : item.Source.ResultY,
                        item.EditedJudge));
                }

                if (updates.Count == 0)
                {
                    CRecipeConfirmDialog.ShowWarning(
                        "결과 수정 저장 불가",
                        "원본과 달라진 RESULT X/Y 또는 JUDGE가 없습니다.");
                    return;
                }

                if (!CRecipeConfirmDialog.ShowConfirmation(
                        "결과 수정 저장",
                        $"BASE 결과: {source.FileName}\n"
                        + $"수정 Shot: {updates.Count:N0}개\n\n"
                        + "수정 결과를 새 파일로 저장하시겠습니까?",
                        "저장"))
                {
                    return;
                }

                SetResultEditSaving(true);
                _resultEditStatusText = $"RESULT SAVE - {updates.Count:N0} SHOTS";
                Notify(nameof(ResultEditStatusText));
                string? savedPath = null;
                try
                {
                    savedPath = await Task.Run(
                        async () => await _reviewResultFile.SaveEditedRevision(
                            source,
                            updates,
                            DateTimeOffset.Now,
                            CancellationToken.None).ConfigureAwait(false));
                    _resultEditStatusText = $"RESULT SAVE COMPLETED - {Path.GetFileName(savedPath)}";
                    _statusReporter($"Edited review result saved: {savedPath}");
                }
                catch (Exception exception)
                {
                    _resultEditStatusText = $"RESULT SAVE FAILED - {exception.Message}";
                    _statusReporter(_resultEditStatusText);
                    CRecipeConfirmDialog.ShowWarning("결과 수정 저장 실패", exception.Message);
                }
                finally
                {
                    SetResultEditSaving(false);
                    Notify(nameof(ResultEditStatusText));
                }

                if (string.IsNullOrWhiteSpace(savedPath)
                    || !CRecipeConfirmDialog.ShowSavedResultLoadConfirmation(
                        "결과 수정 저장 완료",
                        $"저장이 완료되었습니다.\n\n{Path.GetFileName(savedPath)}\n\n새로 저장한 결과를 LOAD하시겠습니까?"))
                {
                    return;
                }

                await Refresh(CancellationToken.None, savedPath);
                if (_selectedNode?.Result is not { } savedResult
                    || !savedResult.FilePath.Equals(savedPath, StringComparison.OrdinalIgnoreCase))
                {
                    CRecipeConfirmDialog.ShowWarning(
                        "수정 결과 LOAD 실패",
                        "저장된 파일을 결과 트리에서 찾지 못했습니다. REFRESH 후 파일을 직접 선택해 주세요.");
                    return;
                }

                await LoadSelectedData();
            }

            private static bool TryParseResultEditValue(string? text, out double value)
            {
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    && double.IsFinite(value);
            }

            private void SetResultEditSaving(bool value)
            {
                if (_isResultEditSaving == value)
                {
                    return;
                }

                _isResultEditSaving = value;
                Notify(nameof(IsResultEditSaving));
                NotifyResultEditCommandState();
            }

            private static IReadOnlyList<ST_REVIEW_DATA_EDIT_ITEM> Renumber(IEnumerable<ST_REVIEW_DATA_EDIT_ITEM> source)
            {
                var items = source.ToArray();
                for (var index = 0; index < items.Length; index++)
                {
                    items[index].No = index + 1;
                }

                return items;
            }

            private IReadOnlySet<string> GetResultEditSelectedShotKeys(int cellNo)
            {
                return _allRows
                    .Where(row => row.IsResultEditSelected && row.CellNo == cellNo)
                    .Select(row => row.ShotKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            private void ResetResultEditState()
            {
                _isResultEditMode = false;
                _resultEditMode = ResultEditKeyInMode;
                _resultEditKeyInItems = [];
                _selectedResultEditKeyInItem = null;
                _resultEditCopySource = null;
                _resultEditCopyTargets = [];
                _selectedResultEditCopyTarget = null;
                _resultEditStatusText = "NO EDIT ITEMS";
                _isResultEditSaving = false;
                foreach (var row in _allRows)
                {
                    row.IsResultEditSelected = false;
                }

                NotifyResultEditState();
            }

            private void NotifyResultEditState()
            {
                Notify(nameof(IsResultEditKeyInMode));
                Notify(nameof(IsResultEditCopyMode));
                Notify(nameof(ResultEditKeyInItems));
                Notify(nameof(SelectedResultEditKeyInItem));
                Notify(nameof(ResultEditCopySourceRows));
                Notify(nameof(ResultEditCopyTargets));
                Notify(nameof(SelectedResultEditCopyTarget));
                Notify(nameof(ResultEditSelectionSummary));
                Notify(nameof(ResultEditKeyInSummary));
                Notify(nameof(ResultEditCopySummary));
                Notify(nameof(ResultEditStatusText));
                Notify(nameof(IsResultEditSaving));
                NotifyResultEditCommandState();
            }

            private void NotifyResultEditCommandState()
            {
                Notify(nameof(CanAddResultEditKeyIn));
                Notify(nameof(CanAddSelectedResultEditKeyIn));
                Notify(nameof(CanAddNgResultEditKeyIn));
                Notify(nameof(CanRemoveResultEditKeyIn));
                Notify(nameof(CanClearResultEditKeyIn));
                Notify(nameof(CanAddResultEditCopySource));
                Notify(nameof(CanAddResultEditCopyTargets));
                Notify(nameof(CanRemoveResultEditCopyTarget));
                Notify(nameof(CanClearResultEditCopyTargets));
                Notify(nameof(CanApplyResultEditCopy));
                Notify(nameof(CanSaveResultEdit));
                AddResultEditKeyInCommand?.NotifyCanExecuteChanged();
                AddSelectedResultEditKeyInCommand?.NotifyCanExecuteChanged();
                AddNgResultEditKeyInCommand?.NotifyCanExecuteChanged();
                RemoveResultEditKeyInCommand?.NotifyCanExecuteChanged();
                ClearResultEditKeyInCommand?.NotifyCanExecuteChanged();
                AddResultEditCopySourceCommand?.NotifyCanExecuteChanged();
                AddResultEditCopyTargetsCommand?.NotifyCanExecuteChanged();
                RemoveResultEditCopyTargetCommand?.NotifyCanExecuteChanged();
                ClearResultEditCopyTargetsCommand?.NotifyCanExecuteChanged();
                CopyResultEditXyCommand?.NotifyCanExecuteChanged();
                CopyResultEditJudgeCommand?.NotifyCanExecuteChanged();
                CopyResultEditAllCommand?.NotifyCanExecuteChanged();
                SaveResultEditCommand?.NotifyCanExecuteChanged();
            }
        }

        public sealed class ST_REVIEW_DATA_EDIT_ITEM : INotifyPropertyChanged
        {
            private int _no;
            private string _editedXText;
            private string _editedYText;
            private string _editedJudge;

            public ST_REVIEW_DATA_EDIT_ITEM(ST_REVIEW_DATA_GRID_ROW source, int no)
            {
                Source = source;
                _no = no;
                _editedXText = OriginalXText;
                _editedYText = OriginalYText;
                _editedJudge = NormalizeJudge(source.Judge);
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public ST_REVIEW_DATA_GRID_ROW Source { get; }
            public int No
            {
                get => _no;
                set
                {
                    if (_no == value) return;
                    _no = value;
                    OnPropertyChanged();
                }
            }

            public string Identity => $"{Source.Cell} / {Source.ShotName} / B{Source.BeamNo:00}";
            public string Head => Source.Head;
            public string Cell => Source.Cell;
            public string ShotName => Source.ShotName;
            public string Beam => Source.Beam;
            public string OriginalXText => Source.ResultX.ToString("F3", CultureInfo.InvariantCulture);
            public string OriginalYText => Source.ResultY.ToString("F3", CultureInfo.InvariantCulture);
            public string OriginalJudge => NormalizeJudge(Source.Judge);
            public Brush OriginalJudgeBrush => CStatusBrush.ForDisplayState(OriginalJudge);
            public string EditedXText
            {
                get => _editedXText;
                set
                {
                    if (_editedXText == value) return;
                    _editedXText = value;
                    OnPropertyChanged();
                }
            }

            public string EditedYText
            {
                get => _editedYText;
                set
                {
                    if (_editedYText == value) return;
                    _editedYText = value;
                    OnPropertyChanged();
                }
            }

            public string EditedJudge
            {
                get => _editedJudge;
                set
                {
                    var normalized = NormalizeJudge(value);
                    if (_editedJudge == normalized) return;
                    _editedJudge = normalized;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EditedJudgeBrush));
                }
            }

            public Brush EditedJudgeBrush => CStatusBrush.ForDisplayState(EditedJudge);

            public bool Matches(ST_REVIEW_DATA_GRID_ROW row)
            {
                return Source.CellNo == row.CellNo
                    && Source.BeamNo == row.BeamNo
                    && Source.ShotKey.Equals(row.ShotKey, StringComparison.OrdinalIgnoreCase);
            }

            private static string NormalizeJudge(string? value)
            {
                return value?.Equals("OK", StringComparison.OrdinalIgnoreCase) == true
                    || value?.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true
                    ? "OK"
                    : "NG";
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
