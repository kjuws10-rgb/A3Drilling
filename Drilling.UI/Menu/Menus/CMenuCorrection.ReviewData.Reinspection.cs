namespace Drilling.UI.Menu.Menus
{
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Windows.Media;
    using Drilling.Common.Review;
    using Drilling.UI.Popup;

    public sealed partial class CMenuCorrection
    {
        public sealed partial class CReviewDataPanel
        {
            private static readonly IReadOnlyList<int> ReinspectionBeamNumbers = Enumerable.Range(1, 16).ToArray();
            private bool _isReinspectionMode;
            private IReadOnlyList<ST_REVIEW_DATA_REINSPECTION_ITEM> _reinspectionShots = [];
            private ST_REVIEW_DATA_REINSPECTION_ITEM? _selectedReinspectionShot;
            private string _reinspectionStatusText = "NO SHOTS ADDED";
            private CancellationTokenSource? _reinspectionCancellation;
            private CancellationTokenSource? _reinspectionSaveCancellation;
            private bool _isReinspectionRunning;
            private bool _isReinspectionSaving;
            private bool _hasUnsavedReinspectionMeasurements;

            public bool IsReinspectionMode => _isReinspectionMode;
            public bool IsReinspectionRunning => _isReinspectionRunning;
            public bool IsReinspectionSaving => _isReinspectionSaving;
            public bool IsBeamInspectionResult => _loadedCatalog?.ResultType.Equals("BEAM_INSPECTION", StringComparison.OrdinalIgnoreCase) == true;
            public bool CanEditReinspectionList => _isReinspectionMode && !_isReinspectionRunning && !_isReinspectionSaving;
            public IReadOnlyList<ST_REVIEW_DATA_REINSPECTION_ITEM> ReinspectionShots => _reinspectionShots;
            public IReadOnlyList<int> ReinspectionBeamOptions => ReinspectionBeamNumbers;
            public string ReinspectionSummary => $"{_reinspectionShots.Count:N0} SHOTS";
            public string ReinspectionStatusText => _reinspectionStatusText;
            public string ReinspectionAvailabilityText => IsBeamInspectionResult
                ? "BEAM INSPECTION 결과는 전용 16-Beam 검사 구조이므로 RE-INSPECTION MODE를 사용할 수 없습니다."
                : _hasLoadedData
                    ? "선택한 BASE 검사 결과의 분류와 형식을 유지한 채 Shot을 재검사합니다."
                    : "먼저 검사 결과를 DATA LOAD 하세요.";
            public bool CanToggleReinspection => _hasLoadedData
                && !_isDataLoading
                && !_isReinspectionRunning
                && !_isReinspectionSaving
                && !IsBeamInspectionResult;
            public bool CanAddReinspectionShot => _isReinspectionMode
                && _hasLoadedData
                && !_isReinspectionRunning
                && !_isReinspectionSaving
                && !string.IsNullOrWhiteSpace(_selectedShotKey);
            public bool CanAddSelectedReinspectionShots => _isReinspectionMode
                && _hasLoadedData
                && !_isReinspectionRunning
                && !_isReinspectionSaving
                && _allRows.Any(row => row.IsReinspectionSelected);
            public bool CanAddNgReinspectionShots => _isReinspectionMode
                && _hasLoadedData
                && !_isReinspectionRunning
                && !_isReinspectionSaving
                && _allRows.Any(IsNgResultRow);
            public bool CanDeleteReinspectionShot => CanEditReinspectionList && _selectedReinspectionShot is not null;
            public bool CanClearReinspectionShots => CanEditReinspectionList && _reinspectionShots.Count > 0;
            public bool CanMoveToReinspectionShot => _isReinspectionMode && !_isReinspectionRunning && !_isReinspectionSaving && _selectedReinspectionShot is not null;
            public bool CanInspectReinspectionShot => CanMoveToReinspectionShot;
            public bool CanInspectAllReinspectionShots => _isReinspectionMode && !_isReinspectionRunning && !_isReinspectionSaving && _reinspectionShots.Count > 0;
            public bool CanStopReinspection => _isReinspectionRunning;
            public bool CanSaveReinspectionResult => _isReinspectionMode
                && !_isReinspectionRunning
                && !_isReinspectionSaving
                && _loadedResultFile is not null
                && !IsBeamInspectionResult
                && _hasUnsavedReinspectionMeasurements
                && _reinspectionShots.Any(item => item.HasCompletedMeasurement);

            public ST_REVIEW_DATA_REINSPECTION_ITEM? SelectedReinspectionShot
            {
                get => _selectedReinspectionShot;
                set
                {
                    if (ReferenceEquals(_selectedReinspectionShot, value))
                    {
                        return;
                    }

                    _selectedReinspectionShot = value;
                    Notify(nameof(SelectedReinspectionShot));
                    NotifyReinspectionCommandState();
                }
            }

            public CButtonCommand ToggleReinspectionCommand { get; private set; } = null!;
            public CButtonCommand AddReinspectionShotCommand { get; private set; } = null!;
            public CButtonCommand AddSelectedReinspectionShotsCommand { get; private set; } = null!;
            public CButtonCommand AddNgReinspectionShotsCommand { get; private set; } = null!;
            public CButtonCommand DeleteReinspectionShotCommand { get; private set; } = null!;
            public CButtonCommand ClearReinspectionShotsCommand { get; private set; } = null!;
            public CButtonCommand MoveToReinspectionShotCommand { get; private set; } = null!;
            public CButtonCommand MoveAndInspectReinspectionShotCommand { get; private set; } = null!;
            public CButtonCommand InspectAllReinspectionShotsCommand { get; private set; } = null!;
            public CButtonCommand StopReinspectionCommand { get; private set; } = null!;
            public CButtonCommand SaveReinspectionResultCommand { get; private set; } = null!;

            private void InitializeReinspectionCommands()
            {
                ToggleReinspectionCommand = new CButtonCommand(ToggleReinspection, _ => CanToggleReinspection);
                AddReinspectionShotCommand = new CButtonCommand(AddReinspectionShot, _ => CanAddReinspectionShot);
                AddSelectedReinspectionShotsCommand = new CButtonCommand(AddSelectedReinspectionShots, _ => CanAddSelectedReinspectionShots);
                AddNgReinspectionShotsCommand = new CButtonCommand(AddNgReinspectionShots, _ => CanAddNgReinspectionShots);
                DeleteReinspectionShotCommand = new CButtonCommand(DeleteReinspectionShot, _ => CanDeleteReinspectionShot);
                ClearReinspectionShotsCommand = new CButtonCommand(ClearReinspectionShots, _ => CanClearReinspectionShots);
                MoveToReinspectionShotCommand = new CButtonCommand(MoveToReinspectionShot, _ => CanMoveToReinspectionShot);
                MoveAndInspectReinspectionShotCommand = new CButtonCommand(MoveAndInspectReinspectionShot, _ => CanInspectReinspectionShot);
                InspectAllReinspectionShotsCommand = new CButtonCommand(InspectAllReinspectionShots, _ => CanInspectAllReinspectionShots);
                StopReinspectionCommand = new CButtonCommand(StopReinspection, _ => CanStopReinspection);
                SaveReinspectionResultCommand = new CButtonCommand(SaveReinspectionResult, _ => CanSaveReinspectionResult);
            }

            private void ToggleReinspection(object? parameter)
            {
                if (!CanToggleReinspection)
                {
                    return;
                }

                _isReinspectionMode = !_isReinspectionMode;
                _reinspectionStatusText = _isReinspectionMode
                    ? (_reinspectionShots.Count == 0 ? "NO SHOTS ADDED" : "RE-INSPECTION LIST READY")
                    : "RE-INSPECTION CLOSED";
                Notify(nameof(IsReinspectionMode));
                Notify(nameof(ReinspectionStatusText));
                if (_isReinspectionMode)
                {
                    RefreshGlassPreview();
                }
                else
                {
                    Camera.Clear();
                }
                NotifyReinspectionCommandState();
            }

            private void AddReinspectionShot(object? parameter)
            {
                if (!CanAddReinspectionShot
                    || !_shotsByCell.TryGetValue(_selectedCellNo, out var shots))
                {
                    return;
                }

                var shot = shots.FirstOrDefault(item => item.ShotKey.Equals(_selectedShotKey, StringComparison.OrdinalIgnoreCase));
                if (shot is null)
                {
                    return;
                }

                var sourceRow = SelectedRow?.CellNo == shot.CellNo
                    && SelectedRow.ShotKey.Equals(shot.ShotKey, StringComparison.OrdinalIgnoreCase)
                    ? SelectedRow
                    : Rows.FirstOrDefault(row =>
                        row.CellNo == shot.CellNo
                        && row.ShotKey.Equals(shot.ShotKey, StringComparison.OrdinalIgnoreCase));
                var beamNo = NormalizeBeamNo(sourceRow?.BeamNo ?? 1);
                var existing = FindReinspectionShot(shot.CellNo, shot.ShotKey);
                if (existing is not null)
                {
                    _selectedReinspectionShot = existing;
                    _reinspectionStatusText = $"{existing.Cell} / {existing.ShotName} / BEAM {existing.BeamNo} ALREADY ADDED";
                    Notify(nameof(SelectedReinspectionShot));
                    Notify(nameof(ReinspectionStatusText));
                    NotifyReinspectionCommandState();
                    return;
                }

                var item = CreateReinspectionItem(shot, sourceRow, beamNo, _reinspectionShots.Count + 1);

                _reinspectionShots = [.. _reinspectionShots, item];
                _selectedReinspectionShot = item;
                _reinspectionStatusText = $"{item.Cell} / {item.ShotName} / BEAM {item.BeamNo} ADDED";
                NotifyReinspectionListState();
            }

            private void AddSelectedReinspectionShots(object? parameter)
            {
                AddReinspectionRows(
                    _allRows.Where(row => row.IsReinspectionSelected),
                    "SELECTED");
            }

            private void AddNgReinspectionShots(object? parameter)
            {
                AddReinspectionRows(
                    _allRows.Where(IsNgResultRow),
                    "NG");
            }

            private void AddReinspectionRows(
                IEnumerable<ST_REVIEW_DATA_GRID_ROW> candidates,
                string sourceName)
            {
                var rows = candidates.ToArray();
                if (rows.Length == 0)
                {
                    _reinspectionStatusText = $"NO {sourceName} SHOTS FOUND";
                    Notify(nameof(ReinspectionStatusText));
                    return;
                }

                var items = _reinspectionShots.ToList();
                var originalCount = items.Count;
                var keys = new HashSet<string>(
                    items.Select(item => BuildReinspectionKey(item.CellNo, item.ShotKey)),
                    StringComparer.OrdinalIgnoreCase);
                ST_REVIEW_DATA_REINSPECTION_ITEM? lastAdded = null;
                var duplicateCount = 0;
                var skippedCount = 0;

                foreach (var row in rows)
                {
                    var shot = FindShot(row.CellNo, row.ShotKey);
                    if (shot is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    var beamNo = NormalizeBeamNo(row.BeamNo);
                    var key = BuildReinspectionKey(row.CellNo, row.ShotKey);
                    if (!keys.Add(key))
                    {
                        duplicateCount++;
                        continue;
                    }

                    lastAdded = CreateReinspectionItem(shot, row, beamNo, items.Count + 1);
                    items.Add(lastAdded);
                }

                _reinspectionShots = items.ToArray();
                if (lastAdded is not null)
                {
                    _selectedReinspectionShot = lastAdded;
                }

                var addedCount = items.Count - originalCount;
                _reinspectionStatusText = $"{sourceName}: {addedCount:N0} ADDED / {duplicateCount:N0} DUPLICATE / {skippedCount:N0} SKIPPED";
                NotifyReinspectionListState();
            }

            private void DeleteReinspectionShot(object? parameter)
            {
                if (!CanDeleteReinspectionShot || _selectedReinspectionShot is not { } selected)
                {
                    return;
                }

                var selectedIndex = _reinspectionShots
                    .Select((item, index) => (item, index))
                    .First(pair => ReferenceEquals(pair.item, selected))
                    .index;
                var remaining = _reinspectionShots
                    .Where(item => !ReferenceEquals(item, selected))
                    .ToArray();
                selected.PropertyChanged -= ReinspectionItemPropertyChanged;
                for (var index = 0; index < remaining.Length; index++)
                {
                    remaining[index].Renumber(index + 1);
                }

                _reinspectionShots = remaining;
                _selectedReinspectionShot = remaining.Length == 0
                    ? null
                    : remaining[Math.Min(selectedIndex, remaining.Length - 1)];
                _reinspectionStatusText = $"{selected.Cell} / {selected.ShotName} DELETED";
                NotifyReinspectionListState();
                if (_selectedReinspectionShot is not null)
                {
                    FocusReinspectionShot(_selectedReinspectionShot);
                }
            }

            private void ClearReinspectionShots(object? parameter)
            {
                if (!CanClearReinspectionShots)
                {
                    return;
                }

                UntrackReinspectionItems(_reinspectionShots);
                _reinspectionShots = [];
                _selectedReinspectionShot = null;
                _reinspectionStatusText = "NO SHOTS ADDED";
                NotifyReinspectionListState();
            }

            private async void MoveToReinspectionShot(object? parameter)
            {
                if (!CanMoveToReinspectionShot || _selectedReinspectionShot is not { } item)
                {
                    return;
                }

                await RunReinspection([item], EN_ONE_SHOT_ACTION.Move, false);
            }

            private async void MoveAndInspectReinspectionShot(object? parameter)
            {
                if (!CanInspectReinspectionShot || _selectedReinspectionShot is not { } item)
                {
                    return;
                }

                await RunReinspection([item], EN_ONE_SHOT_ACTION.MoveAndInspection, false);
            }

            private async void InspectAllReinspectionShots(object? parameter)
            {
                if (!CanInspectAllReinspectionShots)
                {
                    return;
                }

                await RunReinspection(_reinspectionShots.ToArray(), EN_ONE_SHOT_ACTION.MoveAndInspection, true);
            }

            private void StopReinspection(object? parameter)
            {
                if (!CanStopReinspection)
                {
                    return;
                }

                _reinspectionStatusText = "RE-INSPECTION STOP REQUESTED";
                Notify(nameof(ReinspectionStatusText));
                _reinspectionCancellation?.Cancel();
                _reviewManager.Stop();
            }

            private async void SaveReinspectionResult(object? parameter)
            {
                if (!CanSaveReinspectionResult || _loadedResultFile is not { } source)
                {
                    return;
                }

                var updates = _reinspectionShots
                    .Where(item => item.HasCompletedMeasurement)
                    .Select(item => new ST_REVIEW_REINSPECTION_UPDATE(
                        item.CellNo,
                        item.ShotKey,
                        item.ShotName,
                        item.SourceBeamNo,
                        item.BeamNo,
                        item.RawResultX!.Value,
                        item.RawResultY!.Value,
                        item.ResultX!.Value,
                        item.ResultY!.Value,
                        item.State))
                    .ToArray();
                if (updates.Length == 0)
                {
                    CRecipeConfirmDialog.ShowWarning(
                        "재검사 결과 저장 불가",
                        "OK 또는 NG로 완료된 재검사 Shot이 없습니다.");
                    return;
                }

                if (!CRecipeConfirmDialog.ShowConfirmation(
                        "재검사 결과 저장",
                        $"BASE 결과: {source.FileName}\n"
                        + $"갱신 Shot: {updates.Length:N0}개\n\n"
                        + "재검사 결과를 새 파일로 저장하시겠습니까?",
                        "저장"))
                {
                    return;
                }

                _reinspectionSaveCancellation?.Cancel();
                _reinspectionSaveCancellation?.Dispose();
                _reinspectionSaveCancellation = new CancellationTokenSource();
                var cancellationToken = _reinspectionSaveCancellation.Token;
                SetReinspectionSaving(true);
                _reinspectionStatusText = $"SAVE RESULT - BASE {(_loadedCatalog?.ResultType ?? "RESULT")} / {updates.Length:N0} SHOTS";
                Notify(nameof(ReinspectionStatusText));
                string? savedPath = null;
                try
                {
                    var savedAt = DateTimeOffset.Now;
                    savedPath = await Task.Run(
                        async () => await _reviewResultFile.SaveReinspectionRevision(
                            source,
                            updates,
                            savedAt,
                            cancellationToken).ConfigureAwait(false),
                        cancellationToken);
                    _hasUnsavedReinspectionMeasurements = false;
                    _reinspectionStatusText = $"SAVE RESULT COMPLETED - {Path.GetFileName(savedPath)}";
                    _statusReporter($"Re-inspection result saved from BASE {_loadedCatalog?.ResultType ?? "RESULT"}: {savedPath}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _reinspectionStatusText = "SAVE RESULT CANCELED";
                    _statusReporter(_reinspectionStatusText);
                }
                catch (Exception exception)
                {
                    _reinspectionStatusText = $"SAVE RESULT FAILED - {exception.Message}";
                    _statusReporter(_reinspectionStatusText);
                    CRecipeConfirmDialog.ShowWarning(
                        "재검사 결과 저장 실패",
                        exception.Message);
                }
                finally
                {
                    _reinspectionSaveCancellation?.Dispose();
                    _reinspectionSaveCancellation = null;
                    SetReinspectionSaving(false);
                    Notify(nameof(ReinspectionStatusText));
                }


                if (string.IsNullOrWhiteSpace(savedPath))
                {
                    return;
                }

                var shouldLoad = CRecipeConfirmDialog.ShowSavedResultLoadConfirmation(
                    "재검사 결과 저장 완료",
                    $"저장이 완료되었습니다.\n\n{Path.GetFileName(savedPath)}\n\n새로 저장한 결과를 LOAD하시겠습니까?");
                if (!shouldLoad)
                {
                    return;
                }

                await Refresh(CancellationToken.None, savedPath);
                if (_selectedNode?.Result is not { } savedResult
                    || !savedResult.FilePath.Equals(savedPath, StringComparison.OrdinalIgnoreCase))
                {
                    CRecipeConfirmDialog.ShowWarning(
                        "재검사 결과 LOAD 실패",
                        "저장된 파일을 결과 트리에서 찾지 못했습니다. REFRESH 후 파일을 직접 선택해 주세요.");
                    return;
                }

                await LoadSelectedData();
            }

            private async Task RunReinspection(
                IReadOnlyList<ST_REVIEW_DATA_REINSPECTION_ITEM> items,
                EN_ONE_SHOT_ACTION action,
                bool isBatch)
            {
                if (_isReinspectionRunning || items.Count == 0)
                {
                    return;
                }

                _reinspectionCancellation?.Dispose();
                _reinspectionCancellation = new CancellationTokenSource();
                var token = _reinspectionCancellation.Token;
                ST_REVIEW_DATA_REINSPECTION_ITEM? activeItem = null;
                SetReinspectionRunning(true);
                try
                {
                    for (var index = 0; index < items.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        activeItem = items[index];
                        SelectedReinspectionShot = activeItem;
                        FocusReinspectionShot(activeItem);
                        _reinspectionStatusText = isBatch
                            ? $"RE-INSPECTION {index + 1:N0} / {items.Count:N0} - {activeItem.Cell} / {activeItem.ShotName} / BEAM {activeItem.BeamNo}"
                            : $"{activeItem.Cell} / {activeItem.ShotName} / BEAM {activeItem.BeamNo}";
                        Notify(nameof(ReinspectionStatusText));
                        await ExecuteReinspectionItem(activeItem, action, token);
                    }

                    _reinspectionStatusText = action == EN_ONE_SHOT_ACTION.Move
                        ? "MOVE TO SHOT COMPLETED"
                        : $"RE-INSPECTION COMPLETED - {items.Count:N0} SHOTS";
                    _statusReporter(_reinspectionStatusText);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (activeItem is not null && action != EN_ONE_SHOT_ACTION.Move)
                    {
                        activeItem.State = "STOPPED";
                    }

                    _reinspectionStatusText = "RE-INSPECTION STOPPED";
                    _statusReporter(_reinspectionStatusText);
                }
                catch (Exception exception)
                {
                    if (activeItem is not null && action != EN_ONE_SHOT_ACTION.Move)
                    {
                        activeItem.State = "FAILED";
                    }

                    _reinspectionStatusText = $"RE-INSPECTION FAILED - {exception.Message}";
                    _statusReporter(_reinspectionStatusText);
                }
                finally
                {
                    foreach (var item in items)
                    {
                        item.IsRunning = false;
                    }

                    _reinspectionCancellation?.Dispose();
                    _reinspectionCancellation = null;
                    SetReinspectionRunning(false);
                    Notify(nameof(ReinspectionStatusText));
                }
            }

            private async Task ExecuteReinspectionItem(
                ST_REVIEW_DATA_REINSPECTION_ITEM item,
                EN_ONE_SHOT_ACTION action,
                CancellationToken cancellationToken)
            {
                if (_loadedRecipe is null || _loadedResultFile is null)
                {
                    throw new InvalidOperationException("불러온 Review Data 또는 Recipe가 없습니다.");
                }

                var plan = _reviewManager.CreatePlan(
                    _loadedRecipe,
                    [item.ShotKey],
                    item.BeamNo,
                    _loadedResultFile.ShiftOffsetX,
                    _loadedResultFile.ShiftOffsetY);
                var point = plan.Points.FirstOrDefault(candidate =>
                    candidate.CellNo == item.CellNo
                    && candidate.ShotKey.Equals(item.ShotKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{item.Cell} / {item.ShotName}의 검사 좌표를 생성할 수 없습니다.");

                item.IsRunning = true;
                if (action != EN_ONE_SHOT_ACTION.Move)
                {
                    item.BeginInspection();
                }

                void ReportProgress(string message)
                {
                    _reinspectionStatusText = $"{item.Cell} / {item.ShotName} / BEAM {item.BeamNo} - {message}";
                    Notify(nameof(ReinspectionStatusText));
                    if (message.Contains("이동 완료", StringComparison.Ordinal))
                    {
                        UpdateReinspectionCamera(point, "SIMULATION");
                    }
                }

                var result = await _reviewManager.ExecuteOneShot(
                    plan,
                    point,
                    action,
                    EN_ONE_SHOT_METHOD.MarkFindSingle,
                    ReportProgress,
                    cancellationToken,
                    saveResult: false);
                UpdateReinspectionCamera(result.Point, result.IsStageSimulation ? "SIMULATION" : "ONLINE");
                if (action == EN_ONE_SHOT_ACTION.Move)
                {
                    item.IsRunning = false;
                    return;
                }

                var converted = CReviewManager.VisionErrorToStageOffset(
                    result.Point.ErrorX,
                    result.Point.ErrorY,
                    plan.VisionFlipType);
                var state = result.Point.Judge.Trim().ToUpperInvariant() switch
                {
                    "OK" => "OK",
                    "NG" => "NG",
                    _ => throw new InvalidDataException($"Vision 재검사 판정이 OK/NG가 아닙니다. Judge={result.Point.Judge}")
                };
                item.ApplyMeasurement(
                    result.Point.ErrorX,
                    result.Point.ErrorY,
                    converted.X,
                    converted.Y,
                    state);
                item.IsRunning = false;
            }

            private void UpdateReinspectionCamera(ST_REVIEW_PLAN_POINT point, string mode)
            {
                Camera.UpdateFeedback(
                    point.ReviewTargetY.ToString("R", CultureInfo.InvariantCulture),
                    point.ReviewTargetX.ToString("R", CultureInfo.InvariantCulture),
                    mode);
            }

            private void SetReinspectionRunning(bool value)
            {
                if (_isReinspectionRunning == value)
                {
                    return;
                }

                _isReinspectionRunning = value;
                Notify(nameof(IsReinspectionRunning));
                Notify(nameof(CanEditReinspectionList));
                NotifyReinspectionCommandState();
            }

            private void SetReinspectionSaving(bool value)
            {
                if (_isReinspectionSaving == value)
                {
                    return;
                }

                _isReinspectionSaving = value;
                Notify(nameof(IsReinspectionSaving));
                Notify(nameof(CanEditReinspectionList));
                NotifyReinspectionCommandState();
            }

            public void SetReinspectionRowSelection(ST_REVIEW_DATA_GRID_ROW row, bool isSelected)
            {
                row.IsReinspectionSelected = isSelected;
                NotifyReinspectionCommandState();
            }

            public void SelectReinspectionShot(ST_REVIEW_DATA_REINSPECTION_ITEM item)
            {
                if (!_isReinspectionMode || !_reinspectionShots.Contains(item))
                {
                    return;
                }

                SelectedReinspectionShot = item;
                FocusReinspectionShot(item);
            }

            private void FocusReinspectionShot(ST_REVIEW_DATA_REINSPECTION_ITEM item)
            {
                if (!_hasLoadedData)
                {
                    return;
                }

                if (_selectedCellNo != item.CellNo)
                {
                    ApplyCellSelection(item.CellNo);
                }

                var row = item.SourceRow
                    ?? Rows.FirstOrDefault(candidate =>
                        candidate.CellNo == item.CellNo
                        && candidate.ShotKey.Equals(item.ShotKey, StringComparison.OrdinalIgnoreCase));
                ApplyShotSelection(item.ShotKey, row);
            }

            private void ResetReinspectionState()
            {
                _reinspectionCancellation?.Cancel();
                _reinspectionSaveCancellation?.Cancel();
                if (_isReinspectionRunning)
                {
                    _reviewManager.Stop();
                }

                _reinspectionCancellation?.Dispose();
                _reinspectionCancellation = null;
                _reinspectionSaveCancellation?.Dispose();
                _reinspectionSaveCancellation = null;
                _isReinspectionRunning = false;
                _isReinspectionSaving = false;
                _hasUnsavedReinspectionMeasurements = false;
                foreach (var row in _allRows)
                {
                    row.IsReinspectionSelected = false;
                }

                _isReinspectionMode = false;
                UntrackReinspectionItems(_reinspectionShots);
                _reinspectionShots = [];
                _selectedReinspectionShot = null;
                _reinspectionStatusText = "NO SHOTS ADDED";
                Notify(nameof(IsReinspectionMode));
                Notify(nameof(IsReinspectionRunning));
                Notify(nameof(IsReinspectionSaving));
                Notify(nameof(CanEditReinspectionList));
                NotifyReinspectionListState();
            }

            private void NotifyReinspectionListState()
            {
                Notify(nameof(ReinspectionShots));
                Notify(nameof(ReinspectionSummary));
                Notify(nameof(SelectedReinspectionShot));
                Notify(nameof(ReinspectionStatusText));
                NotifyReinspectionCommandState();
            }

            private void NotifyReinspectionCommandState()
            {
                Notify(nameof(CanToggleReinspection));
                Notify(nameof(CanAddReinspectionShot));
                Notify(nameof(CanAddSelectedReinspectionShots));
                Notify(nameof(CanAddNgReinspectionShots));
                Notify(nameof(CanDeleteReinspectionShot));
                Notify(nameof(CanClearReinspectionShots));
                Notify(nameof(CanMoveToReinspectionShot));
                Notify(nameof(CanInspectReinspectionShot));
                Notify(nameof(CanInspectAllReinspectionShots));
                Notify(nameof(CanStopReinspection));
                Notify(nameof(CanSaveReinspectionResult));
                Notify(nameof(IsBeamInspectionResult));
                Notify(nameof(ReinspectionAvailabilityText));
                ToggleReinspectionCommand?.NotifyCanExecuteChanged();
                AddReinspectionShotCommand?.NotifyCanExecuteChanged();
                AddSelectedReinspectionShotsCommand?.NotifyCanExecuteChanged();
                AddNgReinspectionShotsCommand?.NotifyCanExecuteChanged();
                DeleteReinspectionShotCommand?.NotifyCanExecuteChanged();
                ClearReinspectionShotsCommand?.NotifyCanExecuteChanged();
                MoveToReinspectionShotCommand?.NotifyCanExecuteChanged();
                MoveAndInspectReinspectionShotCommand?.NotifyCanExecuteChanged();
                InspectAllReinspectionShotsCommand?.NotifyCanExecuteChanged();
                StopReinspectionCommand?.NotifyCanExecuteChanged();
                SaveReinspectionResultCommand?.NotifyCanExecuteChanged();
            }

            private ST_REVIEW_DATA_REINSPECTION_ITEM? FindReinspectionShot(
                int cellNo,
                string shotKey)
            {
                return _reinspectionShots.FirstOrDefault(item =>
                    item.CellNo == cellNo
                    && item.ShotKey.Equals(shotKey, StringComparison.OrdinalIgnoreCase));
            }

            private ST_REVIEW_DATA_SHOT_POSITION? FindShot(int cellNo, string shotKey)
            {
                return _shotsByCell.TryGetValue(cellNo, out var shots)
                    ? shots.FirstOrDefault(item => item.ShotKey.Equals(shotKey, StringComparison.OrdinalIgnoreCase))
                    : null;
            }

            private ST_REVIEW_DATA_REINSPECTION_ITEM CreateReinspectionItem(
                ST_REVIEW_DATA_SHOT_POSITION shot,
                ST_REVIEW_DATA_GRID_ROW? sourceRow,
                int beamNo,
                int sequence)
            {
                var item = new ST_REVIEW_DATA_REINSPECTION_ITEM(
                    sequence,
                    sourceRow?.HeadNo ?? shot.HeadNo,
                    shot.CellNo,
                    CReviewShotNameFormatter.ToMatrixName(shot.ShotNo, shot.GroupCountX),
                    beamNo,
                    shot.ShotKey,
                    sourceRow);
                item.PropertyChanged += ReinspectionItemPropertyChanged;
                return item;
            }

            private void ReinspectionItemPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
            {
                if (sender is ST_REVIEW_DATA_REINSPECTION_ITEM item
                    && item.HasCompletedMeasurement)
                {
                    _hasUnsavedReinspectionMeasurements = true;
                }

                if (eventArgs.PropertyName is nameof(ST_REVIEW_DATA_REINSPECTION_ITEM.BeamNo)
                    or nameof(ST_REVIEW_DATA_REINSPECTION_ITEM.ResultX)
                    or nameof(ST_REVIEW_DATA_REINSPECTION_ITEM.ResultY)
                    or nameof(ST_REVIEW_DATA_REINSPECTION_ITEM.State))
                {
                    NotifyReinspectionCommandState();
                }
            }

            private void UntrackReinspectionItems(IEnumerable<ST_REVIEW_DATA_REINSPECTION_ITEM> items)
            {
                foreach (var item in items)
                {
                    item.PropertyChanged -= ReinspectionItemPropertyChanged;
                }
            }

            private static int NormalizeBeamNo(int beamNo)
            {
                return beamNo is >= 1 and <= 16 ? beamNo : 1;
            }

            private static bool IsNgResultRow(ST_REVIEW_DATA_GRID_ROW row)
            {
                return row.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase);
            }

            private static string BuildReinspectionKey(int cellNo, string shotKey)
            {
                return $"{cellNo}:{shotKey}";
            }
        }

        public sealed class ST_REVIEW_DATA_REINSPECTION_ITEM : INotifyPropertyChanged
        {
            private int _sequence;
            private int _beamNo;
            private double? _rawResultX;
            private double? _rawResultY;
            private double? _resultX;
            private double? _resultY;
            private string _state = "WAIT";
            private bool _isRunning;

            public ST_REVIEW_DATA_REINSPECTION_ITEM(
                int sequence,
                int headNo,
                int cellNo,
                string shotName,
                int beamNo,
                string shotKey,
                ST_REVIEW_DATA_GRID_ROW? sourceRow)
            {
                _sequence = sequence;
                HeadNo = headNo;
                CellNo = cellNo;
                ShotName = shotName;
                _beamNo = beamNo;
                ShotKey = shotKey;
                SourceRow = sourceRow;
                SourceBeamNo = sourceRow?.BeamNo ?? beamNo;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public int Sequence => _sequence;
            public int HeadNo { get; }
            public int CellNo { get; }
            public string ShotName { get; }
            public int SourceBeamNo { get; }
            public int BeamNo
            {
                get => _beamNo;
                set
                {
                    if (_beamNo == value)
                    {
                        return;
                    }

                    _beamNo = value;
                    OnPropertyChanged();
                    ClearMeasurement();
                }
            }

            public double? ResultX
            {
                get => _resultX;
                set
                {
                    if (_resultX == value)
                    {
                        return;
                    }

                    _resultX = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ResultXText));
                }
            }

            public double? ResultY
            {
                get => _resultY;
                set
                {
                    if (_resultY == value)
                    {
                        return;
                    }

                    _resultY = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ResultYText));
                }
            }

            public string State
            {
                get => _state;
                set
                {
                    if (_state.Equals(value, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ResultBrush));
                    OnPropertyChanged(nameof(StateBrush));
                }
            }

            public bool IsRunning
            {
                get => _isRunning;
                set
                {
                    if (_isRunning == value)
                    {
                        return;
                    }

                    _isRunning = value;
                    OnPropertyChanged();
                }
            }

            public string ShotKey { get; }
            public ST_REVIEW_DATA_GRID_ROW? SourceRow { get; }
            internal double? RawResultX => _rawResultX;
            internal double? RawResultY => _rawResultY;
            internal bool HasCompletedMeasurement => RawResultX.HasValue
                && RawResultY.HasValue
                && ResultX.HasValue
                && ResultY.HasValue
                && double.IsFinite(RawResultX.Value)
                && double.IsFinite(RawResultY.Value)
                && double.IsFinite(ResultX.Value)
                && double.IsFinite(ResultY.Value)
                && (State.Equals("OK", StringComparison.OrdinalIgnoreCase)
                    || State.Equals("NG", StringComparison.OrdinalIgnoreCase));
            public string No => Sequence.ToString("D2", CultureInfo.InvariantCulture);
            public string Head => HeadNo > 0 ? $"H{HeadNo:00}" : "-";
            public string Cell => $"CELL{CellNo:00}";
            public string ResultXText => ResultX?.ToString("F3", CultureInfo.InvariantCulture) ?? "-";
            public string ResultYText => ResultY?.ToString("F3", CultureInfo.InvariantCulture) ?? "-";
            public Brush ResultBrush => CStatusBrush.ForDisplayState(State);
            public Brush StateBrush => CStatusBrush.ForDisplayState(State);

            internal void ClearMeasurement()
            {
                _rawResultX = null;
                _rawResultY = null;
                ResultX = null;
                ResultY = null;
                State = "WAIT";
            }

            internal void BeginInspection()
            {
                _rawResultX = null;
                _rawResultY = null;
                ResultX = null;
                ResultY = null;
                State = "RUNNING";
            }

            internal void ApplyMeasurement(
                double rawResultX,
                double rawResultY,
                double resultX,
                double resultY,
                string state)
            {
                _rawResultX = rawResultX;
                _rawResultY = rawResultY;
                ResultX = resultX;
                ResultY = resultY;
                State = state;
            }

            internal void Renumber(int sequence)
            {
                if (_sequence == sequence)
                {
                    return;
                }

                _sequence = sequence;
                OnPropertyChanged(nameof(Sequence));
                OnPropertyChanged(nameof(No));
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
