using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int Hardening()
    {
        try
        {
            LoadResources("Dark");
            var initial = Snapshot(240) with { Message = "이전 자료를 불러왔습니다." };
            var fake = new FakeController(initial);
            var view = new CManualInspectionView { DataContext = fake, ConfirmationForTesting = (_, _, _) => true };
            var window = new Window { Width = 1120, Height = 720, Content = view, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show(); Pump();
            var map = (CManualInspectionMapControl)view.FindName("PositionMap");
            var scatter = (CManualInspectionScatterControl)view.FindName("ScatterPlot");
            var first = fake.Snapshot.Points.First(p => p.CellNo == 1 && p.HeadNo == 1 && p.CanBeReference);
            var target = fake.Snapshot.Points.First(p => p.CellNo == 1 && p.HeadNo == 2 && p.CanBeReference);
            var another = fake.Snapshot.Points.First(p => p.CellNo == 2 && p.HeadNo == 1 && p.CanBeReference);
            void ClickScatter(CManualInspectionUiPoint point, bool additive = false) =>
                Invoke(view, "Scatter_SelectionRequested", scatter, new CManualInspectionSelectionEventArgs([point.Key], point.Key, additive));
            IReadOnlyList<CManualInspectionUiPoint> Visible() => Field<IReadOnlyList<CManualInspectionUiPoint>>(map, "_visible");
            void Execute(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiRequest request) =>
                ((Task)Invoke(view, "Execute", action, request)!).GetAwaiter().GetResult();
            void Refresh() { Invoke(view, "Refresh"); Pump(); }

            Set(view, "_head", 1); Set(view, "_judge", "NG"); Set(view, "_source", "CORRECTION_BASED"); Set(view, "_query", "not-a-point"); Refresh();
            Test("hidden_scatter_target_is_outside_current_map_filters", !Visible().Any(p => p.Key == target.Key) && scatter.Metrics.Total == 80);
            ClickScatter(target); Pump();
            Test("same_cell_scatter_click_reveals_hidden_head_judge_source_search_target", Visible().Any(p => p.Key == target.Key)
                && Field<string>(view, "_query") == "" && Field<string>(view, "_judge") == "ALL" && Field<string>(view, "_source") == "ALL");
            Test("scatter_click_selects_target_without_device_request", Field<HashSet<string>>(view, "_selected").SetEquals([target.Key]) && fake.Calls.Count == 0);
            Set(view, "_head", 2); Refresh(); ClickScatter(target); Pump();
            Test("already_visible_scatter_click_preserves_useful_filter", Field<int>(view, "_head") == 2);
            Set(view, "_scatterScope", "HEAD"); Set(view, "_scatterHead", 1); Refresh();
            ClickScatter(another, true); Pump();
            Test("cross_cell_scatter_focus_reveals_cell_and_preserves_ctrl_selection", Field<int>(view, "_cell") == 2
                && Visible().Any(p => p.Key == another.Key) && Field<HashSet<string>>(view, "_selected").SetEquals([target.Key, another.Key]));

            Set(view, "_cell", 1); Set(view, "_head", 0); Set(view, "_focusKey", first.Key); Refresh();
            Invoke(view, "SetReference_Click", view, new RoutedEventArgs());
            Invoke(view, "ChangeSelection", new CManualInspectionSelectionEventArgs([target.Key], target.Key));
            Test("direct_reference_enables_copy_to_other_selected_point", ((Button)view.FindName("CopyReferenceButton")).IsEnabled);
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1,
                Points = fake.Snapshot.Points.Select(p => p.Key == first.Key ? p with { Source = "IMPORTED" } : p).ToArray() }); Pump();
            Test("external_source_change_invalidates_representative", Field<string?>(view, "_representativeKey") is null
                && !((Button)view.FindName("CopyReferenceButton")).IsEnabled && !((TextBlock)view.FindName("ReferenceText")).Text.Contains(first.Key));

            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsReadOnly = true, Message = "" }); Pump();
            Test("read_only_source_has_current_notice", ((TextBlock)view.FindName("MessageText")).Text.Contains("조회 전용"));
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsReadOnly = false, Message = "" }); Pump();
            Test("read_only_recovery_clears_old_notice", ((TextBlock)view.FindName("MessageText")).Visibility == Visibility.Collapsed);

            var before = fake.Calls.Count;
            var version = fake.Snapshot.Version;
            view.ConfirmationForTesting = (_, _, _) => false;
            Invoke(view, "Apply_Click", view, new RoutedEventArgs());
            Test("cancelled_apply_never_dispatches", fake.Calls.Count == before);
            view.ConfirmationForTesting = (_, _, _) => { fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1 }); return true; };
            Invoke(view, "Apply_Click", view, new RoutedEventArgs()); Pump();
            Test("external_review_update_during_apply_confirmation_never_dispatches_stale_request", fake.Calls.Count == before
                && ((TextBlock)view.FindName("MessageText")).Text.Contains("바뀌"));
            Test("apply_confirmation_used_frozen_version", fake.Snapshot.Version > version);
            Execute(EN_MANUAL_INSPECTION_UI_ACTION.CopyReference, new() { ExpectedVersion = fake.Snapshot.Version - 1 });
            Test("common_request_gate_rejects_stale_copy", fake.Calls.Count == before);
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsReadOnly = true }); Pump();
            foreach (var action in new[] { EN_MANUAL_INSPECTION_UI_ACTION.Move, EN_MANUAL_INSPECTION_UI_ACTION.Measure,
                EN_MANUAL_INSPECTION_UI_ACTION.Apply, EN_MANUAL_INSPECTION_UI_ACTION.CopyReference, EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits,
                EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels, EN_MANUAL_INSPECTION_UI_ACTION.SetAllowManual })
            {
                Execute(action, new() { ExpectedVersion = fake.Snapshot.Version, Keys = [target.Key], FocusKey = target.Key });
                Test("read_only_request_gate_" + action, fake.Calls.Count == before);
            }
            foreach (var action in new[] { EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint, EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv,
                EN_MANUAL_INSPECTION_UI_ACTION.ExportSession, EN_MANUAL_INSPECTION_UI_ACTION.Refresh, EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,
                EN_MANUAL_INSPECTION_UI_ACTION.ImportSession })
            {
                before = fake.Calls.Count;
                Execute(action, new() { ExpectedVersion = fake.Snapshot.Version, Keys = [target.Key], FocusKey = target.Key });
                Test("read_only_allows_non_device_action_" + action, fake.Calls.Count == before + 1 && fake.Calls[^1].Action == action);
            }
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsBusy = true }); Pump();
            before = fake.Calls.Count;
            Execute(EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint, new() { ExpectedVersion = fake.Snapshot.Version });
            Test("busy_request_gate_keeps_showpoint_disabled", fake.Calls.Count == before);
            Execute(EN_MANUAL_INSPECTION_UI_ACTION.Stop, new() { ExpectedVersion = -1 });
            Test("stop_bypasses_busy_readonly_and_stale_guards", fake.Calls.Count == before + 1 && fake.Calls[^1].Action == EN_MANUAL_INSPECTION_UI_ACTION.Stop);
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, IsBusy = false, IsReadOnly = false, Message = "이전 작업 메시지" }); Pump();
            Invoke(view, "ShowMessage", "현재 입력 확인 오류"); Refresh();
            Test("local_validation_message_survives_same_snapshot_refresh", ((TextBlock)view.FindName("MessageText")).Text == "현재 입력 확인 오류");
            fake.Replace(fake.Snapshot with { Version = fake.Snapshot.Version + 1, Message = "새 자료를 확인했습니다." }); Pump();
            Test("new_snapshot_replaces_old_local_notice", ((TextBlock)view.FindName("MessageText")).Text == "새 자료를 확인했습니다.");
            Render(view, "hardening-dark.png");
            window.Close(); _application.Shutdown();
        }
        catch (Exception ex) { Test("unhandled_ui_hardening_failure", false, ex.ToString()); }
        var result = new { schemaVersion = "a3-manual-inspection-ui-hardening/1", passed = _failures == 0,
            tests = Checks.Count, failures = _failures,
            scope = "Actual WPF view and current themes with a request-recording fake controller. No equipment app, vendor SDK, Vision or motion execution.", checks = Checks };
        File.WriteAllText(Path.Combine(_output, "ui-hardening-results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { result.passed, result.tests, result.failures }));
        return _failures == 0 ? 0 : 1;
    }
}
