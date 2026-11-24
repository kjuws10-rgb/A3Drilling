using System.Collections;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int BulkFinalConsistency()
    {
        var observations = new List<object>();
        try
        {
            RunBulkFinalConfirmationCases(observations);
            RunBulkFinalOutcomeCases(observations);
            RunBulkFinalLayoutCases(observations);
        }
        catch (Exception error) { Test("bulk_final_unexpected_exception", false, error.ToString()); }
        var sourceHashes = new[] { "Views/CManualInspectionView.xaml", "Views/CManualInspectionView.xaml.cs", "Views/CManualInspectionUiContract.cs", "Views/CManualInspectionMapControl.cs", "Views/CManualInspectionScatterControl.cs" }
            .ToDictionary(path => path, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource, path)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output, "ui-bulk-final-consistency-results.json"), JsonSerializer.Serialize(new
        {
            passed = _failures == 0, tests = Checks.Count, failures = _failures, checks = Checks, observations, sourceHashes,
            scope = "Actual source-linked WPF controls/themes and immutable synthetic state transitions through a fake Controller. No production Controller/Manager, real CSV write/readback, monitor transform, motion, Vision, vendor or equipment executes. Persisted readback is independently covered by the Controller/Backend tests."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Bulk final consistency UI: {Checks.Count - _failures}/{Checks.Count}");
        return _failures == 0 ? 0 : 1;
    }

    private static CManualInspectionUiSnapshot BulkFinalState()
    {
        var initial = FinalOffsetFixture();
        var first = initial.Points[0] with { WorkingJudge = "OK", OriginalJudge = "OK", WorkingXUm = 2, WorkingYUm = -3, OriginalXUm = 2, OriginalYUm = -3, BaseXUm = 4, BaseYUm = 6,
            HasAppliedOffset = true, IsMasked = false, IsOperatorResult = false, IsProxy = false, IsMeasurementOffsetInitialized = false, IsMeasurementStateCleared = false, Source = "MEASURED", ReferenceFinalXUm = 6, ReferenceFinalYUm = 3, CandidateXUm = null, CandidateYUm = null };
        var second = initial.Points[1] with { WorkingJudge = "NG", OriginalJudge = "NG", WorkingXUm = 9, WorkingYUm = 9, OriginalXUm = 9, OriginalYUm = 9, BaseXUm = 8, BaseYUm = -4,
            HasAppliedOffset = true, IsMasked = false, IsOperatorResult = false, IsProxy = false, IsMeasurementOffsetInitialized = false, IsMeasurementStateCleared = false, Source = "MEASURED", CandidateXUm = null, CandidateYUm = null };
        return initial with
        {
            Version = 600, RecipeId = "UI_BULK_MODEL", RunId = "UI_BULK_RUN", Points = [first, second], ActiveRevisionId = "B-BEFORE", CandidateRows = [], CandidateStatus = "후보 없음",
            HasFinalOffsetSnapshot = true, FinalOffsetPoints = new Dictionary<string, CManualInspectionUiOffset> { [first.Key] = new(4, 6), [second.Key] = new(8, -4) },
            FinalAppliedPointKeys = new HashSet<string>([first.Key, second.Key], StringComparer.Ordinal), FinalInitializedPointKeys = new HashSet<string>(), FinalInitializedOffsetKeys = new HashSet<string>(),
            FinalOffsetRevisionId = "SAVED-BEFORE", FinalOffsetFileName = "20261124_before_reviewoffset.csv", FinalOffsetFilePath = @"C:\SyntheticOnly\Config\RECIPE\UI_BULK_MODEL_reviewoffset\20261124_before_reviewoffset.csv",
            FinalOffsetContentSha256 = new string('a', 64), FinalOffsetRowCount = 2, FinalOffsetInitializedRowCount = 0, LastSaveVerificationCode = "NONE", LastSaveVerificationText = "", LastSavedRevisionId = "",
            CanBulkApply = true, BulkEligibleCount = 1, BulkSummaryText = "모델 전체 · 보정 대상 1 · NG 1", DeltaLimitUm = 31, TotalLimitUm = 402,
            IsOffsetEditPending = false, OffsetInitPending = false, IsBusy = false
        };
    }

    private static CManualInspectionUiSnapshot BulkFinalVerified(CManualInspectionUiSnapshot before) => before with
    {
        Version = before.Version + 1, ActiveRevisionId = "B-AFTER", CanBulkApply = false, BulkEligibleCount = 0, CandidateStatus = "APPLIED",
        Points = before.Points.Select((point, i) => i == 0 ? point with { BaseXUm = 6, BaseYUm = 3 } : point).ToArray(),
        FinalOffsetPoints = new Dictionary<string, CManualInspectionUiOffset>(before.FinalOffsetPoints) { [before.Points[0].Key] = new(6, 3) },
        FinalOffsetRevisionId = "SAVED-AFTER", LastSavedRevisionId = "SAVED-AFTER", LastSaveVerificationCode = "VERIFIED",
        LastSaveVerificationText = "저장·CSV 확인 완료", FinalOffsetStatusText = "저장된 최종 Review 보정 · DESIGN µm",
        FinalOffsetFileName = "20261124_after_reviewoffset.csv", FinalOffsetFilePath = @"C:\SyntheticOnly\Config\RECIPE\UI_BULK_MODEL_reviewoffset\20261124_after_reviewoffset.csv", FinalOffsetContentSha256 = new string('b', 64)
    };

    private static string BulkFinalRaw(CManualInspectionUiSnapshot state) => JsonSerializer.Serialize(state.Points.Select(point => new { point.Key, point.WorkingJudge, point.WorkingXUm, point.WorkingYUm, point.OriginalJudge, point.OriginalXUm, point.OriginalYUm }));
    private static void BulkFinalMeasurementMode(CManualInspectionView view)
    {
        SelectViewChoice(view, "ColorFilter", "level");
        SelectViewChoice(view, "ScatterDataCombo", "WORKING");
    }
    private static void RunBulkFinalConfirmationCases(List<object> observations)
    {
        LoadResources("Dark"); var state = BulkFinalState(); var fake = new FakeController(state); var view = new CManualInspectionView { DataContext = fake }; var window = HostRepresentativeView(view);
        try
        {
            BulkFinalMeasurementMode(view); SelectViewChoice(view, "JudgeFilter", "NG");
            string seen = ""; var confirms = 0;
            view.ConfirmationForTesting = (title, text, button) => { confirms++; seen = title + "\n" + text + "\n" + button; return false; };
            ((Button)view.FindName("BulkApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Test("bulk_final_cancel_preserves_user_mode_and_does_not_dispatch", confirms == 1 && fake.Calls.Count == 0 && Field<string>(view, "_color") == "level" && !Field<bool>(view, "_scatterFinalOffsets"));
            Test("bulk_final_confirmation_identifies_actual_model_and_whole_model_scope", seen.Contains(state.RecipeId) && seen.Contains("전체") && seen.Contains("필터") && seen.Contains("1"));
            Test("bulk_final_confirmation_distinguishes_B_plus_delta_from_absolute_reference_F", seen.Contains("B +") && seen.Contains("Δ") && seen.Contains("최종 F") && seen.Contains("대표"));
            Test("bulk_final_confirmation_lists_numeric_limits_and_storage_readback", seen.Contains("31") && seen.Contains("402") && seen.Contains("µm") && seen.Contains("CSV") && seen.Contains("다시 읽"));
            Test("bulk_final_confirmation_retains_ng_unmeasured_and_protected_offsets", seen.Contains("NG") && seen.Contains("미측정") && seen.Contains("Default") && seen.Contains("Distortion"));
            var rawBefore = BulkFinalRaw(state); var verified = BulkFinalVerified(state);
            view.ConfirmationForTesting = (_, _, _) => true;
            fake.OnExecute = (action, _) => { if (action == EN_MANUAL_INSPECTION_UI_ACTION.BulkApply) fake.Replace(verified); };
            ((Button)view.FindName("BulkApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Test("bulk_final_accepted_request_uses_frozen_version_without_motion", fake.Calls.Count == 1 && fake.Calls[0].Action == EN_MANUAL_INSPECTION_UI_ACTION.BulkApply && fake.Calls[0].Request.ExpectedVersion == 600);
            Test("bulk_final_verified_new_csv_switches_actual_color_and_scatter_controls", Field<string>(view, "_color") == "applied" && Field<bool>(view, "_scatterFinalOffsets") && !Field<bool>(view, "_scatterOffsets") && !Field<bool>(view, "_scatterOriginal"));
            Test("bulk_final_model_wide_apply_does_not_drop_user_ng_filter", Field<string>(view, "_judge") == "NG");
            var projected = Field<IReadOnlyList<CManualInspectionUiPoint>>((CManualInspectionMapControl)view.FindName("PositionMap"), "_all");
            Test("bulk_final_verified_map_uses_F_not_previous_B_or_raw_delta", projected.Single(point => point.Key == state.Points[0].Key).BaseXUm == 6 && projected.Single(point => point.Key == state.Points[0].Key).BaseYUm == 3);
            SelectViewChoice(view, "JudgeFilter", "ALL");
            var rows = ((IEnumerable)((DataGrid)view.FindName("ResultGrid")).ItemsSource).Cast<object>().Select(row => (CManualInspectionUiPoint)row.GetType().GetProperty("Point")!.GetValue(row)!).ToArray();
            Test("bulk_final_verified_table_uses_same_saved_F_as_map", rows.Single(point => point.Key == state.Points[0].Key).BaseXUm == 6 && rows.Single(point => point.Key == state.Points[0].Key).BaseYUm == 3);
            var plot = (CManualInspectionScatterControl)view.FindName("ScatterPlot");
            Test("bulk_final_verified_scatter_uses_saved_population_including_retained_ng_B", plot.DataKind == "OFFSETS" && plot.Metrics.Count == 2 && plot.Metrics.MeanX == 7 && plot.Metrics.MeanY == -.5);
            Test("bulk_final_verified_file_and_readback_status_are_current", ((TextBlock)view.FindName("FinalOffsetFileText")).Text == verified.FinalOffsetFileName && ((TextBlock)view.FindName("FinalOffsetInfoText")).Text.Contains(verified.LastSaveVerificationText));
            Test("bulk_final_map_explains_saved_um_and_monitor_scaled_contribution", ((TextBlock)view.FindName("ColorDescriptionText")).ToolTip.ToString()!.Contains("REVIEW_OFFSET_SCALE") && ((TextBlock)view.FindName("ColorDescriptionText")).ToolTip.ToString()!.Contains("mm"));
            Test("bulk_final_apply_changes_display_basis_without_rewriting_raw_measurement", BulkFinalRaw(fake.Snapshot) == rawBefore && state.Points[0].BaseXUm == 4 && state.FinalOffsetPoints[state.Points[0].Key].XUm == 4);
            observations.Add(new { verifiedModel = fake.Snapshot.RecipeId, fake.Snapshot.LastSaveVerificationCode, fake.Snapshot.LastSavedRevisionId, plot.Metrics.MeanX, plot.Metrics.MeanY });
        }
        finally { window.Close(); Pump(); }
    }

    private static void RunBulkFinalOutcomeCases(List<object> observations)
    {
        foreach (var outcome in new[] { "PENDING", "UNSUPPORTED", "READ_FAILED", "SUPERSEDED", "MISMATCH", "NOT_APPLIED", "NOOP_PRIOR_VERIFIED", "SAME_SAVE", "WRONG_REVISION", "NO_FINAL", "THROW", "FORCE" })
        {
            LoadResources("Light"); var state = BulkFinalState();
            if (outcome == "SAME_SAVE") state = BulkFinalVerified(state) with { Version = 600, CanBulkApply = true, BulkEligibleCount = 1 };
            if (outcome == "NOOP_PRIOR_VERIFIED") state = state with { LastSavedRevisionId = "SAVED-BEFORE", LastSaveVerificationCode = "VERIFIED" };
            var fake = new FakeController(state); var view = new CManualInspectionView { DataContext = fake, ConfirmationForTesting = (_, _, _) => true }; var window = HostRepresentativeView(view);
            try
            {
                BulkFinalMeasurementMode(view); var verified = BulkFinalVerified(state);
                var reply = outcome switch
                {
                    "PENDING" or "UNSUPPORTED" or "READ_FAILED" or "SUPERSEDED" or "MISMATCH" => verified with
                    {
                        HasFinalOffsetSnapshot = false, LastSaveVerificationCode = outcome,
                        LastSaveVerificationText = outcome switch { "PENDING" => "CSV 저장됨 · 재확인 중", "UNSUPPORTED" => "CSV 저장됨 · 조회 미지원", "SUPERSEDED" => "CSV 저장됨 · 최신값 변경", "MISMATCH" => "CSV 저장됨 · 내용 불일치", _ => "CSV 저장됨 · 재확인 실패" },
                        FinalOffsetStatusText = "최종 저장값 확인 실패 · 재확인 필요"
                    },
                    "NOT_APPLIED" => state with { Version = 601, LastSaveVerificationCode = "NONE", LastSaveVerificationText = "새로운 적용 결과 없음" },
                    "NOOP_PRIOR_VERIFIED" => state with { Version = 601 },
                    "WRONG_REVISION" => verified with { FinalOffsetRevisionId = "OTHER-EXTERNAL-REVISION" },
                    "NO_FINAL" => verified with { HasFinalOffsetSnapshot = false },
                    _ => verified
                };
                fake.OnExecute = (_, _) => { if (outcome == "THROW") throw new InvalidOperationException("synthetic CSV operation rejected before save"); fake.Replace(reply); };
                var action = outcome == "FORCE" ? EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement : EN_MANUAL_INSPECTION_UI_ACTION.BulkApply;
                ((Task)Invoke(view, "Execute", action, new CManualInspectionUiRequest { ExpectedVersion = state.Version, Keys = [state.Points[0].Key] })!).GetAwaiter().GetResult(); Pump();
                Test("bulk_final_does_not_switch_without_this_verified_save_" + outcome, Field<string>(view, "_color") == "level" && !Field<bool>(view, "_scatterFinalOffsets") && !Field<bool>(view, "_scatterOffsets") && !Field<bool>(view, "_scatterOriginal"));
                Test("bulk_final_outcome_dispatches_only_requested_non_motion_action_" + outcome, fake.Calls.Count == 1 && fake.Calls[0].Action == action);
                if (outcome == "READ_FAILED")
                {
                    var info = ((TextBlock)view.FindName("FinalOffsetInfoText")).Text;
                    Test("bulk_final_unverified_distinguishes_committed_csv_from_readback_failure", info.Contains("CSV 저장됨") && info.Contains("재확인 실패"));
                    SelectViewChoice(view, "ColorFilter", "applied");
                    Test("bulk_final_failed_readback_never_draws_old_B_as_new_saved_F", Field<IReadOnlyList<CManualInspectionUiPoint>>((CManualInspectionMapControl)view.FindName("PositionMap"), "_all").All(point => !point.HasAppliedOffset && point.IsMeasurementOffsetInitialized));
                }
                if (outcome == "THROW") Test("bulk_final_operation_failure_unlocks_confirmation_and_preserves_old_csv", !Field<bool>(view, "_actionConfirmationPending") && fake.Snapshot.FinalOffsetRevisionId == state.FinalOffsetRevisionId);
                observations.Add(new { outcome, fake.Snapshot.LastSaveVerificationCode, currentMode = Field<string>(view, "_color") });
            }
            finally { window.Close(); Pump(); }
        }
    }

    private static void RunBulkFinalLayoutCases(List<object> observations)
    {
        foreach (var theme in new[] { "Dark", "Light" })
        foreach (var size in new[] { (W: 1120d, H: 560d, Name: "notebook"), (W: 1490d, H: 900d, Name: "wide") })
        {
            LoadResources(theme); var state = BulkFinalVerified(BulkFinalState()); var view = new CManualInspectionView { DataContext = new FakeController(state) }; var window = HostRepresentativeView(view, size.W, size.H);
            try
            {
                ((Expander)view.FindName("LeftInspectorSection")).IsExpanded = true; ((TabControl)view.FindName("LeftPaneTabs")).SelectedIndex = 2; Pump();
                var map = (CManualInspectionMapControl)view.FindName("PositionMap"); var scroll = (ScrollViewer)view.FindName("WorkspaceScroll");
                var info = (TextBlock)view.FindName("FinalOffsetInfoText"); var pane = (ScrollViewer)view.FindName("LeftInspectorScroll");
                var bounds = info.TransformToAncestor(pane).TransformBounds(new Rect(0, 0, info.ActualWidth, info.ActualHeight));
                Test("bulk_final_verified_status_fits_without_displacing_map_" + theme + "_" + size.Name, info.IsVisible && bounds.Top >= -1 && bounds.Bottom <= pane.ViewportHeight + 1 && map.ActualWidth >= 350 && map.ActualHeight >= 150 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed);
                var probe = new TextBlock { Text = info.Text, FontFamily = info.FontFamily, FontSize = info.FontSize, FontWeight = info.FontWeight, FontStyle = info.FontStyle, FontStretch = info.FontStretch, TextWrapping = info.TextWrapping };
                probe.Measure(new Size(info.ActualWidth, double.PositiveInfinity));
                Test("bulk_final_status_glyphs_are_not_clipped_inside_height_limit_" + theme + "_" + size.Name, info.Text.Split('\n').Length == 2 && probe.DesiredSize.Height <= info.ActualHeight + 1);
                Render(view, "bulk-final-" + size.Name + "-" + theme.ToLowerInvariant() + ".png"); observations.Add(new { theme, size.Name, map.ActualWidth, map.ActualHeight, statusHeight = info.ActualHeight });
            }
            finally { window.Close(); Pump(); }
        }
    }
}
