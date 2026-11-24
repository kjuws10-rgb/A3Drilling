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
    private static int ExistingFinalCsv()
    {
        var observations=new List<object>();
        try { RunExistingFinalCsvBehavior(observations);RunExistingFinalCsvLayouts(observations); }
        catch(Exception error) { Test("existing_csv_unexpected_exception",false,error.ToString()); }
        var files=new[]{"Views/CManualInspectionView.xaml","Views/CManualInspectionView.xaml.cs","Views/CManualInspectionUiContract.cs","Views/CManualInspectionMapControl.cs","Views/CManualInspectionScatterControl.cs"};
        var hashes=files.ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_uiSource,file)))).ToLowerInvariant());
        var report=new{passed=_failures==0,tests=Checks.Count,failures=_failures,checks=Checks,observations,sourceHashes=hashes,
            scope="Actual source-linked WPF control constructed directly, default selections and existing saved CSV metadata; fake Controller and synthetic records only. No production App/Root/Manager, equipment, vendor, motion, Vision, real CSV or export execution."};
        File.WriteAllText(Path.Combine(_output,"ui-existing-final-csv-results.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Existing final CSV UI: {Checks.Count-_failures}/{Checks.Count}");return _failures==0?0:1;
    }

    // Existing measurement regressions explicitly select their intended old data population.
    // The new default tests below never call this helper before proving constructor/load defaults.
    private static void SelectMeasurementView(CManualInspectionView view)
    {
        SelectViewChoice(view,"ColorFilter","level");SelectViewChoice(view,"ScatterDataCombo","WORKING");
    }

    private static void SelectViewChoice(CManualInspectionView view,string name,string value)
    {
        var combo=(ComboBox)view.FindName(name);
        combo.SelectedItem=combo.Items.Cast<object>().Single(item=>item.GetType().GetProperty("Value")!.GetValue(item)?.ToString()==value);
        Pump();
    }

    private static CManualInspectionUiSnapshot ExistingCsvState()
    {
        var seed=Snapshot(160);var picked=new[]{1,2,3,4,5,80,81,82};
        var points=picked.Select((index,i)=>seed.Points[index] with
        {
            Source="MEASURED",IsProxy=false,ReferenceKey="",IsMasked=i==7,WorkingJudge=i==1?"NG":"OK",
            WorkingXUm=.2+i*.1,WorkingYUm=-.3-i*.1,OriginalJudge=i==1?"NG":"OK",OriginalXUm=.2+i*.1,OriginalYUm=-.3-i*.1,
            HasAppliedOffset=true,BaseXUm=900+i,BaseYUm=-800-i,CandidateXUm=1200+i,CandidateYUm=-1100-i,
            IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=false
        }).ToArray();
        double[] xs=[10,2,20,0,0,-4,4,7],ys=[-5,3,7,0,0,6,2,-8];
        return seed with
        {
            Version=400,RecipeId="test0929",RunId="NATIVE-SYNTHETIC-RUN",CoordinateSignature="EXISTING-CSV-COORD",Points=points,
            HasFinalOffsetSnapshot=true,FinalOffsetPoints=points.Select((point,i)=>(point.Key,Value:new CManualInspectionUiOffset(xs[i],ys[i])))
                .ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal),
            FinalAppliedPointKeys=points.Where((_,i)=>i!=4).Select(point=>point.Key).ToHashSet(StringComparer.Ordinal),
            FinalInitializedPointKeys=new HashSet<string>([points[4].Key],StringComparer.Ordinal),
            FinalOffsetFilePath=@"C:\SyntheticOnly\Config\RECIPE\test0929_reviewoffset\20261124_171100_001_reviewoffset.csv",
            FinalOffsetFileName="20261124_171100_001_reviewoffset.csv",FinalOffsetContentSha256=new string('c',64),FinalOffsetRevisionId="EXISTING-CSV-A",
            FinalOffsetSavedAt=new DateTimeOffset(2026,11,24,17,11,0,TimeSpan.FromHours(9)),FinalOffsetRowCount=7,FinalOffsetInitializedRowCount=1,
            FinalOffsetStatusText="기존 Review Offset CSV · 합성 검증 자료 · 원측정 및 미저장 후보 별도"
        };
    }

    private static void RunExistingFinalCsvBehavior(List<object> observations)
    {
        LoadResources("Dark");var state=ExistingCsvState();var rawBefore=JsonSerializer.Serialize(state.Points);var fake=new FakeController(state);
        // Intentionally no shared view factory and no private-field setup before these assertions.
        var view=new CManualInspectionView();
        Test("existing_csv_constructor_defaults_to_saved_map",Field<string>(view,"_color")=="applied");
        Test("existing_csv_constructor_defaults_to_saved_scatter",Field<bool>(view,"_scatterFinalOffsets")&&!Field<bool>(view,"_scatterOriginal")&&!Field<bool>(view,"_scatterOffsets"));
        view.DataContext=fake;view.ConfirmationForTesting=(_,_,_)=>throw new InvalidOperationException("View/source selection must not confirm a device operation.");
        var window=new Window{Width=1120,Height=560,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};window.Show();Pump();
        T Find<T>(string name)=>(T)view.FindName(name);
        string Choice(string name){var selected=Find<ComboBox>(name).SelectedItem;return selected?.GetType().GetProperty("Value")!.GetValue(selected)?.ToString()??"";}
        void Choose(string name,string value)=>SelectViewChoice(view,name,value);
        var map=Find<CManualInspectionMapControl>("PositionMap");var scatter=Find<CManualInspectionScatterControl>("ScatterPlot");
        IReadOnlyList<CManualInspectionUiPoint> MapPoints()=>Field<IReadOnlyList<CManualInspectionUiPoint>>(map,"_all");
        object[] Rows()=>((IEnumerable)Find<DataGrid>("ResultGrid").ItemsSource).Cast<object>().ToArray();
        CManualInspectionUiPoint RowPoint(object row)=>(CManualInspectionUiPoint)row.GetType().GetProperty("Point")!.GetValue(row)!;
        string SourceText()=>Find<TextBlock>("FinalCsvSourceText").Text;
        string SourceTip()=>Find<Button>("FinalCsvSourceButton").ToolTip?.ToString()??"";
        Test("existing_csv_first_loaded_view_needs_no_mode_button",Choice("ColorFilter")=="applied"&&Choice("ScatterDataCombo")=="FINAL_OFFSETS"&&fake.Calls.Count==0);
        Test("existing_csv_default_map_reads_saved_B_instead_of_raw_or_draft",MapPoints().Count>0&&MapPoints().All(point=>point.BaseXUm==state.FinalOffsetPoints[point.Key].XUm&&point.BaseXUm!=state.Points.Single(raw=>raw.Key==point.Key).BaseXUm));
        Test("existing_csv_default_scatter_reads_saved_population",scatter.DataKind=="OFFSETS"&&scatter.Metrics.Count==6&&Math.Abs(scatter.Metrics.MeanX!.Value-32d/6)<1e-9);
        Test("existing_csv_default_table_shows_saved_B",Find<DataGridTextColumn>("ResultOffsetColumn").Visibility==Visibility.Visible&&Rows().All(row=>RowPoint(row).BaseXUm==state.FinalOffsetPoints[RowPoint(row).Key].XUm));
        Find<Expander>("LeftInspectorSection").IsExpanded=false;Find<Expander>("InspectorSection").IsExpanded=false;Pump();
        Test("existing_csv_source_is_visible_when_both_side_panels_are_closed",Find<Button>("FinalCsvSourceButton").IsVisible&&Find<Button>("FinalCsvSourceButton").ActualWidth>50&&SourceText().Contains(state.FinalOffsetFileName));
        Test("existing_csv_source_tooltip_has_exact_absolute_path_and_sha",SourceTip().Contains(state.FinalOffsetFilePath)&&SourceTip().Contains(state.FinalOffsetContentSha256));
        Test("existing_csv_view_setup_preserves_all_raw_and_candidate_values",JsonSerializer.Serialize(fake.Snapshot.Points)==rawBefore&&fake.Calls.Count==0);

        var first=state.Points[0];var beforeMean=scatter.Metrics.MeanX;
        state=state with{Version=401,FinalOffsetPoints=new Dictionary<string,CManualInspectionUiOffset>(state.FinalOffsetPoints,StringComparer.Ordinal){[first.Key]=new(30,-5)},
            FinalOffsetRevisionId="EXISTING-CSV-B",FinalOffsetFileName="20261124_171200_001_reviewoffset.csv",FinalOffsetContentSha256=new string('d',64),
            FinalOffsetFilePath=@"C:\SyntheticOnly\Config\RECIPE\test0929_reviewoffset\20261124_171200_001_reviewoffset.csv"};fake.Replace(state);Pump();
        Test("existing_csv_metadata_change_updates_map_table_scatter_immediately",MapPoints().Single(point=>point.Key==first.Key).BaseXUm==30
            &&Rows().Select(RowPoint).Single(point=>point.Key==first.Key).BaseXUm==30&&scatter.Metrics.MeanX!=beforeMean);
        Test("existing_csv_metadata_change_updates_visible_source_without_opening_panels",SourceText().Contains(state.FinalOffsetFileName)&&SourceTip().Contains(new string('d',64))&&!Find<Expander>("LeftInspectorSection").IsExpanded);

        Choose("ColorFilter","level");Choose("ScatterDataCombo","WORKING");fake.Replace(state with{Version=402,Message="저장된 CSV 갱신"});Pump();
        Test("existing_csv_explicit_raw_view_stays_selected_during_same_frame_refresh",Choice("ColorFilter")=="level"&&Choice("ScatterDataCombo")=="WORKING"&&scatter.DataKind=="WORKING");
        Test("existing_csv_raw_option_uses_original_working_residual",MapPoints().Single(point=>point.Key==first.Key).WorkingXUm==first.WorkingXUm&&MapPoints().Single(point=>point.Key==first.Key).BaseXUm==first.BaseXUm);
        Choose("ColorFilter","judge");fake.Replace(state with{Version=403});Pump();Test("existing_csv_explicit_judge_view_stays_selected",Choice("ColorFilter")=="judge");
        var calls=fake.Calls.Count;Find<Button>("FinalCsvSourceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        Test("existing_csv_source_one_click_returns_to_final_map_without_motion",Choice("ColorFilter")=="applied"&&Choice("ScatterDataCombo")=="FINAL_OFFSETS"&&fake.Calls.Count==calls);

        state=state with{Version=404,RecipeId="test0930",RunId="NEXT-SYNTHETIC-RUN",CoordinateSignature="NEXT-COORD",
            FinalOffsetFilePath=@"C:\SyntheticOnly\Config\RECIPE\test0930_reviewoffset\20261124_171200_001_reviewoffset.csv"};fake.Replace(state);Pump();
        Test("existing_csv_new_model_resets_to_final_map_and_final_scatter",Choice("ColorFilter")=="applied"&&Choice("ScatterDataCombo")=="FINAL_OFFSETS"&&scatter.DataKind=="OFFSETS");
        Test("existing_csv_new_model_source_does_not_keep_previous_recipe_path",SourceTip().Contains("test0930_reviewoffset")&&!SourceTip().Contains("test0929_reviewoffset"));

        var unknown=state with{Version=405,HasFinalOffsetSnapshot=false,FinalOffsetStatusText="최종 저장 보정 확인 실패 · 모델 좌표 확인 필요"};fake.Replace(unknown);Pump();
        Test("existing_csv_failure_is_not_reported_as_missing_file",SourceText().Contains("확인")&&!SourceText().Contains("파일 없음")&&SourceTip().Contains("확인 실패")&&scatter.Metrics.Count==0);
        Test("existing_csv_failure_does_not_expose_old_file_as_current",!SourceText().Contains(state.FinalOffsetFileName)&&!SourceTip().Contains(state.FinalOffsetContentSha256));
        var empty=state with{Version=406,FinalOffsetFilePath="",FinalOffsetFileName="",FinalOffsetContentSha256="",FinalOffsetSavedAt=null,FinalOffsetRowCount=0,FinalOffsetInitializedRowCount=0,
            FinalOffsetStatusText="저장된 측정 보정 파일 없음",FinalAppliedPointKeys=new HashSet<string>(),FinalInitializedPointKeys=state.Points.Select(point=>point.Key).ToHashSet(StringComparer.Ordinal)};fake.Replace(empty);Pump();
        Test("existing_csv_no_file_is_explicit_empty_not_perfect_zero",SourceText().Contains("없음")&&scatter.Metrics.Count==0&&MapPoints().All(point=>point.IsMeasurementOffsetInitialized&&!point.HasAppliedOffset));

        fake.Replace(state with{Version=407});Pump();Choose("ColorFilter","level");Choose("ScatterDataCombo","ORIGINAL");
        var native=fake.Snapshot;fake.OnExecute=(action,_)=>
        {
            if(action==EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile)fake.Replace(native with{Version=408,RunId="FILE-RUN",CanReturnToPreviousWorkspace=true,SourcePath="synthetic/archived-result.csv"});
            if(action==EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace)fake.Replace(native with{Version=409});
        };
        ((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,new CManualInspectionUiRequest{ExpectedVersion=407,ResultPath="synthetic/archived-result.csv"})!).GetAwaiter().GetResult();Pump();
        Choose("ColorFilter","judge");Choose("ScatterDataCombo","WORKING");
        ((Task)Invoke(view,"Execute",EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace,new CManualInspectionUiRequest{ExpectedVersion=408})!).GetAwaiter().GetResult();Pump();
        Test("existing_csv_file_return_preserves_users_original_display_choice",Choice("ColorFilter")=="level"&&Choice("ScatterDataCombo")=="ORIGINAL"&&scatter.DataKind=="ORIGINAL");
        var bookmark=Invoke(view,"CaptureWorkspaceView")!;bookmark.GetType().GetProperty("Color")!.SetValue(bookmark,"unsupported-old-value");
        Invoke(view,"RestoreWorkspaceView",bookmark);Pump();Test("existing_csv_invalid_bookmark_mode_falls_back_to_final_csv",Choice("ColorFilter")=="applied");
        Test("existing_csv_all_display_switches_keep_raw_records_exact",JsonSerializer.Serialize(fake.Snapshot.Points)==rawBefore&&fake.Calls.All(call=>call.Action is EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile or EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace));
        observations.Add(new{defaultFinalCount=6,visibleFile=SourceText(),savedRevision=state.FinalOffsetRevisionId,rawUnchanged=JsonSerializer.Serialize(fake.Snapshot.Points)==rawBefore});window.Close();Pump();
    }

    private static void RunExistingFinalCsvLayouts(List<object> observations)
    {
        foreach(var theme in new[]{"Dark","Light"})foreach(var size in new[]{(W:1120d,H:560d,Name:"notebook"),(W:1490d,H:900d,Name:"wide")})
        {
            LoadResources(theme);var fake=new FakeController(ExistingCsvState());var view=new CManualInspectionView{DataContext=fake};
            var host=new Window{Width=size.W,Height=size.H,Content=view,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};host.Show();Pump();
            T Find<T>(string name)=>(T)view.FindName(name);
            var source=Find<Button>("FinalCsvSourceButton");var map=Find<CManualInspectionMapControl>("PositionMap");var scroll=Find<ScrollViewer>("WorkspaceScroll");
            bool FitsSource(){var rect=source.TransformToAncestor(view).TransformBounds(new Rect(0,0,source.ActualWidth,source.ActualHeight));return source.IsVisible&&rect.Left>=-1&&rect.Top>=-1&&rect.Right<=view.ActualWidth+1&&rect.Bottom<=view.ActualHeight+1;}
            Test("existing_csv_source_visible_without_opening_any_panel_"+theme+"_"+size.Name,FitsSource()&&!Find<Expander>("LeftInspectorSection").IsExpanded);
            Find<Expander>("LeftInspectorSection").IsExpanded=true;Find<Expander>("InspectorSection").IsExpanded=true;Pump();
            Test("existing_csv_source_and_map_fit_both_panels_without_scroll_"+theme+"_"+size.Name,FitsSource()&&map.ActualWidth>=350&&map.ActualHeight>=150&&scroll.ScrollableHeight<=1&&scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed);
            Test("existing_csv_theme_is_native_"+theme+"_"+size.Name,ResourceColor("PanelBackgroundBrush")== (theme=="Dark"?"#FF20262D":"#FFFFFFFF"));
            Test("existing_csv_layout_defaults_are_final_without_test_mode_setup_"+theme+"_"+size.Name,Field<string>(view,"_color")=="applied"&&Field<bool>(view,"_scatterFinalOffsets")&&fake.Calls.Count==0);
            Render(view,"existing-csv-"+size.Name+"-"+theme.ToLowerInvariant()+".png");observations.Add(new{theme,size.Name,mapWidth=map.ActualWidth,mapHeight=map.ActualHeight,sourceWidth=source.ActualWidth,sourceHeight=source.ActualHeight});host.Close();Pump();
        }
    }
}
