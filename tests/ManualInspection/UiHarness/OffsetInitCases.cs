using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Views;

internal static partial class Program
{
    private static int OffsetInit()
    {
        try
        {
            LoadResources("Dark");var state=ReplacementUiState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view,1120,560);
            try
            {
                var popups=0;view.ConfirmationForTesting=(_,_,_)=>{popups++;return true;};Invoke(view,"SetMapCellScope",1);
                var init=(Button)view.FindName("OffsetInitButton");init.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Test("offset_init_current_cell_request_is_explicit_and_immediate",fake.Calls.Count==1&&popups==0&&fake.Calls[0].Request.CurrentCellNo==1&&fake.Calls[0].Request.OffsetInitCellNo==1&&fake.Calls[0].Request.OffsetInitScope=="CELL");
                Test("offset_init_no_head_or_whole_model_controls",new[]{"InitScopeCombo","InitCellCombo","InitHeadCombo","ApplyButton"}.All(name=>view.FindName(name)is null));
                var explain=CManualInspectionView.DescribeAction(EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,state,0);
                Test("offset_init_protected_offset_families_are_explicit",new[]{"하드웨어","Default","First","Distortion"}.All(explain.Contains)&&explain.Contains("다른 Cell")&&explain.Contains("값 없음"));
                var pending=state with{Version=801,IsOffsetEditPending=true,OffsetInitPending=true,PendingOffsetCellNo=1,Points=state.Points.Select(point=>point.CellNo==1?point with{WorkingXUm=null,WorkingYUm=null,WorkingJudge="UNMEASURED",IsMeasurementOffsetInitialized=true}:point).ToArray()};fake.Replace(pending);Pump();
                Test("offset_init_staged_points_are_empty_not_precision_zero",pending.Points.Where(point=>point.CellNo==1).All(point=>!point.HasMeasurementHistory&&point.MeasurementXUm is null&&CManualInspectionDrawing.Level(point,pending)==-1));
                Test("offset_init_other_cell_measurements_remain_unchanged",pending.Points.Where(point=>point.CellNo!=1).SequenceEqual(state.Points.Where(point=>point.CellNo!=1)));
                Test("offset_init_pending_reuses_init_button_as_cancel",init.Tag?.ToString()=="CancelOffsetEdit"&&init.Content.ToString()!.Contains("취소")&&((Button)view.FindName("BulkApplyButton")).IsEnabled);
                var before=fake.Calls.Count;((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.Move,new CManualInspectionUiRequest{ExpectedVersion=801,Keys=[state.Points[0].Key]})!).GetAwaiter().GetResult();
                Test("offset_init_pending_blocks_moving_plan",fake.Calls.Count==before);
                init.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Test("offset_init_cancel_does_not_save_or_confirm",fake.Calls.Count==before+1&&fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit&&popups==0&&fake.Snapshot.FinalOffsetRevisionId==state.FinalOffsetRevisionId);
                ((Button)view.FindName("BulkApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Test("offset_init_single_bulk_saves_staged_current_cell",fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.BulkApply&&fake.Calls.Last().Request.CurrentCellNo==1&&popups==1);
                fake.Replace(state with{Version=802,IsReadOnly=true});Pump();Test("offset_init_legacy_readonly_flag_does_not_disable_current_cell_init",init.IsEnabled);
                Invoke(view,"SetMapCellScope",0);Test("offset_init_overview_disables_scope_ambiguous_init",!init.IsEnabled);
            }finally{host.Close();Pump();}
            RunCumulativeScatterChecks();
        }catch(Exception error){Test("offset_init_unexpected_exception",false,error.ToString());}
        return ReplacementReport("offset-init");
    }

    private static void RunCumulativeScatterChecks()
    {
        var state=ReplacementUiState();var savedOnly=state.Points[0] with{WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,HasAppliedOffset=true,BaseXUm=999,BaseYUm=-999};
        state=state with{Points=state.Points.Select((point,i)=>i==0?savedOnly:point).ToArray()};var raw=JsonSerializer.Serialize(state.Points);
        var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
        try
        {
            var plot=(CManualInspectionScatterControl)view.FindName("ScatterPlot");SelectViewChoice(view,"ScatterDataCombo","WORKING");
            Test("scatter_working_excludes_saved_B_only_history",plot.Metrics.Count==3&&!plot.Metrics.ValidPoints.Any(point=>point.Key==savedOnly.Key)&&plot.DataKind=="WORKING_REVIEW");
            Test("scatter_auto_scale_uses_current_error_not_large_saved_B",plot.MaxXUm<999&&plot.MaxYUm<999);
            Set(view,"_scatterScope","CELLS");Set(view,"_scatterFollowSelection",true);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[1].Key,state.Points[2].Key],state.Points[2].Key));Pump();
            Test("scatter_selected_cells_use_complete_current_populations",plot.Metrics.Total==4&&plot.Metrics.Count==3);
            Set(view,"_judge","NG");Set(view,"_query","no-match");Invoke(view,"Refresh");Pump();Test("scatter_current_population_independent_of_map_filter",plot.Metrics.Count==3);
            plot.SetFixedHalfRange(10);Test("scatter_fixed_range_does_not_change_population",!plot.IsAutoScale&&plot.Metrics.Count==3&&plot.MaxXUm==10);plot.ResetView();Test("scatter_fit_returns_to_auto_without_B_fallback",plot.IsAutoScale&&plot.MaxXUm<999);
            SelectViewChoice(view,"ScatterDataCombo","ORIGINAL");Test("scatter_original_preserves_camera_raw_for_saved_only_point",plot.Metrics.Count==4&&plot.Metrics.ValidPoints.Any(point=>point.Key==savedOnly.Key));
            Test("scatter_population_changes_never_modify_raw_or_offset",raw==JsonSerializer.Serialize(fake.Snapshot.Points)&&fake.Calls.Count==0);
        }finally{host.Close();Pump();}
    }


}
