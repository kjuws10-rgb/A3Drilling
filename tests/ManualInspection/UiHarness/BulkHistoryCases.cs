using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int BulkHistory()
    {
        try
        {
            LoadResources("Light");var state=Snapshot(240) with{CanBulkApply=true,BulkEligibleCount=180,BulkSummaryText="모델 전체 · 보정 대상 180 · NG 20 · 미측정 40",CanUndoOffsets=false,CanRedoOffsets=false,OffsetHistoryText="되돌리기 0 / 다시 적용 0"};
            var fake=new FakeController(state);var confirms=0;var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>{confirms++;return false;}};
            var window=new Window{Width=1490,Height=900,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
            T Find<T>(string name)=>(T)view.FindName(name);
            void Click(string name){Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
            var map=Find<CManualInspectionMapControl>("PositionMap");
            Find<Expander>("InspectorSection").IsExpanded=true;Find<Expander>("CorrectionSection").IsExpanded=true;Pump();
            Test("bulk_ui_exposes_separate_bulk_apply_undo_redo_controls",Find<Button>("BulkApplyButton").Content.ToString()!.Contains("일괄")&&Find<Button>("RollbackButton").Content.ToString()!.Contains("되돌리기")&&Find<Button>("RedoOffsetsButton").Content.ToString()!.Contains("다시"));
            Test("bulk_ui_initial_history_buttons_disabled",!Find<Button>("RollbackButton").IsEnabled&&!Find<Button>("RedoOffsetsButton").IsEnabled);
            Test("bulk_ui_summary_discloses_model_wide_eligible_and_excluded_counts",Find<TextBlock>("BulkSummaryText").Text==state.BulkSummaryText);
            var p=state.Points.First(x=>!x.IsMasked);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([p.Key],p.Key));Pump();map.ShowOperationMenu();Pump();
            var items=map.ContextMenu!.Items.OfType<MenuItem>().Where(i=>i.IsEnabled).ToArray(); // Disabled selection summary is not an action.
            Test("bulk_ui_point_context_has_move_and_move_measure_without_automatic_correction",items.Count(i=>i.Header.ToString()!.StartsWith("이동",StringComparison.Ordinal))==2&&items.All(i=>!i.Header.ToString()!.Contains("&")&&!i.Header.ToString()!.Contains("보정")));
            Test("bulk_ui_point_context_retains_single_measure_and_details",items.Length==4&&items.Any(i=>i.Header.ToString()!.Contains("현재 위치"))&&items.Any(i=>i.Header.ToString()!.Contains("상세")));map.ContextMenu.IsOpen=false;
            var before=fake.Calls.Count;Click("BulkApplyButton");
            Test("bulk_ui_separate_bulk_apply_is_immediate_without_motion_confirmation",fake.Calls.Count==before+1&&fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.BulkApply&&confirms==0&&fake.Calls[^1].Request.ExpectedVersion==state.Version);
            fake.Replace(state with{Version=8,CanUndoOffsets=true,CanRedoOffsets=true,UndoOffsetCount=8,RedoOffsetCount=2,OffsetHistoryText="되돌리기 8 / 다시 적용 2"});Pump();
            Test("bulk_ui_history_buttons_show_available_depths",Find<Button>("RollbackButton").IsEnabled&&Find<Button>("RedoOffsetsButton").IsEnabled&&Find<Button>("RollbackButton").Content.ToString()!.Contains("8")&&Find<Button>("RedoOffsetsButton").Content.ToString()!.Contains("2"));
            before=fake.Calls.Count;Click("RollbackButton");Click("RedoOffsetsButton");
            Test("bulk_ui_undo_and_redo_dispatch_immediately_without_motion_confirmation",fake.Calls.Count==before+2&&fake.Calls[^2].Action==EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets&&fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets&&confirms==0);
            Test("bulk_ui_history_text_matches_current_cursor_summary",Find<TextBlock>("OffsetHistoryText").Text==fake.Snapshot.OffsetHistoryText);
            fake.Replace(fake.Snapshot with{Version=9,IsBusy=true});Pump();
            Test("bulk_ui_busy_disables_bulk_and_history_edits",!Find<Button>("BulkApplyButton").IsEnabled&&!Find<Button>("RollbackButton").IsEnabled&&!Find<Button>("RedoOffsetsButton").IsEnabled);
            fake.Replace(state with{Version=10,IsOffsetEditPending=true,OffsetInitPending=true});Pump();
            Test("bulk_ui_pending_init_disables_bulk_and_history_edits",!Find<Button>("BulkApplyButton").IsEnabled&&!Find<Button>("RollbackButton").IsEnabled&&!Find<Button>("RedoOffsetsButton").IsEnabled);
            fake.Replace(state with{Version=11,CanBulkApply=false,BulkEligibleCount=0});Pump();Test("bulk_ui_no_eligible_results_disables_bulk_apply",!Find<Button>("BulkApplyButton").IsEnabled);
            foreach(var theme in new[]{"Light","Dark"})
            {
                LoadResources(theme);fake.Replace(state with{Version=12,CanUndoOffsets=true,CanRedoOffsets=true,UndoOffsetCount=7,RedoOffsetCount=3,OffsetHistoryText="되돌리기 7 / 다시 적용 3"});Pump();
                Render(view,"bulk-history-"+theme.ToLowerInvariant()+".png");
                var empty=state.Points.Select(x=>x with{IsMeasurementOffsetInitialized=true,WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=true,BaseXUm=0,BaseYUm=0,Source="OFFSET_INIT"}).ToArray();
                fake.Replace(state with{Version=13,Points=empty,CanBulkApply=false,BulkEligibleCount=0,BulkSummaryText="측정된 보정 대상 없음"});Pump();
                Test("bulk_ui_init_has_no_numeric_deviation_or_precision_level_"+theme,empty.All(x=>!x.HasMeasurementHistory&&x.MeasurementXUm is null&&CManualInspectionDrawing.Level(x,fake.Snapshot)==-1));
                Test("bulk_ui_init_right_result_is_absent_instead_of_zero_"+theme,Find<TextBlock>("PointErrorText").Text.Contains("—")&&!Find<TextBlock>("PointErrorText").Text.Contains("0.000"));
                Render(view,"bulk-history-empty-"+theme.ToLowerInvariant()+".png");
            }
            LoadResources("Dark");var endPoints=state.Points.Select(x=>x with{IsMeasurementOffsetInitialized=true,WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=true,BaseXUm=0,BaseYUm=0,Source="OFFSET_INIT"}).ToArray();
            fake.Replace(state with{Version=14,Points=endPoints,CanBulkApply=false,BulkEligibleCount=0,BulkSummaryText="측정된 보정 대상 없음",CanUndoOffsets=false,CanRedoOffsets=true,UndoOffsetCount=0,RedoOffsetCount=10,OffsetHistoryText="되돌리기 0 / 다시 적용 10 · 가장 이전 보정 상태"});Pump();
            Test("bulk_ui_oldest_retained_state_disables_undo_and_allows_ten_redos",!Find<Button>("RollbackButton").IsEnabled&&Find<Button>("RedoOffsetsButton").IsEnabled&&Find<Button>("RedoOffsetsButton").Content.ToString()!.Contains("10"));
            Render(view,"bulk-history-history-dark.png");
            fake.Replace(state with{Version=14});Find<Expander>("InspectorSection").IsExpanded=false;Find<Expander>("ResultsSection").IsExpanded=false;Find<Expander>("ScatterSection").IsExpanded=false;Pump();
            foreach(var size in new[]{(1490,900),(1120,560)})
            {window.Width=size.Item1;window.Height=size.Item2;Pump();Test("bulk_ui_preserves_default_scroll_free_map_"+size.Item1,Find<ScrollViewer>("WorkspaceScroll").ScrollableHeight<=1&&map.ActualHeight>=350);}
            window.Close();
        }
        catch(Exception error){Test("bulk_history_unhandled",false,error.ToString());}
        var files=new[]{"Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs","Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs"};
        var hashes=files.ToDictionary(f=>f,f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,f)))).ToLowerInvariant());
        var report=new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,sourceHashes=hashes,scope="Source-linked native WPF and synthetic snapshots/fake controller. No equipment App/Root/Manager/SDK, motion, Vision, PLC or scripts executed."};
        File.WriteAllText(Path.Combine(_output,"ui-bulk-history-results.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Bulk history UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }
}
