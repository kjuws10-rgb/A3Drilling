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
        LoadResources("Dark");var state=FinalOffsetFixture();var fake=new FakeController(state);var view=new CManualInspectionView{DataContext=fake};var host=HostRepresentativeView(view);
        try
        {
            var plot=(CManualInspectionScatterControl)view.FindName("ScatterPlot");var raw=JsonSerializer.Serialize(state.Points);
            Test("final_offset_metadata_retained_without_color_or_scatter_modes",view.FindName("ShowFinalOffsetsButton") is null&&view.FindName("ColorFilter") is null&&((ComboBox)view.FindName("ScatterDataCombo")).Items.Count==2);
            Test("final_offset_metadata_shows_exact_current_saved_csv",((TextBlock)view.FindName("FinalOffsetFileText")).Text==state.FinalOffsetFileName&&((TextBlock)view.FindName("FinalOffsetFileText")).ToolTip.ToString()!.Contains(state.FinalOffsetContentSha256));
            Test("final_offset_map_is_measurement_not_final_saved_projection",MeasurementMapMode(view)=="level"&&Field<IReadOnlyList<CManualInspectionUiPoint>>((CManualInspectionMapControl)view.FindName("PositionMap"),"_all").All(point=>point.WorkingXUm==state.Points.Single(raw=>raw.Key==point.Key).WorkingXUm));
            var changed=state with{Version=state.Version+1,FinalOffsetRevisionId="NEW-CSV",FinalOffsetFileName="updated_reviewoffset.csv",IsOffsetEditPending=true};fake.Replace(changed);Pump();
            Test("final_offset_new_csv_metadata_is_visible_while_working_draft_preserved",((TextBlock)view.FindName("FinalCsvSourceText")).Text.Contains("updated_reviewoffset.csv")&&fake.Snapshot.IsOffsetEditPending&&MeasurementMapMode(view)=="level");
            Test("final_offset_metadata_refresh_does_not_mutate_raw_or_fake_zero",JsonSerializer.Serialize(fake.Snapshot.Points)==raw&&plot.DataKind=="ORIGINAL");
            SelectViewChoice(view,"ScatterDataCombo","ORIGINAL");var bookmark=Invoke(view,"CaptureWorkspaceView")!;SelectViewChoice(view,"ScatterDataCombo","WORKING");Invoke(view,"RestoreWorkspaceView",bookmark);Pump();
            Test("final_offset_bookmark_preserves_original_measurement_not_saved_mode",plot.DataKind=="ORIGINAL"&&MeasurementMapMode(view)=="level");
            fake.Replace(state with{Version=state.Version+2,HasFinalOffsetSnapshot=false,FinalOffsetFilePath="",FinalOffsetFileName="",FinalOffsetStatusText="조회 실패"});Pump();
            Test("final_offset_failed_read_has_explicit_metadata_warning",((Border)view.FindName("FinalCsvSourceBadge")).ToolTip.ToString()!.Contains("실패")&&MeasurementMapMode(view)=="level");
            Test("final_offset_display_and_bookmark_send_no_motion_or_save",fake.Calls.Count==0);
            observations.Add(new{savedCsvMetadataOnly=true,rawPopulation=plot.Metrics.Count});
        }finally{host.Close();Pump();}
    }

    private static void RunFinalOffsetLayoutCases(List<object> observations)
    {
        foreach(var theme in new[]{"Dark","Light"})foreach(var size in new[]{(W:1120d,H:560d,Name:"notebook"),(W:1490d,H:900d,Name:"wide")})
        {
            LoadResources(theme);var view=new CManualInspectionView{DataContext=new FakeController(FinalOffsetFixture())};var host=HostRepresentativeView(view,size.W,size.H);
            try
            {
                ((Expander)view.FindName("LeftInspectorSection")).IsExpanded=true;((TabControl)view.FindName("LeftPaneTabs")).SelectedIndex=2;Pump();
                var map=(CManualInspectionMapControl)view.FindName("PositionMap");var scroll=(ScrollViewer)view.FindName("WorkspaceScroll");
                Test("final_metadata_compact_page_preserves_map_"+theme+size.Name,((TextBlock)view.FindName("FinalOffsetFileText")).IsVisible&&map.ActualWidth>=350&&map.ActualHeight>=150&&scroll.ScrollableHeight<=1);
                Test("final_metadata_does_not_add_another_action_"+theme+size.Name,view.FindName("FinalCsvSourceBadge") is Border&&view.FindName("ShowFinalOffsetsButton") is null);
                Render(view,"final-metadata-"+size.Name+"-"+theme.ToLowerInvariant()+".png");observations.Add(new{theme,size.Name,map.ActualWidth,map.ActualHeight});
            }finally{host.Close();Pump();}
        }
    }


}
