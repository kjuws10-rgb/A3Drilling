using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunCellCsvWorkflowChecks()
    {
        static (FakeBackend Fake,CManualInspectionController Controller) TwoCells()
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P3"?p with{CellNo=2}:p).ToArray()};return(f,c);
        }
        await Check("bulk_current_cell_applies_all_its_points_even_if_request_selection_is_other_cell",async()=>
        {
            var(f,c)=TwoCells();await c.EnsureLoaded();True(c.Snapshot.CellBulkEligibleCounts[1]==2&&c.Snapshot.CellBulkEligibleCounts[2]==1);
            var other=c.Snapshot.Points.Single(p=>p.Key=="P3");await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,["P3"],r=>r with{CurrentCellNo=1});
            True(f.Saves==1&&f.Executed.Count==0&&f.SavedConsumption.Select(p=>p.Key).ToHashSet().SetEquals(["P1","P2"]));
            Near(c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm,3);Near(c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm,1);
            var after=c.Snapshot.Points.Single(p=>p.Key=="P3");True(after.BaseXUm==other.BaseXUm&&after.OriginalXUm==other.OriginalXUm&&after.WorkingXUm==other.WorkingXUm);
            True(c.Snapshot.CellBulkEligibleCounts[1]==0&&c.Snapshot.CellBulkEligibleCounts[2]==1&&c.Snapshot.StatusText.Contains("Cell #1"));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=2});True(f.Saves==2&&c.Snapshot.CellBulkEligibleCounts[2]==0);
        });
        await Check("bulk_requires_explicit_existing_positive_cell_and_never_infers_focus_or_selection",async()=>
        {
            var(f,c)=TwoCells();await c.EnsureLoaded();foreach(var cell in new int?[]{null,0,-1,99})
                await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,["P1"],r=>r with{CurrentCellNo=cell,FocusKey="P1"}));
            True(f.Saves==0&&f.Executed.Count==0&&c.Snapshot.CandidateRows.Count==0&&c.Snapshot.CellBulkEligibleCounts[1]==2);
        });
        await Check("bulk_empty_current_cell_does_not_apply_another_eligible_cell",async()=>
        {
            var(f,c)=TwoCells();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.CellNo==1?p with{Judge="NG"}:p).ToArray()};await c.EnsureLoaded();
            True(c.Snapshot.CellBulkEligibleCounts[1]==0&&c.Snapshot.CellBulkEligibleCounts[2]==1&&c.Snapshot.CellBulkSummaryTexts[1].Contains("NG 2"));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=1}));True(f.Saves==0&&c.Snapshot.Points.All(p=>p.BaseXUm==0));
        });
        await Check("representative_measured_review_f_replaces_nonzero_target_and_repeated_copy_does_not_save_twice",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Values=new Dictionary<string,ST_MANUAL_OFFSET>{{"P1",new(10,-2)},{"P2",new(20,5)},{"P3",new(0,0)}}};await c.EnsureLoaded();
            var reference=c.Snapshot.Points.Single(p=>p.Key=="P1");True(reference.ReferenceFinalXUm==3&&reference.ReferenceFinalYUm==-1);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="absolute target replacement"});
            True(f.Saves==0&&f.Source.Values["P2"].XUm==20);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            Near(c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm,reference.ReferenceFinalXUm!.Value);Near(f.Source.Values["P2"].YUm,-1);
            True(f.Saves==1&&f.Source.Values["P2"].XUm==3&&f.Source.Values["P2"].XUm!=33);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="same absolute final"});True(f.Saves==1);
        });
        await Check("current_offset_csv_export_uses_latest_saved_api_and_preserves_pending_init_draft",async()=>
        {
            var(f,_)=Fixture();var b=new ControllerCsvBackend(f);var c=new CManualInspectionController(b);await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);
            var candidate=c.Snapshot.CandidateRows.ToArray();var guards=f.Guards;var file=System.IO.Path.Combine(f.StateDirectory,"saved_export.csv");
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportCurrentOffsetCsv,[],r=>r with{ResultPath=file});
            True(b.CsvExports==1&&b.ExportPath==file&&c.Snapshot.IsOffsetEditPending&&c.Snapshot.CandidateRows.SequenceEqual(candidate)&&f.Saves==0&&f.Guards==guards&&f.Executed.Count==0);
            True(c.Snapshot.StatusText.Contains("미저장 작업값")&&c.Snapshot.FinalOffsetRevisionId==f.Source.Context.RevisionId);
        });
        await Check("current_offset_csv_export_cancel_or_failure_does_not_mutate_model_or_saved_confirmation",async()=>
        {
            var(f,_)=Fixture();var b=new ControllerCsvBackend(f);var c=new CManualInspectionController(b);await c.EnsureLoaded();string? filter=null;c.ChooseFile=(_,ext)=>{filter=ext;return null;};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportCurrentOffsetCsv);True(filter=="csv"&&b.CsvExports==0&&f.Saves==0);
            b.FailExport=true;await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportCurrentOffsetCsv,[],r=>r with{ResultPath="rejected.csv"}));
            True(f.Saves==0&&f.Executed.Count==0&&c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.LastSaveVerificationCode=="NONE"&&!c.IsBusy);
        });
        await Check("native_offset_csv_dialog_defaults_to_current_directory_safe_export_name_and_explicit_overwrite_prompt_without_showing_dialog",async()=>
        {
            var(f,_)=Fixture();var saved=Saved(f.Source,"CURRENT",0) with{FilePath=System.IO.Path.Combine(f.StateDirectory,"20261124_120000_reviewoffset.csv"),FileName="20261124_120000_reviewoffset.csv"};
            var factory=typeof(CManualInspectionController).GetMethod("CreateCurrentOffsetCsvSaveDialog",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
            var dialog=(Microsoft.Win32.SaveFileDialog)factory.Invoke(null,[saved])!;
            if(dialog.InitialDirectory!=f.StateDirectory||dialog.FileName!="20261124_120000_reviewoffset_export.csv"||!dialog.Filter.Contains("*.csv")||dialog.DefaultExt.TrimStart('.')!="csv"||!dialog.AddExtension||!dialog.OverwritePrompt)
                throw new InvalidOperationException($"CSV dialog defaults mismatch: dir={dialog.InitialDirectory}, file={dialog.FileName}, ext={dialog.DefaultExt}, add={dialog.AddExtension}, overwrite={dialog.OverwritePrompt}");
            True(f.Saves==0&&f.Executed.Count==0);await Task.CompletedTask;
        });
        await Check("combined_offset_import_entry_accepts_csv_and_surfaces_unsigned_model_draft_explanation",async()=>
        {
            var(f,_)=Fixture();var b=new ControllerCsvBackend(f);var c=new CManualInspectionController(b);await c.EnsureLoaded();string? chooserKind=null;
            c.ChooseFile=(_,kind)=>{chooserKind=kind;return "external.csv";};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle);
            True(chooserKind=="offset"&&b.ImportPath=="external.csv"&&c.Snapshot.IsOffsetEditPending&&c.Snapshot.StatusText.Contains("모델 정보 없음")&&f.Saves==0&&f.Executed.Count==0);
        });
    }

    private sealed class ControllerCsvBackend(FakeBackend inner):IManualInspectionBackend
    {
        public bool IsSimulation=>false;public string StateDirectory=>inner.StateDirectory;public int CsvExports;public bool FailExport;public string? ExportPath,ImportPath;
        public string ImportedOffsetDescription=>"CSV · 모델 정보 없음 · 현재 모델 QA의 저장 전 작업값";
        public Task<CManualInspectionSource> LoadAsync(string? path,CancellationToken token,IReadOnlyList<string>? displayKeys=null)=>inner.LoadAsync(path,token,displayKeys);
        public Task EnsureCurrentAsync(CManualInspectionSource s,CancellationToken token)=>inner.EnsureCurrentAsync(s,token);
        public Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(CManualInspectionSource s,CancellationToken token)
            =>Task.FromResult<CManualInspectionSavedOffsetSnapshot?>(Saved(inner.Source,inner.Source.Context.RevisionId,0) with{ValuesByPointKey=inner.Source.Values});
        public async Task<CManualInspectionSavedOffsetSnapshot?> ExportCurrentOffsetCsvAsync(CManualInspectionSource s,string path,CancellationToken token,bool overwriteConfirmed=false)
        {True(!overwriteConfirmed);if(FailExport)throw new System.IO.IOException("synthetic CSV export failure");await inner.EnsureCurrentAsync(s,token);CsvExports++;ExportPath=path;return await ReadSavedOffsetSnapshotAsync(s,token);}
        public Task<IReadOnlyDictionary<string,ST_MANUAL_OFFSET>> ImportOffsetBundleAsync(CManualInspectionSource s,string path,CancellationToken token)
        {ImportPath=path;return Task.FromResult(s.Values);}
        public void GuardOperation()=>inner.GuardOperation();public void Stop()=>inner.Stop();
        public Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource s,string key,EN_MANUAL_OPERATION_KIND kind,Action<string> progress,CancellationToken token)=>inner.ExecutePointAsync(s,key,kind,progress,token);
        public Task ExecuteFlyingAsync(CManualInspectionSource s,IReadOnlyList<string> keys,Action<CManualInspectionMeasurement> measured,Action<string> progress,CancellationToken token)=>inner.ExecuteFlyingAsync(s,keys,measured,progress,token);
        public Task<string> SaveAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,CancellationToken token,IReadOnlyDictionary<string,string>? consumed=null)=>inner.SaveAsync(s,values,identity,rows,token,consumed);
    }
}
