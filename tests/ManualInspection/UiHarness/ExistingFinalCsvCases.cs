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
        SelectViewChoice(view,"ScatterDataCombo","WORKING");
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
        LoadResources("Dark");var state=ExistingCsvState();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
        try
        {
            var plot=(CManualInspectionScatterControl)view.FindName("ScatterPlot");
            var original=JsonSerializer.Serialize(state.Points);
            Test("existing_csv_constructor_fixed_measurement_mode",MeasurementMapMode(view)=="level"&&plot.DataKind=="WORKING"&&view.FindName("ColorFilter") is null);
            Test("existing_csv_saved_data_is_metadata_not_measurement_projection",Field<IReadOnlyList<CManualInspectionUiPoint>>((CManualInspectionMapControl)view.FindName("PositionMap"),"_all").All(point=>point.WorkingXUm==state.Points.Single(raw=>raw.Key==point.Key).WorkingXUm));
            Test("existing_csv_badge_visible_with_closed_side_panels",((Border)view.FindName("FinalCsvSourceBadge")).IsVisible&&((TextBlock)view.FindName("FinalCsvSourceText")).Text.Contains(state.FinalOffsetFileName));
            Test("existing_csv_badge_tooltip_preserves_path_sha",((Border)view.FindName("FinalCsvSourceBadge")).ToolTip.ToString()!.Contains(state.FinalOffsetFilePath)&&((Border)view.FindName("FinalCsvSourceBadge")).ToolTip.ToString()!.Contains(state.FinalOffsetContentSha256));
            Test("existing_csv_table_has_measurement_only_no_offset_column",view.FindName("ResultOffsetColumn") is null);
            SelectViewChoice(view,"JudgeFilter","NG");SelectViewChoice(view,"ScatterDataCombo","ORIGINAL");
            var bookmark=Invoke(view,"CaptureWorkspaceView")!;
            var changed=state with{Version=401,FinalOffsetFileName="new_reviewoffset.csv",FinalOffsetRevisionId="NEW",FinalOffsetContentSha256=new string('f',64)};fake.Replace(changed);Pump();
            Test("existing_csv_metadata_updates_without_recoloring_measurement",((TextBlock)view.FindName("FinalCsvSourceText")).Text.Contains("new_reviewoffset.csv")&&MeasurementMapMode(view)=="level"&&plot.DataKind=="ORIGINAL");
            Test("existing_csv_user_judge_and_original_scope_survive_same_frame_reload",Field<string>(view,"_judge")=="NG"&&Field<bool>(view,"_scatterOriginal"));
            SelectViewChoice(view,"JudgeFilter","ALL");SelectViewChoice(view,"ScatterDataCombo","WORKING");Invoke(view,"RestoreWorkspaceView",bookmark);Pump();
            Test("existing_csv_file_bookmark_restores_judge_and_original_scope",Field<string>(view,"_judge")=="NG"&&plot.DataKind=="ORIGINAL"&&MeasurementMapMode(view)=="level");
            fake.Replace(changed with{Version=402,RecipeId="OTHER",RunId="OTHER"});Pump();
            Test("existing_csv_new_model_defaults_measurement_working",MeasurementMapMode(view)=="level"&&plot.DataKind=="WORKING");
            fake.Replace(state with{Version=403,HasFinalOffsetSnapshot=true,FinalOffsetFileName="",FinalOffsetFilePath="",FinalOffsetStatusText="저장 파일 없음"});Pump();
            Test("existing_csv_no_file_is_explicit_but_does_not_erase_raw",((TextBlock)view.FindName("FinalCsvSourceText")).Text.Contains("없음")&&plot.Metrics.Count>0);
            fake.Replace(state with{Version=404,HasFinalOffsetSnapshot=false,FinalOffsetFileName="",FinalOffsetFilePath="",FinalOffsetStatusText="최종 저장값 조회 실패"});Pump();
            Test("existing_csv_failure_distinguished_from_no_file",((TextBlock)view.FindName("FinalCsvSourceText")).Text.Contains("확인 필요")&&((Border)view.FindName("FinalCsvSourceBadge")).ToolTip.ToString()!.Contains("조회 실패"));
            Test("existing_csv_no_display_action_mutates_source_or_calls_backend",fake.Calls.Count==0&&JsonSerializer.Serialize(state.Points)==original);
            observations.Add(new{existingCsvRemainsMetadata=true,workingPopulation=plot.Metrics.Count});
        }finally{host.Close();Pump();}
    }

    private static void RunExistingFinalCsvLayouts(List<object> observations)
    {
        foreach(var theme in new[]{"Dark","Light"})foreach(var size in new[]{(W:1120d,H:560d,Name:"notebook"),(W:1490d,H:900d,Name:"wide")})
        {
            LoadResources(theme);var fake=new FakeController(ExistingCsvState());var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view,size.W,size.H);
            try
            {
                var map=(CManualInspectionMapControl)view.FindName("PositionMap");var badge=(Border)view.FindName("FinalCsvSourceBadge");var scroll=(ScrollViewer)view.FindName("WorkspaceScroll");
                Test("existing_csv_metadata_badge_does_not_displace_map_"+theme+size.Name,badge.IsVisible&&badge.ActualHeight<35&&map.ActualWidth>=350&&map.ActualHeight>=350&&scroll.ScrollableHeight<=1);
                Test("existing_csv_theme_keeps_fixed_measurement_view_"+theme+size.Name,MeasurementMapMode(view)=="level"&&fake.Calls.Count==0&&ResourceColor("PanelBackgroundBrush")== (theme=="Dark"?"#FF20262D":"#FFFFFFFF"));
                Render(view,"existing-csv-"+size.Name+"-"+theme.ToLowerInvariant()+".png");observations.Add(new{theme,size.Name,map.ActualWidth,map.ActualHeight});
            }finally{host.Close();Pump();}
        }
    }


}
