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
    private static int FinalOffsetCases()
    {
        var observations=new List<object>();
        try
        {
            RunFinalOffsetDisplayCases(observations);
            RunFinalOffsetLayoutCases(observations);
        }
        catch(Exception error){Test("final_offsets_unexpected_exception",false,error.ToString());}
        var files=new[]{"Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs","Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs"};
        var hashes=files.ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,file)))).ToLowerInvariant());
        var report=new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,observations,sourceHashes=hashes,
            scope="Actual source-linked WPF views and current theme resources with immutable synthetic saved-offset metadata and a fake Controller. No production App/Root/Manager, vendor, equipment, motion, Vision, real CSV or offset export executes. Final values and working/raw states are deliberately different."};
        File.WriteAllText(Path.Combine(_output,"ui-final-offsets-results.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Final offsets UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }

    private static CManualInspectionUiSnapshot FinalOffsetFixture()
    {
        var initial=Snapshot(160);int[] indices=[1,2,3,4,5,6,80,81];
        var points=indices.Select((n,i)=>initial.Points[n] with
        {
            WorkingJudge=i==1?"NG":i==2?"UNMEASURED":"OK",WorkingXUm=i==1?.01:i==2?null:.1+i,WorkingYUm=i==2?null:-.2-i,
            OriginalJudge=i==1?"NG":"OK",OriginalXUm=.3+i,OriginalYUm=-.4-i,
            BaseXUm=900+i,BaseYUm=-800-i,HasAppliedOffset=true,IsMasked=i==7,
            IsMeasurementOffsetInitialized=i==2,IsMeasurementStateCleared=i==2,
            IsProxy=false,ReferenceKey="",Source="MEASURED",CandidateXUm=1000+i,CandidateYUm=-1000-i
        }).ToArray();
        double[] xs=[10,2,20,0,0,0,-4,4],ys=[-5,3,7,0,0,0,6,2];
        return initial with
        {
            Version=300,Points=points,HasFinalOffsetSnapshot=true,
            FinalOffsetPoints=points.Select((p,i)=>(p.Key,Value:new CManualInspectionUiOffset(xs[i],ys[i]))).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal),
            FinalAppliedPointKeys=points.Where((_,i)=>i!=5).Select(p=>p.Key).ToHashSet(StringComparer.Ordinal),
            FinalInitializedPointKeys=new HashSet<string>([points[4].Key,points[5].Key],StringComparer.Ordinal),
            FinalOffsetFilePath=@"C:\SyntheticOnly\Config\RECIPE\A3_QA_reviewoffset\20261124_151100_001_reviewoffset.csv",
            FinalOffsetFileName="20261124_151100_001_reviewoffset.csv",FinalOffsetContentSha256=new string('a',64),
            FinalOffsetRevisionId="SAVED-A",FinalOffsetSavedAt=new DateTimeOffset(2026,11,24,15,11,0,TimeSpan.FromHours(9)),
            FinalOffsetRowCount=7,FinalOffsetInitializedRowCount=1,FinalOffsetStatusText="합성 최신 저장 보정 · DESIGN µm · 작업 초안과 별도"
        };
    }

    private static void RunFinalOffsetDisplayCases(List<object> observations)
    {
        LoadResources("Dark");var state=FinalOffsetFixture();var rawBefore=JsonSerializer.Serialize(state.Points);var fake=new FakeController(state);
        var view=new CManualInspectionView{DataContext=fake,ConfirmationForTesting=(_,_,_)=>throw new InvalidOperationException("Display/export must not open a motion confirmation")};
        var window=new Window{Width=1490,Height=900,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
        T Find<T>(string name)=>(T)view.FindName(name);
        void Refresh(){Invoke(view,"Refresh");Pump();}
        void Choose(string name,string value)
        {
            var combo=Find<ComboBox>(name);combo.SelectedItem=combo.Items.Cast<object>().Single(x=>x.GetType().GetProperty("Value")!.GetValue(x)?.ToString()==value);Pump();
        }
        CManualInspectionUiPoint Final(CManualInspectionUiPoint point)=>(CManualInspectionUiPoint)Invoke(view,"FinalOffsetPoint",point)!;
        object[] Rows()=>Find<DataGrid>("ResultGrid").ItemsSource.Cast<object>().ToArray();
        CManualInspectionUiPoint RowPoint(object row)=>(CManualInspectionUiPoint)row.GetType().GetProperty("Point")!.GetValue(row)!;
        string RowText(object row,string name)=>row.GetType().GetProperty(name)!.GetValue(row)?.ToString()??"";
        Set(view,"_cell",0);Set(view,"_head",0);Set(view,"_scatterScope","HEAD");Set(view,"_scatterHead",0);Refresh();
        Test("final_color_and_scatter_modes_are_explicit",Find<ComboBox>("ColorFilter").Items.Count==3&&Find<ComboBox>("ScatterDataCombo").Items.Count==4);
        Test("final_metadata_file_path_and_sha_visible",Find<TextBlock>("FinalOffsetFileText").Text==state.FinalOffsetFileName&&Find<TextBlock>("FinalOffsetFileText").ToolTip.ToString()!.Contains(state.FinalOffsetFilePath)&&Find<TextBlock>("FinalOffsetFileText").ToolTip.ToString()!.Contains(state.FinalOffsetContentSha256));
        Test("final_metadata_model_row_counts_and_unit",Find<TextBlock>("FinalOffsetInfoText").Text.Contains("7 Shot")&&Find<TextBlock>("FinalOffsetInfoText").Text.Contains("초기화 1")&&Find<TextBlock>("FinalOffsetInfoText").Text.Contains("DESIGN µm"));
        Find<Button>("ShowFinalOffsetsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        Test("final_map_shortcut_selects_saved_color_mode_without_controller_call",Field<string>(view,"_color")=="applied"&&fake.Calls.Count==0);
        var projected=Field<IReadOnlyList<CManualInspectionUiPoint>>(Find<CManualInspectionMapControl>("PositionMap"),"_all");
        Test("final_map_uses_saved_values_not_draft_or_candidate",projected.All(p=>p.BaseXUm==state.FinalOffsetPoints[p.Key].XUm&&p.BaseYUm==state.FinalOffsetPoints[p.Key].YUm)&&projected.All(p=>p.BaseXUm!=state.Points.Single(x=>x.Key==p.Key).BaseXUm));
        Test("final_table_exposes_saved_values_column",Find<DataGridTextColumn>("ResultOffsetColumn").Visibility==Visibility.Visible&&Find<DataGridTextColumn>("ResultOffsetColumn").Header.ToString()!.Contains("최종 Review"));
        Test("final_table_rows_match_saved_map",Rows().All(row=>RowPoint(row).BaseXUm==state.FinalOffsetPoints[RowPoint(row).Key].XUm));
        var zeroRow=Rows().Single(row=>RowPoint(row).Key==state.Points[3].Key);var initRow=Rows().Single(row=>RowPoint(row).Key==state.Points[4].Key);
        Test("final_table_actual_zero_and_initialized_empty_are_distinct",RowText(zeroRow,"AppliedOffsetText").Contains("0")&&!RowText(zeroRow,"AppliedOffsetText").Contains("없음")&&RowText(initRow,"AppliedOffsetText")=="없음");
        var ng=Final(state.Points[1]);var brush=(SolidColorBrush)CManualInspectionDrawing.PointBrush(view,ng,"applied",state);
        Test("final_map_ng_is_red_even_with_small_saved_or_measured_value",ng.WorkingJudge=="NG"&&brush.Color.ToString()=="#FFD16776");
        var init=Final(state.Points[4]);var missing=Final(state.Points[5]);var zero=Final(state.Points[3]);
        Test("final_explicit_init_is_gray_without_level",init.IsMeasurementOffsetInitialized&&CManualInspectionDrawing.AppliedLevel(init,state)==-1&&((SolidColorBrush)CManualInspectionDrawing.PointBrush(view,init,"applied",state)).Color.ToString()=="#FFDBE4EA");
        Test("final_missing_row_is_not_invented_zero",missing.IsMeasurementOffsetInitialized&&!missing.HasAppliedOffset&&CManualInspectionDrawing.AppliedLevel(missing,state)==-1);
        Test("final_genuine_saved_zero_has_valid_level",zero.HasAppliedOffset&&!zero.IsMeasurementOffsetInitialized&&zero.BaseXUm==0&&zero.BaseYUm==0&&CManualInspectionDrawing.AppliedLevel(zero,state)==0);
        var draftInit=Final(state.Points[2]);Test("final_saved_snapshot_ignores_unsaved_working_init",!draftInit.IsMeasurementOffsetInitialized&&draftInit.HasAppliedOffset&&draftInit.BaseXUm==20&&CManualInspectionDrawing.AppliedLevel(draftInit,state)>=0);
        Test("final_mask_remains_masked",Final(state.Points[7]).IsMasked&&CManualInspectionDrawing.AppliedLevel(Final(state.Points[7]),state)==-1);
        Choose("ScatterDataCombo","FINAL_OFFSETS");var plot=Find<CManualInspectionScatterControl>("ScatterPlot");
        Test("final_scatter_is_saved_offset_population",plot.DataKind=="OFFSETS"&&plot.Metrics.Count==5&&Math.Abs(plot.Metrics.MeanX!.Value-5.6)<1e-9&&Math.Abs(plot.Metrics.MeanY!.Value-2.2)<1e-9);
        Test("final_scatter_excludes_init_missing_mask_but_keeps_real_zero_and_ng_B",plot.Metrics.Count==5&&plot.Metrics.Total==8&&plot.Metrics.Masked==1&&plot.Metrics.Unmeasured==2);
        Test("final_view_does_not_mutate_raw_working_or_candidate",JsonSerializer.Serialize(state.Points)==rawBefore&&JsonSerializer.Serialize(fake.Snapshot.Points)==rawBefore&&fake.Calls.Count==0);
        Choose("ScatterDataCombo","ORIGINAL");Test("final_original_scatter_remains_original_values",plot.DataKind=="ORIGINAL"&&Math.Abs(plot.Metrics.MeanX!.Value-state.Points.Where(p=>!p.IsMasked).Average(p=>p.OriginalXUm!.Value))<1e-9);
        Choose("ScatterDataCombo","FINAL_OFFSETS");var oldMean=plot.Metrics.MeanX;
        var changed=new Dictionary<string,CManualInspectionUiOffset>(state.FinalOffsetPoints,StringComparer.Ordinal){[state.Points[0].Key]=new(30,-5)};
        state=state with{Version=301,FinalOffsetPoints=changed,FinalOffsetRevisionId="SAVED-B",FinalOffsetContentSha256=new string('b',64),FinalOffsetFileName="20261124_151200_001_reviewoffset.csv"};fake.Replace(state);Pump();
        Test("final_only_metadata_revision_refresh_rebuilds_same_raw_point_array",plot.Metrics.MeanX!=oldMean&&Math.Abs(plot.Metrics.MeanX!.Value-9.6)<1e-9&&Final(state.Points[0]).BaseXUm==30);
        var dirty=state with{Version=302,IsOffsetEditPending=true,OffsetInitPending=true,CanReturnToPreviousWorkspace=true,SourcePath="synthetic/old-fine.csv",CandidateStatus="DRAFT"};fake.Replace(dirty);Pump();
        Test("final_old_file_and_dirty_draft_keep_latest_saved_display",Field<string>(view,"_color")=="applied"&&Final(dirty.Points[0]).BaseXUm==30&&Find<TextBlock>("FinalOffsetFileText").Text==dirty.FinalOffsetFileName&&fake.Snapshot.SourcePath=="synthetic/old-fine.csv");
        Invoke(view,"Files_Click",view,new RoutedEventArgs());var menu=Find<Button>("FilesButton").ContextMenu!;var export=menu.Items.OfType<MenuItem>().Single(x=>x.Header.ToString()!.Contains("최종 보정 파일 내보내기"));
        Test("final_export_is_available_during_unsaved_file_work",export.IsEnabled&&export.ToolTip!.ToString()!.Contains("미저장 초안은 포함하지"));
        var before=fake.Calls.Count;export.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();menu.IsOpen=false;
        Test("final_export_dispatches_distinct_fresh_read_action",fake.Calls.Count==before+1&&fake.Calls[^1].Action==EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets&&fake.Calls[^1].Request.ExpectedVersion==302);
        Test("final_export_keeps_dirty_snapshot_and_original_points",fake.Snapshot.IsOffsetEditPending&&fake.Snapshot.SourcePath=="synthetic/old-fine.csv"&&JsonSerializer.Serialize(fake.Snapshot.Points)==rawBefore);
        var unknown=state with{Version=303,HasFinalOffsetSnapshot=false,FinalOffsetStatusText="최종 저장값 확인 실패 · 재확인 필요"};fake.Replace(unknown);Pump();
        Test("final_unknown_never_falls_back_to_working_B_or_fake_zero",Final(unknown.Points[0]).IsMeasurementOffsetInitialized&&!Final(unknown.Points[0]).HasAppliedOffset&&CManualInspectionDrawing.AppliedLevel(Final(unknown.Points[0]),unknown)==-1&&plot.Metrics.Count==0&&!Find<Button>("ShowFinalOffsetsButton").IsEnabled);
        Test("final_unknown_metadata_is_honest",Find<TextBlock>("FinalOffsetFileText").Text.Contains("확인 대기")&&Find<TextBlock>("FinalOffsetInfoText").Text.Contains("확인 실패"));
        var none=state with{Version=304,FinalOffsetFilePath="",FinalOffsetFileName="",FinalOffsetStatusText="저장된 측정 보정 파일 없음",FinalAppliedPointKeys=new HashSet<string>(),FinalInitializedPointKeys=state.Points.Select(p=>p.Key).ToHashSet(StringComparer.Ordinal)};fake.Replace(none);Pump();
        Test("final_known_no_file_is_empty_not_perfect_measurement",Find<TextBlock>("FinalOffsetFileText").Text.Contains("파일 없음")&&plot.Metrics.Count==0&&Find<Button>("ShowFinalOffsetsButton").IsEnabled);
        fake.Replace(state with{Version=305});Pump();Set(view,"_color","applied");Set(view,"_scatterFinalOffsets",true);Set(view,"_scatterOffsets",false);Set(view,"_scatterOriginal",false);Refresh();
        var native=fake.Snapshot;fake.OnExecute=(action,_)=>
        {
            if(action==EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile)fake.Replace(native with{Version=306,CanReturnToPreviousWorkspace=true,SourcePath="synthetic/old-one-shot.csv"});
            if(action==EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace)fake.Replace(native with{Version=307});
        };
        ((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,new CManualInspectionUiRequest{ExpectedVersion=305,ResultPath="synthetic/old-one-shot.csv"})!).GetAwaiter().GetResult();Pump();
        Set(view,"_color","judge");Set(view,"_scatterFinalOffsets",false);Set(view,"_scatterOriginal",true);Refresh();
        ((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace,new CManualInspectionUiRequest{ExpectedVersion=306})!).GetAwaiter().GetResult();Pump();
        Test("final_file_return_restores_saved_color_and_scatter_bookmark",Field<string>(view,"_color")=="applied"&&Field<bool>(view,"_scatterFinalOffsets")&&!Field<bool>(view,"_scatterOriginal")&&plot.DataKind=="OFFSETS");
        observations.Add(new{savedMeanX=plot.Metrics.MeanX,savedMeanY=plot.Metrics.MeanY,count=plot.Metrics.Count,rawUnchanged=JsonSerializer.Serialize(state.Points)==rawBefore});
        window.Close();Pump();
    }

    private static void RunFinalOffsetLayoutCases(List<object> observations)
    {
        foreach(var theme in new[]{"Dark","Light"})foreach(var size in new[]{(W:1120d,H:560d,Name:"compact"),(W:1490d,H:900d,Name:"wide")})
        {
            LoadResources(theme);var state=FinalOffsetFixture();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};
            var window=new Window{Width=size.W,Height=size.H,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
            T Find<T>(string name)=>(T)view.FindName(name);
            Find<Expander>("LeftInspectorSection").IsExpanded=true;Find<Expander>("InspectorSection").IsExpanded=true;Find<TabControl>("LeftPaneTabs").SelectedIndex=2;Pump();
            Find<Button>("ShowFinalOffsetsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            bool Fits(string name){var p=Find<FrameworkElement>(name);var viewport=Find<ScrollViewer>("LeftInspectorScroll");var rect=p.TransformToAncestor(viewport).TransformBounds(new Rect(0,0,p.ActualWidth,p.ActualHeight));return p.IsVisible&&rect.Top>=-1&&rect.Bottom<=viewport.ViewportHeight+1&&rect.Left>=-1&&rect.Right<=viewport.ViewportWidth+1;}
            var scroll=Find<ScrollViewer>("WorkspaceScroll");var map=Find<CManualInspectionMapControl>("PositionMap");
            Test("final_metadata_and_shortcut_fit_without_outer_scroll_"+theme+"_"+size.Name,Fits("FinalOffsetFileText")&&Fits("FinalOffsetInfoText")&&Fits("ShowFinalOffsetsButton")&&scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed&&map.ActualWidth>=350&&map.ActualHeight>=150);
            Test("final_saved_map_and_theme_preserve_central_workspace_"+theme+"_"+size.Name,Field<string>(view,"_color")=="applied"&&Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_all").Count>0&&ResourceColor("PanelBackgroundBrush")== (theme=="Dark"?"#FF20262D":"#FFFFFFFF"));
            Render(view,"final-offsets-"+size.Name+"-"+theme.ToLowerInvariant()+".png");observations.Add(new{theme,size.Name,mapWidth=map.ActualWidth,mapHeight=map.ActualHeight});window.Close();Pump();
        }
    }
}
