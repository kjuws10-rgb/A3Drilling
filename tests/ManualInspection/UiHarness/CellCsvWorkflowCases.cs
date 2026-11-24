using System.Collections;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int CellCsvWorkflow()
    {
        var observations = new List<object>();
        try { RunCellCsvMenuCases(observations); RunCellScopedBulkCases(observations); RunAbsoluteReferenceUiCases(observations); }
        catch (Exception error) { Test("cell_csv_unexpected_exception", false, error.ToString()); }
        var hashes = new[] { "Views/CManualInspectionView.xaml", "Views/CManualInspectionView.xaml.cs", "Views/CManualInspectionUiContract.cs", "Views/CManualInspectionMapControl.cs", "Views/CManualInspectionScatterControl.cs" }
            .ToDictionary(path => path, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource, path)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output, "ui-cell-csv-workflow-results.json"), JsonSerializer.Serialize(new
        {
            passed = _failures == 0, tests = Checks.Count, failures = _failures, checks = Checks, observations, sourceHashes = hashes,
            scope = "Actual source-linked WPF with immutable synthetic records and fake Controller. Tests verify menu exposure, request scope, confirmation and saved projection; actual CSV paths, save cancellation, byte copy/reopen, absolute correction and Undo persistence are verified by separate actual Backend/Core/Controller tests. No equipment, vendor, motion, Vision, real operating file or production save executes here."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Cell CSV workflow UI: {Checks.Count - _failures}/{Checks.Count}"); return _failures == 0 ? 0 : 1;
    }

    private static CManualInspectionUiSnapshot CellCsvState()
    {
        var source = Snapshot(160); var points = new[] { source.Points[1], source.Points[2], source.Points[81], source.Points[82] }
            .Select((point, i) => point with
            {
                WorkingJudge = i == 3 ? "NG" : "OK", OriginalJudge = i == 3 ? "NG" : "OK", WorkingXUm = 2, WorkingYUm = 1, OriginalXUm = 2, OriginalYUm = 1,
                IsMasked = false, IsProxy = false, IsOperatorResult = false, IsMeasurementOffsetInitialized = false, IsMeasurementStateCleared = false,
                HasAppliedOffset = true, BaseXUm = i == 0 ? 7 : 100 + i, BaseYUm = i == 0 ? -4 : 200 + i,
                ReferenceFinalXUm = 2, ReferenceFinalYUm = 1,
                ReferenceValueKind = "MEASURED", ReferenceValueJudge = i == 3 ? "NG" : "OK", ReferenceValueUnavailableReason = "", Source = "MEASURED", CandidateXUm = null, CandidateYUm = null
            }).ToArray();
        return source with
        {
            Version = 700, RecipeId = "CELL_CSV_QA", RunId = "CELL_CSV_RUN", Points = points, HasFinalOffsetSnapshot = true,
            FinalOffsetPoints = points.ToDictionary(point => point.Key, point => new CManualInspectionUiOffset(point.BaseXUm, point.BaseYUm)),
            FinalAppliedPointKeys = points.Select(point => point.Key).ToHashSet(StringComparer.Ordinal), FinalInitializedPointKeys = new HashSet<string>(),
            FinalOffsetRevisionId = "CSV-BEFORE", ActiveRevisionId = "B-BEFORE", FinalOffsetFileName = "20261124_reviewoffset.csv",
            FinalOffsetFilePath = @"C:\SyntheticOnly\Config\RECIPE\CELL_CSV_QA_reviewoffset\20261124_reviewoffset.csv", FinalOffsetContentSha256 = new string('a', 64), FinalOffsetRowCount = 4,
            CanBulkApply = true, BulkEligibleCount = 3, BulkSummaryText = "모델 전체 3점 · Cell 1은 2점, Cell 2는 1점",
            CellBulkEligibleCounts = new Dictionary<int, int> { [1] = 2, [2] = 1 },
            CellBulkSummaryTexts = new Dictionary<int, string> { [1] = "Cell 1 · 보정 대상 2 · 다른 Cell 제외", [2] = "Cell 2 · 보정 대상 1 · NG 1" }
        };
    }
    private static MenuItem[] CellCsvMenu(CManualInspectionView view)
    { Invoke(view, "Files_Click", view, new RoutedEventArgs()); Pump(); return ((Button)view.FindName("FilesButton")).ContextMenu!.Items.OfType<MenuItem>().ToArray(); }

    private static void RunCellCsvMenuCases(List<object> observations)
    {
        foreach (var theme in new[] { "Dark", "Light" })
        {
            LoadResources(theme); var state = CellCsvState(); var fake = new FakeController(state); var confirmations = 0;
            var view = new CManualInspectionView { DataContext = fake, ConfirmationForTesting = (_, _, _) => { confirmations++; return false; } }; var window = HostRepresentativeView(view, 1120, 560);
            try
            {
                var items = CellCsvMenu(view);
                Test("cell_csv_file_menu_exposes_only_integrated_import_and_current_csv_" + theme, items.Length == 2 && items.Count(item => item.Header.ToString()!.Contains("가져오기")) == 1 && items.Count(item => item.Header.ToString()!.Contains("내보내기") && item.Header.ToString()!.Contains("CSV")) == 1);
                Test("cell_csv_file_menu_does_not_expose_session_measurement_json_export_or_return_" + theme, items.All(item => !item.Header.ToString()!.Contains("세션") && !item.Header.ToString()!.Contains("최근") && !item.Header.ToString()!.Contains("작업 Offset 파일 내보내기") && !item.Header.ToString()!.Contains("측정 결과") && !item.Header.ToString()!.Contains("돌아가기")));
                Render(((Button)view.FindName("FilesButton")).ContextMenu!, "cell-csv-popup-active-" + theme.ToLowerInvariant() + ".png");
                var export = items.Single(item => item.Header.ToString()!.Contains("내보내기") && item.Header.ToString()!.Contains("CSV")); export.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Test("cell_csv_export_delegates_current_saved_csv_picker_without_motion_confirmation_" + theme, fake.Calls.Count == 1 && fake.Calls[0].Action == EN_MANUAL_INSPECTION_UI_ACTION.ExportCurrentOffsetCsv && fake.Calls[0].Request.ExpectedVersion == 700 && confirmations == 0);
                Test("cell_csv_cancelled_fake_export_does_not_change_working_or_final_state_" + theme, fake.Snapshot == state && fake.Snapshot.FinalOffsetRevisionId == "CSV-BEFORE");
                ((Button)view.FindName("FilesButton")).ContextMenu!.IsOpen = false;
                Invoke(view,"SetMapCellScope",1); items = CellCsvMenu(view); items.Single(item => item.Header.ToString()!.Contains("가져오기")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Test("cell_csv_import_delegates_single_json_or_csv_import_action_" + theme, fake.Calls.Count == 2 && fake.Calls[1].Action == EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle && confirmations == 0);
                ((Button)view.FindName("FilesButton")).ContextMenu!.IsOpen = false;
                fake.Replace(state with { Version = 701, IsOffsetEditPending = true, OffsetInitPending = true }); Pump(); items = CellCsvMenu(view);
                Test("cell_csv_pending_edit_requires_save_before_import_and_csv_export_" + theme, items.All(item => !item.IsEnabled));
                Render(view, "cell-csv-menu-" + theme.ToLowerInvariant() + ".png"); Render(((Button)view.FindName("FilesButton")).ContextMenu!, "cell-csv-popup-" + theme.ToLowerInvariant() + ".png"); ((Button)view.FindName("FilesButton")).ContextMenu!.IsOpen = false;
                fake.Replace(state with { Version = 702, HasIncomplete = true, RemainingCount = 1 }); Pump(); items = CellCsvMenu(view);
                Test("cell_csv_incomplete_measurement_blocks_import_only_" + theme, !items.Single(item => item.Header.ToString()!.Contains("가져오기")).IsEnabled && items.Single(item => item.Header.ToString()!.Contains("내보내기") && item.Header.ToString()!.Contains("CSV")).IsEnabled);
                ((Button)view.FindName("FilesButton")).ContextMenu!.IsOpen = false;
                observations.Add(new { theme, menuCount = items.Length, fakeCalls = fake.Calls.Count, confirmations });
            }
            finally { window.Close(); Pump(); }
        }
    }

    private static void RunCellScopedBulkCases(List<object> observations)
    {
        LoadResources("Dark"); var state = CellCsvState(); var fake = new FakeController(state); var view = new CManualInspectionView { DataContext = fake }; var window = HostRepresentativeView(view);
        try
        {
            var confirms = 0; string description = "";
            void Bulk() { ((Button)view.FindName("BulkApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
            view.ConfirmationForTesting = (_, text, _) => { confirms++; description = text; return true; };
            Invoke(view, "SetMapCellScope", 0); Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([state.Points[0].Key], state.Points[0].Key)); Pump(); Bulk();
            Test("cell_csv_overview_never_falls_back_to_focused_cell_or_whole_model", confirms == 0 && fake.Calls.Count == 0);
            Invoke(view, "SetMapCellScope", 1);
            ((Task)Invoke(view, "Execute", EN_MANUAL_INSPECTION_UI_ACTION.BulkApply, new CManualInspectionUiRequest { ExpectedVersion = 700, Keys = [state.Points[0].Key] })!).GetAwaiter().GetResult(); Pump();
            Test("cell_csv_missing_request_cell_cannot_fall_back_to_open_cell", confirms == 0 && fake.Calls.Count == 0);
            Invoke(view, "SetMapCellScope", 1); Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([state.Points[1].Key, state.Points[2].Key], state.Points[2].Key));
            SelectViewChoice(view, "HeadFilter", "2"); SelectViewChoice(view, "JudgeFilter", "NG"); Bulk();
            Test("cell_csv_bulk_uses_explicit_cell_even_with_other_cell_focus_and_selection", fake.Calls.Count == 1 && fake.Calls[0].Action == EN_MANUAL_INSPECTION_UI_ACTION.BulkApply && fake.Calls[0].Request.CurrentCellNo == 1);
            Test("cell_csv_bulk_confirmation_uses_current_cell_count_not_global_count", confirms == 1 && description.Contains("Cell 1") && description.Contains(state.CellBulkSummaryTexts[1]) && !description.Contains(state.BulkSummaryText));
            Test("cell_csv_visual_filters_do_not_shrink_current_cell_bulk_target", description.Contains("필터") && fake.Calls[0].Request.CurrentCellNo == 1 && ((TextBlock)view.FindName("BulkSummaryText")).Text.Contains(state.CellBulkSummaryTexts[1]));
            var before = fake.Calls.Count; Invoke(view, "SetMapCellScope", 1); view.ConfirmationForTesting = (_, _, _) => { Invoke(view, "SetMapCellScope", 2); Pump(); return true; }; Bulk();
            Test("cell_csv_cell_change_during_modal_is_stale_even_at_same_snapshot_version", fake.Snapshot.Version == 700 && fake.Calls.Count == before);
            fake.Replace(state with { Version = 703, CellBulkEligibleCounts = new Dictionary<int, int> { [1] = 2, [2] = 0 }, CellBulkSummaryTexts = new Dictionary<int, string> { [1] = state.CellBulkSummaryTexts[1], [2] = "Cell 2 · 새 OK 0" } });
            Invoke(view, "SetMapCellScope", 2); Pump(); confirms = 0; view.ConfirmationForTesting = (_, _, _) => { confirms++; return true; }; Bulk();
            Test("cell_csv_empty_current_cell_does_not_use_other_cell_global_eligibility", fake.Calls.Count == before && confirms == 0);
            fake.Replace(state with { Version = 704 }); Invoke(view, "SetMapCellScope", 1); Pump();
            view.ConfirmationForTesting = (_, _, _) => { fake.Replace(state with { Version = 705 }); Pump(); return true; }; Bulk();
            Test("cell_csv_source_change_during_modal_preserves_scope_and_blocks_dispatch", fake.Calls.Count == before);
            observations.Add(new { currentCellGuarded = true, globalEligible = state.BulkEligibleCount, cellEligible = state.CellBulkEligibleCounts, fake.Calls.Count });
        }
        finally { window.Close(); Pump(); }
    }

    private static void RunAbsoluteReferenceUiCases(List<object> observations)
    {
        LoadResources("Light"); var state = CellCsvState(); var fake = new FakeController(state); var view = new CManualInspectionView { DataContext = fake }; var window = HostRepresentativeView(view);
        try
        {
            var reference = state.Points[0]; var target = state.Points[1];
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([reference.Key], reference.Key)); Invoke(view, "SetReference_Click", view, new RoutedEventArgs());
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([target.Key], target.Key)); Pump();
            var seen = ""; view.ConfirmationForTesting = (_, text, _) => { seen = text; return false; };
            void Copy() { ((Button)view.FindName("CopyReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
            Copy(); Test("cell_csv_reference_cancel_never_changes_offset_or_dispatches", fake.Calls.Count == 0 && fake.Snapshot.FinalOffsetPoints[target.Key].XUm == 101);
            Test("cell_csv_reference_confirmation_explains_absolute_replacement", seen.Contains("대체") && seen.Contains("기존") && seen.Contains("더하지"));
            var saved = state with {Version=706,Points=state.Points.Select(point=>point.Key==target.Key?point with{WorkingXUm=2,WorkingYUm=1,IsProxy=true,ReferenceKey=reference.Key}:point).ToArray()};
            view.ConfirmationForTesting = (_, _, _) => true; fake.OnExecute = (action, _) => { if (action == EN_MANUAL_INSPECTION_UI_ACTION.CopyReference) fake.Replace(saved); }; Copy();
            Test("cell_csv_reference_dispatch_identifies_source_target_and_version", fake.Calls.Count == 1 && fake.Calls[0].Action == EN_MANUAL_INSPECTION_UI_ACTION.CopyReference && fake.Calls[0].Request.ReferenceKey == reference.Key && fake.Calls[0].Request.Keys.SequenceEqual([target.Key]) && fake.Calls[0].Request.ExpectedVersion == 700);
            var finalTarget = Field<IReadOnlyList<CManualInspectionUiPoint>>((CManualInspectionMapControl)view.FindName("PositionMap"), "_all").Single(point => point.Key == target.Key);
            Test("cell_csv_reference_stage_does_not_add_target_B_or_save_csv",finalTarget.MeasurementXUm==2&&finalTarget.MeasurementYUm==1&&finalTarget.BaseXUm==target.BaseXUm&&saved.FinalOffsetRevisionId==state.FinalOffsetRevisionId);
            Test("cell_csv_reference_reply_keeps_other_cell_offsets_and_original_measurements",state.Points.Where(point=>point.CellNo==2).All(point=>saved.FinalOffsetPoints[point.Key]==state.FinalOffsetPoints[point.Key])&&saved.Points.Zip(state.Points).All(pair=>pair.First.OriginalXUm==pair.Second.OriginalXUm&&pair.First.OriginalJudge==pair.Second.OriginalJudge));
            observations.Add(new { sourceFinalX = reference.ReferenceFinalXUm, targetBeforeX = target.BaseXUm, targetAfterX = finalTarget.BaseXUm });
        }
        finally { window.Close(); Pump(); }
    }
}
