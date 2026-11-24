using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drilling.UI.Views;

internal static partial class Program
{
    private static string MeasurementMapMode(CManualInspectionView view) => Field<string>((CManualInspectionMapControl)view.FindName("PositionMap"),"_colorMode");
    private static int ReplacementReport(string name, object? observations = null)
    {
        var files = new[]{"Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs","Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs"};
        var hashes = files.ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,file)))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(_output,"ui-"+name+"-results.json"),JsonSerializer.Serialize(new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,observations,sourceHashes=hashes,
            scope="Actual source-linked WPF controls/themes, synthetic immutable snapshots and fake Controller only. No Manager, equipment, motion, Vision, vendor, production CSV or real application executes. Actual replacement arithmetic/CSV persistence is covered separately by Core/Controller/Backend tests. Obsolete final-color/candidate UI assertions are replaced by current measurement-only/Cell-stage policy; generic legacy APIs are not changed by this UI fixture."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(name+" UI: "+(Checks.Count-_failures)+"/"+Checks.Count);return _failures==0?0:1;
    }
    private static CManualInspectionUiSnapshot ReplacementUiState()
    {
        var state=CellCsvState();
        return state with {Version=800,Points=state.Points.Select(point=>point with{ReferenceFinalXUm=point.WorkingXUm,ReferenceFinalYUm=point.WorkingYUm}).ToArray(),
            CellBulkEligibleCounts=new Dictionary<int,int>{{1,2},{2,1}},CellBulkSummaryTexts=new Dictionary<int,string>{{1,"Cell 1 · 편차 대체 2점"},{2,"Cell 2 · 편차 대체 1점"}}};
    }
    private static int ReplacementWorkflow()
    {
        var observations=new List<object>();
        try { RunReplacementSurfaceChecks(observations); RunReplacementStageChecks(observations); RunReplacementLayouts(observations); }
        catch(Exception error){Test("replacement_unexpected_exception",false,error.ToString());}
        return ReplacementReport("replacement-workflow",observations);
    }
    private static void RunReplacementSurfaceChecks(List<object> observations)
    {
        LoadResources("Dark");var state=ReplacementUiState();var fake=new FakeController(state);
        var view=new CManualInspectionView{DataContext=fake};var window=HostRepresentativeView(view);
        try
        {
            Test("replacement_constructor_has_single_measurement_color_no_mode_combo",view.FindName("ColorFilter") is null&&MeasurementMapMode(view)=="level"&&((TextBlock)view.FindName("MeasurementLevelLabel")).Text.Contains("측정 편차"));
            foreach(var name in new[]{"CalculateButton","ValidateButton","ApplyButton","AllowManualCheck","CorrectionPaneTabs","CandidateGrid","ValidationText","ShowFinalOffsetsButton","InitScopeCombo","InitCellCombo","InitHeadCombo","PointAppliedText","ResultOffsetColumn"})
                Test("replacement_removed_control_"+name,view.FindName(name) is null);
            var choices=((ComboBox)view.FindName("ScatterDataCombo")).Items.Cast<object>().Select(item=>item.GetType().GetProperty("Value")!.GetValue(item)?.ToString()).ToArray();
            Test("replacement_scatter_only_measurement_and_original",choices.SequenceEqual(new[]{"WORKING","ORIGINAL"}));
            Test("replacement_csv_source_is_noninteractive_badge",view.FindName("FinalCsvSourceButton") is null&&view.FindName("FinalCsvSourceBadge") is Border&&((TextBlock)view.FindName("FinalCsvSourceText")).Text.Contains(state.FinalOffsetFileName));
            Test("replacement_badge_has_exact_saved_path_sha_but_no_action",((Border)view.FindName("FinalCsvSourceBadge")).ToolTip.ToString()!.Contains(state.FinalOffsetFilePath)&&((Border)view.FindName("FinalCsvSourceBadge")).ToolTip.ToString()!.Contains(state.FinalOffsetContentSha256)&&fake.Calls.Count==0);
            var savedOnly=state.Points[0] with{WorkingXUm=null,WorkingYUm=null,OriginalXUm=null,OriginalYUm=null,WorkingJudge="UNMEASURED",OriginalJudge="UNMEASURED",HasAppliedOffset=true,BaseXUm=100,BaseYUm=200,ReferenceFinalXUm=null,ReferenceFinalYUm=null};
            Test("replacement_old_saved_B_only_has_no_measurement_or_level",savedOnly.MeasurementXUm is null&&!savedOnly.HasMeasurementHistory&&CManualInspectionDrawing.Level(savedOnly,state)==-1);
            Test("replacement_old_saved_B_only_is_gray",((SolidColorBrush)CManualInspectionDrawing.PointBrush(view,savedOnly,"level",state)).Color.ToString()=="#FFDBE4EA");
            var malformedOk=savedOnly with{WorkingJudge="OK",IsOperatorResult=true,Source="OPERATOR",ReferenceFinalXUm=12,ReferenceFinalYUm=-8};
            fake.Replace(state with{Version=state.Version+1,Points=[malformedOk]});Pump();
            SelectViewChoice(view,"JudgeFilter","OK");var okCount=Field<IReadOnlyList<CManualInspectionUiPoint>>(view,"_filtered").Count;
            SelectViewChoice(view,"JudgeFilter","UNMEASURED");var emptyCount=Field<IReadOnlyList<CManualInspectionUiPoint>>(view,"_filtered").Count;
            Test("replacement_no_xy_OK_marker_is_unmeasured_and_never_in_OK_filter",okCount==0&&emptyCount==1&&malformedOk.MeasurementXUm is null);
            fake.Replace(state);Pump();SelectViewChoice(view,"JudgeFilter","ALL");

            var zero=savedOnly with{WorkingXUm=0,WorkingYUm=0,WorkingJudge="OK"};
            Test("replacement_real_measured_zero_keeps_level",zero.HasMeasurementHistory&&zero.MeasurementXUm==0&&CManualInspectionDrawing.Level(zero,state)==0);
            var reference=state.Points[0];var target=state.Points[1];var raw=JsonSerializer.Serialize(state.Points.Select(point=>new{point.OriginalJudge,point.OriginalXUm,point.OriginalYUm}));
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([reference.Key],reference.Key));Invoke(view,"SetReference_Click",view,new RoutedEventArgs());
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([target.Key],target.Key));
            string message="";var confirms=0;view.ConfirmationForTesting=(_,text,_)=>{message=text;confirms++;return false;};
            ((Button)view.FindName("CopyReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("replacement_copy_cancel_has_confirmation_and_no_dispatch",confirms==1&&fake.Calls.Count==0);
            Test("replacement_copy_explains_delta_absolute_stage_not_immediate_save",message.Contains("편차 Δ")&&message.Contains("기존 Review Offset에 복사한 편차를 반영")&&message.Contains("아직 CSV")&&message.Contains("현재 Cell"));
            view.ConfirmationForTesting=(_,_,_)=>true;((Button)view.FindName("CopyReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("replacement_copy_only_dispatches_copy_not_apply",fake.Calls.Count==1&&fake.Calls[0].Action==EN_MANUAL_INSPECTION_UI_ACTION.CopyReference&&fake.Calls[0].Request.Keys.SequenceEqual([target.Key]));
            Test("replacement_copy_keeps_saved_csv_and_original_raw_unchanged_in_ui",fake.Snapshot.FinalOffsetRevisionId==state.FinalOffsetRevisionId&&JsonSerializer.Serialize(fake.Snapshot.Points.Select(point=>new{point.OriginalJudge,point.OriginalXUm,point.OriginalYUm}))==raw);
            var values=((double?,double?))Invoke(view,"ReferenceValues",reference)!;
            Test("replacement_preview_uses_current_delta_not_B_plus_delta",values==(2d,1d));
            var old=fake.Calls.Count;
            foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.Calculate,EN_MANUAL_INSPECTION_UI_ACTION.Validate,EN_MANUAL_INSPECTION_UI_ACTION.Apply,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets})
                ((Task)Invoke(view,"Execute",action,new CManualInspectionUiRequest{ExpectedVersion=state.Version,CurrentCellNo=1,Keys=[target.Key]})!).GetAwaiter().GetResult();
            Test("replacement_removed_step_actions_cannot_bypass_single_save_from_view",fake.Calls.Count==old);
            SelectViewChoice(view,"JudgeFilter","NG");Test("replacement_judge_filter_stays_separate_from_fixed_level",Field<IReadOnlyList<CManualInspectionUiPoint>>(view,"_filtered").All(point=>point.WorkingJudge=="NG")&&MeasurementMapMode(view)=="level");
            observations.Add(new{surface="measurement-only",choices,copyRequests=fake.Calls.Count});
        }
        finally{window.Close();Pump();}
    }
    private static void RunReplacementStageChecks(List<object> observations)
    {
        LoadResources("Light");var state=ReplacementUiState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var window=HostRepresentativeView(view);
        try
        {
            var confirms=0;string description="";view.ConfirmationForTesting=(_,text,_)=>{confirms++;description=text;return true;};
            void Click(string name){((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
            Invoke(view,"SetMapCellScope",0);Click("BulkApplyButton");Test("replacement_overview_never_saves_whole_model",fake.Calls.Count==0&&confirms==0);
            ((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle,new CManualInspectionUiRequest{ExpectedVersion=state.Version})!).GetAwaiter().GetResult();
            Test("replacement_overview_import_without_cell_is_rejected",fake.Calls.Count==0);
            Invoke(view,"SetMapCellScope",1);Click("OffsetInitButton");Test("replacement_init_dispatches_current_cell_without_modal_and_describes_immediate_reset",fake.Calls.Count==1&&confirms==0&&fake.Calls[0].Action==EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets&&fake.Calls[0].Request.OffsetInitScope=="CELL"&&fake.Calls[0].Request.CurrentCellNo==1&&fake.Calls[0].Request.OffsetInitCellNo==1&&fake.Calls[0].Request.OffsetInitHeadNo is null&&((Button)view.FindName("OffsetInitButton")).ToolTip.ToString()!.Contains("마지막 저장 Review Offset까지 값 없음으로 즉시 저장"));
            var pending=state with{Version=801,IsOffsetEditPending=true,OffsetInitPending=true,PendingOffsetCellNo=1};fake.Replace(pending);Pump();
            Test("replacement_pending_current_cell_exposes_one_bulk_save_and_cancel",((Button)view.FindName("BulkApplyButton")).IsEnabled&&((Button)view.FindName("OffsetInitButton")).Tag?.ToString()=="CancelOffsetEdit"&&((TextBlock)view.FindName("SaveStatusText")).Text.Contains("미저장"));
            Click("BulkApplyButton");Test("replacement_pending_init_uses_bulk_with_same_explicit_cell",fake.Calls.Count==2&&fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.BulkApply&&fake.Calls.Last().Request.CurrentCellNo==1&&description.Contains("값 없음")&&description.Contains("다른 Cell"));
            Invoke(view,"SetMapCellScope",2);Test("replacement_pending_other_cell_blocks_save",!((Button)view.FindName("BulkApplyButton")).IsEnabled);var before=fake.Calls.Count;Click("BulkApplyButton");Test("replacement_programmatic_other_cell_click_also_cannot_leak_scope",fake.Calls.Count==before);
            Click("OffsetInitButton");Test("replacement_cancel_is_nonmoving_no_modal_and_keeps_csv",fake.Calls.Count==before+1&&fake.Calls.Last().Action==EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit&&fake.Snapshot.FinalOffsetRevisionId==state.FinalOffsetRevisionId);
            fake.Replace(state with{Version=802});Invoke(view,"SetMapCellScope",1);SelectViewChoice(view,"HeadFilter","2");SelectViewChoice(view,"JudgeFilter","NG");before=fake.Calls.Count;Click("BulkApplyButton");
            Test("replacement_bulk_filters_do_not_change_current_cell_scope",fake.Calls.Count==before+1&&fake.Calls.Last().Request.CurrentCellNo==1&&description.Contains("필터")&&description.Contains("F = B + Δ")&&description.Contains("실측·대표 복사"));
            view.ConfirmationForTesting=(_,_,_)=>{Invoke(view,"SetMapCellScope",2);return true;};before=fake.Calls.Count;Invoke(view,"SetMapCellScope",1);Click("BulkApplyButton");Test("replacement_cell_change_inside_modal_rejects_same_version_save",fake.Calls.Count==before);
            fake.Replace(state with{Version=803,IsOffsetEditPending=true,PendingOffsetCellNo=1,OffsetInitPending=false});Invoke(view,"SetMapCellScope",1);Pump();
            Test("replacement_import_stage_has_same_single_save_path",((Button)view.FindName("BulkApplyButton")).IsEnabled&&((TextBlock)view.FindName("SaveStatusText")).Text.Contains("가져온 편차"));
            Invoke(view,"Files_Click",view,new RoutedEventArgs());var menu=((Button)view.FindName("FilesButton")).ContextMenu!;Test("replacement_pending_import_disables_both_file_actions",menu.Items.OfType<MenuItem>().Count()==2&&menu.Items.OfType<MenuItem>().All(item=>!item.IsEnabled));menu.IsOpen=false;
            observations.Add(new{stage="current-cell",fake.Calls.Count,confirms});
        }
        finally{window.Close();Pump();}
    }
    private static void RunReplacementLayouts(List<object> observations)
    {
        foreach(var theme in new[]{"Dark","Light"})foreach(var size in new[]{(W:1120d,H:560d,Name:"notebook"),(W:1490d,H:900d,Name:"wide")})
        {
            LoadResources(theme);var state=ReplacementUiState();var view=new CManualInspectionView{DataContext=new FakeController(state)};var window=HostRepresentativeView(view,size.W,size.H);
            try
            {
                Invoke(view,"SetMapCellScope",1);((Expander)view.FindName("LeftInspectorSection")).IsExpanded=true;((Expander)view.FindName("InspectorSection")).IsExpanded=true;((TabControl)view.FindName("LeftPaneTabs")).SelectedIndex=0;((TabControl)view.FindName("RightPaneTabs")).SelectedIndex=2;Pump();
                var map=(CManualInspectionMapControl)view.FindName("PositionMap");var scroll=(ScrollViewer)view.FindName("WorkspaceScroll");var panel=(ScrollViewer)view.FindName("InspectorScroll");
                bool Fits(string name){var item=(FrameworkElement)view.FindName(name);var b=item.TransformToAncestor(panel).TransformBounds(new Rect(item.RenderSize));return item.IsVisible&&b.Top>=-1&&b.Bottom<=panel.ViewportHeight+1;}
                Test("replacement_single_correction_panel_fits_without_scroll_"+theme+"_"+size.Name,new[]{"BulkApplyButton","OffsetInitButton","RollbackButton","RedoOffsetsButton","SaveStatusText"}.All(Fits)&&scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed&&map.ActualWidth>=350&&map.ActualHeight>=150);
                Test("replacement_fixed_level_theme_unchanged_"+theme+"_"+size.Name,MeasurementMapMode(view)=="level"&&ResourceColor("PanelBackgroundBrush")== (theme=="Dark"?"#FF20262D":"#FFFFFFFF"));
                Render(view,"replacement-"+size.Name+"-"+theme.ToLowerInvariant()+".png");observations.Add(new{theme,size.Name,map.ActualWidth,map.ActualHeight});
            }finally{window.Close();Pump();}
        }
    }
}
