using System.Collections;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drilling.UI.Views;

internal static partial class Program
{
    private static CManualInspectionUiSnapshot CalibrationUiState()
    {
        var seed=CellCsvState();var template=seed.Points[0];
        CManualInspectionUiPoint At(string key,int cell,double? rawX,double? rawY,string judge="OK")=>template with
        {
            Key=key,CellNo=cell,HeadNo=1,ShotNo=key[^1]-'0'+1,BeamNo=11,Column=key[^1]-'0',Row=0,IsMasked=false,Source="MEASURED",IsProxy=false,IsOperatorResult=false,
            OriginalJudge=rawX.HasValue?judge:"UNMEASURED",OriginalXUm=rawX,OriginalYUm=rawY,WorkingJudge=rawX.HasValue?judge:"UNMEASURED",WorkingXUm=rawX,WorkingYUm=rawY,
            WorkingValueKind="RESIDUAL",CalibrationBasisReady=true,CalibrationBasisStatusText="합성 가공기준 B_run 10 / 20 µm · Scale 2 / 2",HasAppliedOffset=true,BaseXUm=999,BaseYUm=-999,
            HasUnsavedCorrection=true,ReferenceFinalXUm=11,ReferenceFinalYUm=19.5,ReferenceValueJudge=judge,ReferenceValueKind="CALIBRATED",ReferenceValueUnavailableReason="",IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=false
        };
        var raw=At("P1",1,2,-1);
        var absolute=At("P2",1,null,null) with {Source="OPERATOR",IsOperatorResult=true,WorkingJudge="OK",WorkingValueKind="ABSOLUTE_REVIEW",WorkingXUm=null,WorkingYUm=null,ReferenceFinalXUm=12,ReferenceFinalYUm=-8};
        var approved=At("P3",1,6,-7,"NG") with{Source="OPERATOR",IsOperatorResult=true,WorkingJudge="OK",WorkingValueKind="ABSOLUTE_REVIEW",ReferenceValueJudge="OK",ReferenceFinalXUm=20,ReferenceFinalYUm=30};
        var copied=At("P4",1,-8,9,"NG") with{Source="MANUAL",IsProxy=true,ReferenceKey="P1",WorkingJudge="OK",WorkingValueKind="ABSOLUTE_REVIEW",ReferenceValueJudge="OK",ReferenceFinalXUm=11,ReferenceFinalYUm=19.5};
        var file=At("P5",2,null,null) with{Source="OFFSET_FILE",WorkingValueKind="ABSOLUTE_REVIEW",WorkingJudge="OK",WorkingXUm=500,WorkingYUm=600,ReferenceFinalXUm=3,ReferenceFinalYUm=4};
        var saved=At("P6",2,null,null) with{HasUnsavedCorrection=false,ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="측정 없음"};
        var init=At("P7",2,null,null) with{IsMeasurementOffsetInitialized=true,HasUnsavedCorrection=false,ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="초기화"};
        var zero=At("P8",2,0,0) with{ReferenceFinalXUm=10,ReferenceFinalYUm=20,HasUnsavedCorrection=false};
        var points=new[]{raw,absolute,approved,copied,file,saved,init,zero};
        return seed with{Version=900,RecipeId="CALIBRATION_UI",RunId="SYNTHETIC_BASIS",Points=points,StageXmm=0,StageYmm=0,
            CellLayouts=[],HasFinalOffsetSnapshot=true,FinalOffsetPoints=points.ToDictionary(point=>point.Key,point=>new CManualInspectionUiOffset(10,20)),
            FinalAppliedPointKeys=points.Where(point=>point.Key!="P7").Select(point=>point.Key).ToHashSet(StringComparer.Ordinal),FinalInitializedPointKeys=new HashSet<string>{"P7"},
            CanBulkApply=true,CellBulkEligibleCounts=new Dictionary<int,int>{{1,4},{2,1}},CellBulkSummaryTexts=new Dictionary<int,string>{{1,"Cell 1 · 추천값 4점"},{2,"Cell 2 · 파일값 1점"}},
            CellUnsavedPointCounts=new Dictionary<int,int>{{1,4},{2,1}},UnsavedPointCount=5,CorrectionBasisStatusText="",
            CanUndoOffsets=true,UndoOffsetCount=2,UndoOffsetDescription="Cell 2 · 11-24 20:10 저장 전으로",CanRedoOffsets=true,RedoOffsetCount=1,RedoOffsetDescription="Cell 1 · 11-24 20:12 저장으로",
            LastSaveVerificationCode="VERIFIED",LastSaveVerificationText="저장·CSV 확인 완료",LastSavedRevisionId="OLD-VERIFIED"};
    }
    private static int CalibrationBasisUi()
    {
        var observations=new List<object>();
        try{RunCalibrationValueChecks(observations);RunCalibrationWorkflowChecks(observations);RunCalibrationLayoutChecks(observations);}
        catch(Exception error){Test("calibration_ui_unexpected_exception",false,error.ToString());}
        return ReplacementReport("calibration-basis",observations);
    }
    private static void RunCalibrationValueChecks(List<object> observations)
    {
        LoadResources("Dark");var state=CalibrationUiState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
        try
        {
            var original=JsonSerializer.Serialize(state.Points);var plot=(CManualInspectionScatterControl)view.FindName("ScatterPlot");
            Test("calibration_default_scatter_is_original_residual_not_final_F",plot.DataKind=="ORIGINAL"&&Field<bool>(view,"_scatterOriginal")&&plot.Metrics.Count==4);
            Test("calibration_original_scatter_keeps_raw_NG_and_explicit_zero",plot.Metrics.ValidPoints.Any(point=>point.Key=="P3"&&point.MeasurementJudge=="NG")&&plot.Metrics.ValidPoints.Any(point=>point.Key=="P8"&&point.MeasurementXUm==0));
            var absolute=state.Points[1];var approved=state.Points[2];var copied=state.Points[3];var file=state.Points[4];
            Test("calibration_absolute_F_without_raw_is_not_measurement_or_precision_zero",absolute.CanCopyAsReference&&!absolute.HasMeasurementHistory&&absolute.MeasurementXUm is null&&CManualInspectionDrawing.Level(absolute,state)==-1);
            Test("calibration_manual_approval_keeps_original_NG_residual",approved.WorkingJudge=="OK"&&approved.MeasurementJudge=="NG"&&approved.MeasurementXUm==6&&approved.ReferenceFinalXUm==20);
            Test("calibration_copy_keeps_target_raw_instead_of_source_F",copied.MeasurementXUm==-8&&copied.MeasurementYUm==9&&copied.ReferenceFinalXUm==11);
            var residualApproval=approved with{WorkingValueKind="RESIDUAL"};Test("calibration_residual_approval_also_keeps_original_NG_not_operator_OK",residualApproval.MeasurementJudge=="NG"&&residualApproval.MeasurementXUm==6);
            Test("calibration_file_absolute_F_never_becomes_live_OK_or_error_500",file.MeasurementXUm is null&&!file.HasMeasurementHistory&&file.MeasurementStatusText.Contains("파일값")&&!file.MeasurementStatusText.Contains("OK"));
            string Brush(CManualInspectionUiPoint point)=>((SolidColorBrush)CManualInspectionDrawing.PointBrush(view,point,"level",state)).Color.ToString();
            Test("calibration_original_NG_remains_red_after_manual_approval_or_copy",Brush(approved)=="#FFD16776"&&Brush(copied)=="#FFD16776");
            var ngWithoutNumbers=approved with{OriginalXUm=null,OriginalYUm=null};
            Test("calibration_original_NG_without_numbers_stays_red_not_manual_OK_green",ngWithoutNumbers.MeasurementXUm is null&&Brush(ngWithoutNumbers)=="#FFD16776");
            var error=state.Points[0] with{WorkingJudge="ERROR",WorkingXUm=null,WorkingYUm=null,OriginalJudge="ERROR",OriginalXUm=null,OriginalYUm=null};
            Test("calibration_receive_error_is_named_error_not_unmeasured",error.MeasurementStatusText.Contains("오류")&&Brush(error)=="#FFD39A45");
            Test("calibration_F_only_and_saved_B_only_are_gray_but_true_raw_zero_has_level",Brush(absolute)=="#FFDBE4EA"&&Brush(state.Points[5])=="#FFDBE4EA"&&CManualInspectionDrawing.Level(state.Points[7],state)==0);
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(["P1"],"P1"));Pump();
            Test("calibration_selected_point_shows_current_residual_and_saved_not_draft_B",((TextBlock)view.FindName("PointErrorText")).Text.Contains("+2 / -1")&&((TextBlock)view.FindName("StoredReviewText")).Text.Contains("+10 / +20")&&!((TextBlock)view.FindName("StoredReviewText")).Text.Contains("999"));
            Test("calibration_selected_point_shows_unsaved_recommended_F_separately",((TextBlock)view.FindName("StoredReviewText")).Text.Contains("+11 / +19.5")&&((TextBlock)view.FindName("StoredReviewText")).Text.Contains("미저장"));
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(["P2"],"P2"));Pump();
            Test("calibration_absolute_only_point_reads_no_residual_but_explicit_edit",((TextBlock)view.FindName("PointErrorText")).Text.Contains("없음")&&((TextBlock)view.FindName("PointJudgesText")).Text.Contains("수동")&&((TextBlock)view.FindName("StoredReviewText")).Text.Contains("+12 / -8"));
            Invoke(view,"SetMapCellScope",2);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(["P7"],"P7"));Pump();Test("calibration_initialized_saved_value_is_absent_not_stored_zero",((TextBlock)view.FindName("StoredReviewText")).Text.Contains("Init")&&!((TextBlock)view.FindName("StoredReviewText")).Text.Contains("0 / 0"));
            SelectViewChoice(view,"ScatterDataCombo","WORKING");
            Test("calibration_working_F_scatter_is_named_separately_and_has_no_residual_tolerance",plot.DataKind=="WORKING_REVIEW"&&plot.GetType().GetProperty("IsWorkingReviewData",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(plot) is true);
            Test("calibration_working_plot_has_absolute_only_and_file_F_samples",plot.Metrics.ValidPoints.Any(point=>point.Key=="P2"&&point.MeasurementXUm==12)&&plot.Metrics.ValidPoints.Any(point=>point.Key=="P5"&&point.MeasurementXUm==3));
            Test("calibration_working_plot_does_not_claim_physical_measurement",plot.Metrics.ValidPoints.All(point=>!point.HasActualMeasurement)&&((TextBlock)view.FindName("ScatterMetricsText")).ToolTip.ToString()!.Contains("합격이 아닙니다"));
            Test("calibration_working_origin_counts_distinguish_camera_copy_manual_and_file",new[]{"실측 2","복사 1","수동 2","파일 1"}.All(text=>((TextBlock)view.FindName("ScatterMetricsText")).Text.Contains(text)));
            Test("calibration_projection_never_mutates_raw_working_or_saved_source_records",JsonSerializer.Serialize(fake.Snapshot.Points)==original&&fake.Calls.Count==0);
            fake.Replace(state with{Version=901,HasFinalOffsetSnapshot=false,FinalOffsetStatusText="최종값 조회 실패"});Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(["P1"],"P1"));Pump();
            Test("calibration_failed_saved_read_does_not_fall_back_to_active_draft_B",((TextBlock)view.FindName("StoredReviewText")).Text.StartsWith("저장 Review 확인 필요")&&!((TextBlock)view.FindName("StoredReviewText")).Text.Contains("999"));
            observations.Add(new{workingCount=plot.Metrics.Count,rawPreserved=true});
        }finally{host.Close();Pump();}
    }
    private static void RunCalibrationWorkflowChecks(List<object> observations)
    {
        LoadResources("Light");var state=CalibrationUiState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
        try
        {
            Test("calibration_unsaved_state_wins_over_prior_CSV_verified",((TextBlock)view.FindName("SaveStatusText")).Text.Contains("미저장 4점")&&((TextBlock)view.FindName("SaveStatusText")).Text.Contains("다른 Cell 1점"));
            Test("calibration_primary_save_label_is_short_and_cell_explicit",((Button)view.FindName("BulkApplyButton")).Content.ToString()=="Cell 1 보정 저장");
            Test("calibration_undo_redo_tooltips_name_target_cell_and_time",((Button)view.FindName("RollbackButton")).ToolTip.ToString()==state.UndoOffsetDescription&&((Button)view.FindName("RedoOffsetsButton")).ToolTip.ToString()==state.RedoOffsetDescription);
            var missing=state.Points[0] with{CalibrationBasisReady=false,CalibrationBasisStatusText="가공기준 필요",ReferenceFinalXUm=null,ReferenceFinalYUm=null,ReferenceValueUnavailableReason="가공기준 없음"};
            var missingState=state with{Version=902,Points=[missing],CanBulkApply=false,CellBulkEligibleCounts=new Dictionary<int,int>(),CellBulkSummaryTexts=new Dictionary<int,string>(),CorrectionBasisStatusText="가공기준이 없어 잔차만 측정할 수 있습니다."};fake.Replace(missingState);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs([missing.Key],missing.Key));Pump();
            Test("calibration_missing_basis_blocks_residual_copy_but_preserves_measurement",!missing.CanCopyAsReference&&((Button)view.FindName("MeasureCurrentButton")).IsEnabled&&((TextBlock)view.FindName("BulkSummaryText")).Text.Contains("가공기준 필요"));
            var entered=new Queue<string?>(new[]{"12","-8"});var prompts=new List<string>();view.NumericInputForTesting=(prompt,_)=>{prompts.Add(prompt);return entered.Dequeue();};
            ((Task)Invoke(view,"InputMeasurementValues",missing.Key,missingState.Version)!).GetAwaiter().GetResult();Pump();
            Test("calibration_explicit_absolute_input_works_without_inventing_camera_measurement",fake.Calls.Count==1&&fake.Calls[0].Action==EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement&&fake.Calls[0].Request.ForcedXUm==12&&prompts.All(prompt=>prompt.Contains("Review 보정값"))&&fake.Snapshot.Points[0].OriginalXUm==2);
            fake.Replace(state with{Version=903});Invoke(view,"SetMapCellScope",1);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(["P2"],"P2"));Invoke(view,"SetReference_Click",view,new RoutedEventArgs());
            Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(["P3","P5"],"P3"));var seen="";view.ConfirmationForTesting=(_,text,_)=>{seen=text;return false;};var before=fake.Calls.Count;
            ((Button)view.FindName("CopyReferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("calibration_copy_confirmation_uses_recommended_absolute_F_and_cell_counts",seen.Contains("+12 / -8")&&seen.Contains("Cell 1 1점")&&seen.Contains("Cell 2 1점")&&seen.Split('\n').Length<=5);
            Test("calibration_cancelled_copy_dispatches_no_save_or_copy",fake.Calls.Count==before);
            view.ConfirmationForTesting=(_,text,_)=>{seen=text;return false;};((Button)view.FindName("BulkApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Test("calibration_save_confirmation_is_short_scoped_and_requires_remeasurement",seen.Contains("Cell 1")&&seen.Contains("다른 Cell")&&seen.Contains("재측정")&&seen.Split('\n').Length<=5&&fake.Calls.Count==before);
            Invoke(view,"Files_Click",view,new RoutedEventArgs());var menu=((Button)view.FindName("FilesButton")).ContextMenu!;Test("calibration_export_label_names_last_saved_whole_model",menu.Items.OfType<MenuItem>().Single(item=>item.Header.ToString()!.Contains("내보내기")).Header.ToString()=="마지막 저장 모델 전체 내보내기 (CSV)"&&menu.Items.OfType<MenuItem>().Count()==2);menu.IsOpen=false;
            fake.Replace(state with{Version=904,UnsavedPointCount=0,CellUnsavedPointCounts=new Dictionary<int,int>()});Pump();
            Test("calibration_CSV_verification_is_not_physical_precision_pass",((TextBlock)view.FindName("SaveStatusText")).Text=="저장됨 · 재측정 필요"&&((TextBlock)view.FindName("SaveStatusText")).ToolTip.ToString()!.Contains("실제 정밀도 합격 판정이 아닙니다"));
            observations.Add(new{scopedAndShort=true,noDeviceRequests=fake.Calls.All(call=>call.Action==EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement)});
        }finally{host.Close();Pump();}
    }
    private static void RunCalibrationLayoutChecks(List<object> observations)
    {
        foreach(var theme in new[]{"Dark","Light"})foreach(var size in new[]{(W:1120d,H:560d,Name:"notebook"),(W:1490d,H:900d,Name:"wide")})
        {
            LoadResources(theme);var view=new CManualInspectionView{DataContext=new FakeController(CalibrationUiState())};var host=HostRepresentativeView(view,size.W,size.H);
            try
            {
                Invoke(view,"SetMapCellScope",1);Invoke(view,"ChangeSelection",new CManualInspectionSelectionEventArgs(["P1"],"P1"));((Expander)view.FindName("LeftInspectorSection")).IsExpanded=true;((Expander)view.FindName("InspectorSection")).IsExpanded=true;((TabControl)view.FindName("RightPaneTabs")).SelectedIndex=2;Pump();
                var map=(CManualInspectionMapControl)view.FindName("PositionMap");var pane=(ScrollViewer)view.FindName("InspectorScroll");var outer=(ScrollViewer)view.FindName("WorkspaceScroll");
                bool Fits(string name){var item=(FrameworkElement)view.FindName(name);var rect=item.TransformToAncestor(pane).TransformBounds(new Rect(item.RenderSize));return item.IsVisible&&rect.Top>=-1&&rect.Bottom<=pane.ViewportHeight+1;}
                Test("calibration_short_value_save_controls_fit_without_outer_scroll_"+theme+size.Name,new[]{"StoredReviewText","SaveStatusText","BulkApplyButton","OffsetInitButton","RollbackButton","RedoOffsetsButton"}.All(Fits)&&map.ActualWidth>=350&&map.ActualHeight>=150&&outer.ScrollableHeight<=1);
                Test("calibration_no_new_tabs_modes_or_settings_actions_"+theme+size.Name,((TabControl)view.FindName("RightPaneTabs")).Items.Count==3&&((ComboBox)view.FindName("ScatterDataCombo")).Items.Count==2&&view.FindName("ColorFilter") is null);
                Test("calibration_theme_is_original_dark_light_"+theme+size.Name,ResourceColor("PanelBackgroundBrush")== (theme=="Dark"?"#FF20262D":"#FFFFFFFF"));
                Render(view,"calibration-"+size.Name+"-"+theme.ToLowerInvariant()+".png");observations.Add(new{theme,size.Name,map.ActualWidth,map.ActualHeight});
            }finally{host.Close();Pump();}
        }
    }
}
