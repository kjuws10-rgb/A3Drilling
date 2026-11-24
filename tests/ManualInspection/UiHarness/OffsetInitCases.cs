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
            LoadResources("Dark");
            var state=Snapshot(32);var fake=new FakeController(state);var descriptions=new List<string>();
            var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,text,_)=>{descriptions.Add(text);return false;}};
            var window=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
            T Find<T>(string name)=>(T)view.FindName(name);
            void Click(string name){Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
            void Execute(EN_MANUAL_INSPECTION_UI_ACTION action){((Task)Invoke(view,"Execute",action,new CManualInspectionUiRequest{ExpectedVersion=fake.Snapshot.Version,Keys=[state.Points[0].Key]})!).GetAwaiter().GetResult();Pump();}
            var map=Find<CManualInspectionMapControl>("PositionMap");var init=Find<Button>("OffsetInitButton");
            Invoke(view,"SetMapCellScope",1);Pump();
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([state.Points[0].Key],state.Points[0].Key));Pump();
            int AllCellButtons()=> (Field<Rect>(map,"_allCellsButton").IsEmpty?0:1)+Field<List<(int CellNo,Rect Header,Rect AllCells)>>(map,"_cellHeaders").Count(h=>!h.AllCells.IsEmpty);
            Test("offset_ui_single_cell_has_exactly_one_whole_cell_button",AllCellButtons()==1);
            Test("offset_ui_default_folded_workspace_still_has_no_scroll",Find<ScrollViewer>("WorkspaceScroll").ScrollableHeight<=1&&map.ActualHeight>=360);
            Invoke(view,"SetMapCellScope",0);Pump();Test("offset_ui_overview_does_not_draw_duplicate_whole_cell_button",AllCellButtons()==0);
            Invoke(view,"SetMapCellScope",1);Pump();
            Find<Expander>("InspectorSection").IsExpanded=true;Find<Expander>("CorrectionSection").IsExpanded=true;Find<TabControl>("CorrectionPaneTabs").SelectedIndex=1;Pump();
            Test("offset_init_button_is_in_manual_correction_panel_and_initially_ready",init.Content.ToString()=="Offset Init"&&init.IsEnabled&&init.IsVisible);
            Click("OffsetInitButton");Test("offset_init_dispatches_without_confirmation",fake.Calls.Count==1&&descriptions.Count==0);
            var explain=CManualInspectionView.DescribeAction(EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,state,0);
            Test("offset_init_description_names_every_protected_offset_family",new[]{"하드웨어","Default","First","Distortion"}.All(x=>explain.Contains(x,StringComparison.OrdinalIgnoreCase)));
            Test("offset_init_description_explains_full_model_preview_and_old_file_preservation",explain.Contains("전체")&&explain.Contains("저장")&&(explain.Contains("보존")||explain.Contains("남")));
            view.ConfirmationForTesting=(_,text,_)=>{descriptions.Add(text);return true;};Click("OffsetInitButton");
            Test("offset_init_immediate_dispatch_uses_current_version_once_per_click",fake.Calls.Count==2&&fake.Calls.All(c=>c.Action==EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets&&c.Request.ExpectedVersion==state.Version)&&descriptions.Count==0);
            var draft=state with{Version=state.Version+1,IsOffsetEditPending=true,OffsetInitPending=true,OffsetSummaryText="모델 전체 측정 통합 Offset 0 · 미저장 · 하드웨어/Default/First/Distortion 보존",CandidateRows=state.Points.Select(p=>new CManualInspectionUiCandidateRow{Key=p.Key,Source="OFFSET_INIT",BaseXUm=p.BaseXUm,BaseYUm=p.BaseYUm,DeltaXUm=-p.BaseXUm,DeltaYUm=-p.BaseYUm,FinalXUm=0,FinalYUm=0}).ToArray(),CanApply=true,ValidationPassed=true,
                Points=state.Points.Select(p=>p with{BaseXUm=0,BaseYUm=0,CandidateXUm=0,CandidateYUm=0,HasAppliedOffset=!p.IsMasked,IsMeasurementOffsetInitialized=true,AppliedLevel=null,AppliedMetricUm=null,WorkingXUm=null,WorkingYUm=null,WorkingJudge="UNMEASURED",Source="OFFSET_INIT",IsProxy=false}).ToArray()};
            fake.Replace(draft);Pump();
            Test("offset_init_pending_reuses_button_as_cancel_instead_of_adding_duplicate_control",init.Content.ToString()!.Contains("취소")&&init.Tag.ToString()=="CancelOffsetEdit");
            Test("offset_init_pending_labels_the_state_as_unsaved",Find<TextBlock>("ContextText").Text.Contains("미저장")&&Find<TextBlock>("OffsetStateText").Text==draft.OffsetSummaryText&&Find<TextBlock>("CandidateStatusText").Text.Contains("미저장"));
            Test("offset_init_pending_table_and_color_labels_keep_simple_measurement_views",Find<DataGridTextColumn>("ResultOffsetColumn").Header.ToString()!.Contains("작업 통합")&&Find<ComboBox>("ColorFilter").Items.Count==3&&draft.Points.All(p=>p.IsMeasurementOffsetInitialized));
            Test("offset_init_pending_only_exposes_context_save_with_automatic_validation",!Find<Button>("CalculateButton").IsEnabled&&!Find<Button>("LoadRecipeButton").IsEnabled&&Find<Button>("ValidateButton").Visibility==Visibility.Collapsed&&Find<Button>("ApplyButton").IsEnabled&&Find<Button>("ApplyButton").Visibility==Visibility.Visible&&Find<Button>("ApplyButton").Content.ToString()!.Contains("초기화"));
            var count=fake.Calls.Count;foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.Move,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace,EN_MANUAL_INSPECTION_UI_ACTION.Rollback})Execute(action);
            Test("offset_init_pending_common_dispatch_blocks_stale_plan_operations",fake.Calls.Count==count&&Find<TextBlock>("MessageText").Text.Contains("저장"));
            Invoke(view,"Files_Click",view,new RoutedEventArgs());var menu=Find<Button>("FilesButton").ContextMenu!;var items=menu.Items.OfType<MenuItem>().ToArray();
            Test("offset_file_menu_exposes_single_portable_import_and_export",items.Length==6&&items.Count(i=>i.Header.ToString()!.Contains("통합 Offset 파일 가져오기"))==1&&items.Count(i=>i.Header.ToString()!.Contains("작업 Offset 파일 내보내기"))==1&&items.Count(i=>i.Header.ToString()!.Contains("최종 보정 파일 내보내기"))==1);
            Test("offset_pending_menu_allows_exports_but_blocks_switching_sources",items.Where(i=>i.Header.ToString()!.Contains("내보내기")).All(i=>i.IsEnabled)&&items.Where(i=>!i.Header.ToString()!.Contains("내보내기")).All(i=>!i.IsEnabled));menu.IsOpen=false;
            Execute(EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle);Test("offset_pending_export_is_allowed_without_model_save",fake.Calls.Count==count+1&&fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle);
            view.ConfirmationForTesting=(_,text,_)=>{descriptions.Add(text);return false;};count=fake.Calls.Count;Click("OffsetInitButton");Test("offset_edit_cancel_is_immediate_without_confirmation",fake.Calls.Count==count+1&&descriptions.Count==0);
            view.ConfirmationForTesting=(_,text,_)=>{descriptions.Add(text);return true;};Click("OffsetInitButton");Test("offset_edit_cancel_confirmation_dispatches_cancel_only",fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);
            count=fake.Calls.Count;Click("ApplyButton");Test("offset_pending_model_save_confirms_explicit_apply_action",fake.Calls.Count==count+1&&fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.Apply&&descriptions.Count==1);
            window.Width=1480;window.Height=900;Pump();Find<Button>("OffsetInitButton").BringIntoView();Find<Button>("ApplyButton").BringIntoView();Pump();
            foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"offset-init-draft-"+theme.ToLowerInvariant()+".png");}
            window.Width=1120;window.Height=560;Pump();
            fake.Replace(state with{Version=draft.Version+1});Pump();Test("offset_init_cancel_projection_restores_original_labels_and_enabled_operations",!Find<TextBlock>("ContextText").Text.Contains("미저장")&&init.Content.ToString()=="Offset Init"&&Find<Button>("CalculateButton").IsEnabled);
            Find<Expander>("InspectorSection").IsExpanded=false;Pump();Test("offset_init_after_fold_returns_to_scroll_free_map",Find<ScrollViewer>("WorkspaceScroll").ScrollableHeight<=1);
            fake.Replace(state with{Version=draft.Version+2,HasIncomplete=true});Pump();Test("offset_init_button_allows_explicit_idle_reset_after_incomplete_measurement",init.IsEnabled);
            fake.Replace(state with{Version=draft.Version+3,IsReadOnly=true});Pump();Test("offset_init_button_has_no_legacy_readonly_gate",init.IsEnabled&&!Find<TextBlock>("ContextText").Text.Contains("조회 전용"));
            window.Close();
            RunCumulativeScatterChecks();
        }
        catch(Exception e){Test("offset_ui_unexpected_exception",false,e.ToString());}
        var files=new[]{"Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs","Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs"};
        var hashes=files.ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,file)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output,"ui-offset-init-results.json"),JsonSerializer.Serialize(new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,sourceHashes=hashes,scope="Current source-linked WPF Manual Inspection and app themes with synthetic immutable snapshots/fake controller. No equipment App/Root/Manager/vendor SDK, PLC/Vision/motion or scripts."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Offset UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }
    private static void RunCumulativeScatterChecks()
    {
        var template=Snapshot(1).Points[0];
        CManualInspectionUiPoint At(string key,int cell,int head,double x,double y,bool applied=true)=>template with{Key=key,CellNo=cell,HeadNo=head,ShotNo=1,BeamNo=11,Row=0,Column=0,IsMasked=false,BaseXUm=x,BaseYUm=y,HasAppliedOffset=applied,WorkingXUm=999,WorkingYUm=-999,WorkingJudge="OK",OriginalXUm=null,OriginalYUm=null,OriginalJudge="UNMEASURED",IsProxy=false,Source="MEASURED"};
        var a=At("A",1,1,12,-8) with{WorkingXUm=.1,WorkingYUm=-.2,OriginalXUm=.1,OriginalYUm=-.2,OriginalJudge="OK"};
        var zero=At("ZERO",1,2,0,0) with{WorkingJudge="NG"};var b=At("B",2,2,-30,25);
        var missing=At("MISSING",2,1,888,888,false);var masked=At("MASKED",3,1,777,777) with{IsMasked=true};
        var error=At("ERROR_WITH_B",3,1,6,4) with{WorkingJudge="ERROR",WorkingXUm=null,WorkingYUm=null};
        var snapshot=Snapshot(1) with{Version=2500,Points=[a,zero,b,missing,masked,error],IsOffsetEditPending=false};
        var serialized=JsonSerializer.Serialize(snapshot.Points);var fake=new FakeController(snapshot);var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>false};
        var window=new Window{Width=1480,Height=900,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
        var plot=(CManualInspectionScatterControl)view.FindName("ScatterPlot");var combo=(ComboBox)view.FindName("ScatterDataCombo");var scope=(Expander)view.FindName("ScatterSection");scope.IsExpanded=true;Pump();
        Test("scatter_defaults_to_measurement_residuals_and_all_heads",!Field<bool>(view,"_scatterOffsets")&&!Field<bool>(view,"_scatterOriginal")&&Field<string>(view,"_scatterScope")=="HEAD"&&Field<int>(view,"_scatterHead")==0&&plot.DataKind=="WORKING");
        // The following legacy cumulative-Offset checks explicitly select that retained optional mode.
        combo.SelectedItem=combo.Items.Cast<object>().Single(i=>i.GetType().GetProperty("Value")!.GetValue(i)?.ToString()=="OFFSETS");Pump();
        Test("scatter_cumulative_uses_base_even_when_fresh_residual_or_ng_exists",plot.Metrics.Count==4&&Math.Abs(plot.Metrics.MeanX.GetValueOrDefault(double.NaN)+3)<1e-10&&Math.Abs(plot.Metrics.MeanY.GetValueOrDefault(double.NaN)-5.25)<1e-10);
        Test("scatter_cumulative_keeps_explicit_zero_and_latest_error_with_applied_history",plot.Metrics.Count==4&&Field<IReadOnlyList<CManualInspectionUiPoint>>(plot,"_points").Any(p=>p.Key==zero.Key)&&Field<IReadOnlyList<CManualInspectionUiPoint>>(plot,"_points").Any(p=>p.Key==error.Key));
        Test("scatter_cumulative_does_not_invent_missing_or_masked_offset",plot.Metrics.ValidPoints.Select(p=>p.Key).ToHashSet().SetEquals([a.Key,zero.Key,b.Key,error.Key]));
        Test("scatter_cumulative_auto_scale_ignores_unrelated_large_raw_and_missing_b",plot.IsAutoScale&&plot.MaxXUm>=30&&plot.MaxXUm<40&&plot.MaxXUm==plot.MaxYUm);
        Test("scatter_cumulative_labels_distinguish_correction_amount_from_residual_error",System.Windows.Automation.AutomationProperties.GetName(plot).Contains("Offset")&&((TextBlock)view.FindName("ScatterLegendText")).Text.Contains("누적"));
        Set(view,"_scatterHead",1);Invoke(view,"Refresh");Pump();Test("scatter_cumulative_head_scope_gathers_all_cells_including_error_history",plot.Metrics.Count==2&&Math.Abs(plot.Metrics.MeanX.GetValueOrDefault(double.NaN)-9)<1e-10);
        Set(view,"_scatterScope","CELL");Invoke(view,"SetMapCellScope",1);Invoke(view,"Refresh");Pump();Test("scatter_cumulative_current_cell_scope_includes_explicit_zero",plot.Metrics.Count==2&&Math.Abs(plot.Metrics.MeanX.GetValueOrDefault(double.NaN)-6)<1e-10);
        Set(view,"_scatterScope","CELLS");Set(view,"_scatterFollowSelection",true);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([a.Key,b.Key],b.Key));Pump();
        Test("scatter_cumulative_multiple_selected_cells_use_entire_cell_populations",plot.Metrics.Count==3&&Math.Abs(plot.Metrics.MeanX.GetValueOrDefault(double.NaN)+6)<1e-10&&Math.Abs(plot.Metrics.MeanY.GetValueOrDefault(double.NaN)-17d/3)<1e-10);
        Set(view,"_judge","NG");Set(view,"_query","not-a-point");Invoke(view,"Refresh");Pump();Test("scatter_cumulative_population_is_not_reduced_by_map_filters",plot.Metrics.Count==3);
        plot.SetFixedHalfRange(10);Pump();Test("scatter_cumulative_fixed_scale_preserves_full_statistics",!plot.IsAutoScale&&plot.MaxXUm==10&&plot.Metrics.Count==3);
        plot.ResetView();Pump();Test("scatter_cumulative_fit_restores_full_multiple_cell_range",plot.IsAutoScale&&plot.MaxXUm>=30&&plot.MaxXUm<40&&plot.Metrics.Count==3);
        combo.SelectedItem=combo.Items.Cast<object>().Single(i=>i.GetType().GetProperty("Value")!.GetValue(i)?.ToString()=="ORIGINAL");Pump();Test("scatter_original_mode_never_substitutes_cumulative_b",plot.DataKind=="ORIGINAL"&&plot.Metrics.Count==1&&Math.Abs(plot.Metrics.MeanX.GetValueOrDefault(double.NaN)-.1)<1e-10);
        combo.SelectedItem=combo.Items.Cast<object>().Single(i=>i.GetType().GetProperty("Value")!.GetValue(i)?.ToString()=="OFFSETS");Pump();Test("scatter_offset_switch_does_not_modify_original_or_working_records",JsonSerializer.Serialize(fake.Snapshot.Points)==serialized&&fake.Calls.Count==0);
        // Keep the filter-independence assertion above, but use a clear whole-model scene for delivery.
        Set(view,"_judge","ALL");Set(view,"_query","");Set(view,"_head",0);Set(view,"_scatterScope","HEAD");Set(view,"_scatterHead",0);Invoke(view,"SetMapCellScope",0);Invoke(view,"Refresh");Pump();
        plot.BringIntoView();Pump();
        foreach(var theme in new[]{"Light","Dark"}){LoadResources(theme);Pump();Render(view,"offset-cumulative-scatter-"+theme.ToLowerInvariant()+".png");}
        window.Close();
    }
}
