using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunCellOverviewChecks()
    {
        await Check("file_workspace_has_explicit_return_without_implicit_device_work", async () =>
        {
            var (f,c)=await Loaded(); True(!c.Snapshot.CanReturnToPreviousWorkspace);
            var file=Archive(f,"one",7); f.FileSources[file.SourcePath]=file;
            await OpenFile(c,file.SourcePath);
            True(c.Snapshot.CanReturnToPreviousWorkspace && !c.Snapshot.IsReadOnly && c.Snapshot.SourcePath==file.SourcePath);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);
            True(!c.Snapshot.CanReturnToPreviousWorkspace && c.Snapshot.SourcePath.Length==0 && f.Executed.Count==0 && f.Saves==0);
        });
        await Check("file_workspace_executes_move_measure_and_correction_with_native_interlocks", async () =>
        {
            var(f,c)=await Loaded(); var file=Archive(f,"executable",7); f.FileSources[file.SourcePath]=file;
            await OpenFile(c,file.SourcePath); await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P2"]);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P2"]);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(f.Executed.SequenceEqual(["P1","P2"]) && f.Saves==1 && !c.Snapshot.IsReadOnly);
        });
        await Check("file_workspace_reconnects_legacy_source_and_requires_fresh_measurement_before_correction", async () =>
        {
            var(f,c)=await Loaded(); var file=Archive(f,"unsafe",7) with {ReadOnly=true};file=file with{Context=file.Context with{LineageValid=false}}; f.FileSources[file.SourcePath]=file;
            await OpenFile(c,file.SourcePath); await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]));await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);await Prepared(c);True(f.Executed.Count==2&&f.Saves==0&&c.Snapshot.CanApply);
        });
        await Check("return_restores_native_candidate_raw_limits_and_level_options", async () =>
        {
            var(f,c)=await Loaded();
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits,null,r=>r with{DeltaLimitUm=40,TotalLimitUm=250,ScannerFieldMm=6});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,null,r=>r with{LevelMetric="RADIAL",LevelBoundsUm=[2,4,8],LevelNames=["A","B","C","D"]});
            await Prepared(c); var before=c.Snapshot;
            var file=Archive(f,"different",9); f.FileSources[file.SourcePath]=file; await OpenFile(c,file.SourcePath);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,null,r=>r with{LevelMetric="MAX_AXIS",LevelBoundsUm=[5,10,20],LevelNames=["N","O","W","E"]});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);
            True(c.Snapshot.CanApply && c.Snapshot.CandidateRows.SequenceEqual(before.CandidateRows));
            True(c.Snapshot.Points.Select(p=>p.MeasurementToken).SequenceEqual(before.Points.Select(p=>p.MeasurementToken)));
            True(c.Snapshot.LevelMetric=="RADIAL" && c.Snapshot.LevelBoundsUm.SequenceEqual([2d,4,8]) && c.Snapshot.LevelNames.SequenceEqual(["A","B","C","D"]));
            True(c.Snapshot.DeltaLimitUm==40 && c.Snapshot.TotalLimitUm==250 && c.Snapshot.ScannerFieldMm==6 && f.Saves==0);
        });
        await Check("return_keeps_copied_native_working_values_and_original_provenance", async () =>
        {
            var(f,c)=await Loaded(); await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="native copy before file"});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Validate,null,r=>r with{AllowManual=true}); var old=c.Snapshot.Points.Single(p=>p.Key=="P2");
            var file=Archive(f,"copied-native",8);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);var actual=c.Snapshot.Points.Single(p=>p.Key=="P2");
            True(actual.IsProxy && actual.WorkingXUm==old.WorkingXUm && actual.OriginalXUm==old.OriginalXUm && actual.ReferenceKey==old.ReferenceKey);
            True(c.Snapshot.AllowManual && !c.Snapshot.CanApply && f.Executed.Count==0 && f.Saves==1);
        });
        await Check("file_open_cannot_replace_interrupted_native_measurement_without_cleanup", async () =>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));
            var count=f.Executed.Count;var file=Archive(f,"while-paused",6);f.FileSources[file.SourcePath]=file;await Throws(()=>OpenFile(c,file.SourcePath));
            True(c.Snapshot.HasIncomplete && c.Snapshot.CompletedCount==1 && c.Snapshot.RemainingCount==1 && !c.Snapshot.CanReturnToPreviousWorkspace && !c.Snapshot.IsReadOnly && f.Executed.Count==count && f.Saves==0);
            f.FailKey=null;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);True(!c.Snapshot.HasIncomplete && f.Executed.Last()=="P2");
        });
        await Check("multiple_file_loads_return_to_first_native_workspace", async () =>
        {
            var(f,c)=await Loaded(); var original=c.Snapshot.Points[0].WorkingXUm;
            var first=Archive(f,"first",7);var second=Archive(f,"second",13);f.FileSources[first.SourcePath]=first;f.FileSources[second.SourcePath]=second;
            await OpenFile(c,first.SourcePath);await OpenFile(c,second.SourcePath);Near(c.Snapshot.Points[0].WorkingXUm!.Value,13);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);
            True(c.Snapshot.SourcePath.Length==0 && c.Snapshot.Points[0].WorkingXUm==original && !c.Snapshot.CanReturnToPreviousWorkspace);
        });
        await Check("cancelled_file_picker_does_not_create_return_bookmark", async () =>
        {
            var(f,c)=await Loaded();c.ChooseFile=(_,_)=>null;var token=c.Snapshot.Points[0].MeasurementToken;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile);
            True(!c.Snapshot.CanReturnToPreviousWorkspace && c.Snapshot.Points[0].MeasurementToken==token && f.Loads==1 && f.Saves==0);
        });
        await Check("returned_workspace_invalidates_old_file_confirmation_before_movement", async () =>
        {
            var(f,c)=await Loaded();var file=Archive(f,"stale-confirmation",7);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            var stale=new CManualInspectionUiRequest{ExpectedVersion=c.Snapshot.Version,Keys=["P1"]};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);
            await Throws(()=>c.ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect,stale));True(f.Executed.Count==0&&f.Saves==0);
        });
        await Check("new_product_during_file_view_returns_without_promoting_old_raw", async () =>
        {
            var(f,c)=await Loaded();var file=Archive(f,"old-product",7);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            f.Source=WithRun(f.Source,"RUN-NEW");await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);
            True(c.Snapshot.RunId=="RUN-NEW" && c.Snapshot.Points.All(p=>p.MeasurementToken.StartsWith("RUN-NEW:",StringComparison.Ordinal)));
            True(c.Snapshot.CandidateRows.Count==0 && f.Executed.Count==0 && f.Saves==0);
        });
        await Check("file_apply_rebases_returned_base_and_invalidates_pre_file_candidate", async () =>
        {
            var(f,c)=await Loaded();await Prepared(c);var nativeRaw=c.Snapshot.Points[0].OriginalXUm;
            var file=Archive(f,"applied",2);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);
            True(c.Snapshot.ActiveRevisionId=="REV-1" && !c.Snapshot.CanApply && c.Snapshot.CandidateRows.Count==0 && !c.Snapshot.IsReadOnly);
            Near(c.Snapshot.Points[0].BaseXUm,2);True(c.Snapshot.Points[0].OriginalXUm==nativeRaw && f.Saves==1);
        });
        await Check("file_apply_does_not_make_old_native_hole_measurement_fresh_again", async () =>
        {
            var(f,c)=await Loaded();var file=Archive(f,"same-hole-other-token",2);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]));
            True(!c.Snapshot.CanApply && f.Saves==1);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(f.Saves==1);
        });
        await Check("file_checkpoints_cannot_replace_native_saved_session", async () =>
        {
            var(f,c)=await Loaded();await Prepared(c);var nativeSession=Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").ToDictionary(p=>p,p=>File.ReadAllText(p));
            var file=Archive(f,"checkpoint",9);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,null,r=>r with{LevelMetric="RADIAL",LevelBoundsUm=[1,2,3],LevelNames=["F1","F2","F3","F4"]});
            True(nativeSession.Count>0 && nativeSession.All(p=>File.ReadAllText(p.Key)==p.Value));
            var other=new FakeBackend(f.StateDirectory,false,false);var fresh=new CManualInspectionController(other);await Do(fresh,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(fresh.Snapshot.SourcePath.Length==0 && !fresh.Snapshot.CanApply && fresh.Snapshot.CandidateRows.Count==1 && fresh.Snapshot.Points[0].WorkingXUm==3 && other.Saves==0);
            await Do(fresh,EN_MANUAL_INSPECTION_UI_ACTION.Validate);True(fresh.Snapshot.CanApply);
        });
        await Check("reopening_file_cannot_erase_incomplete_file_measurement_and_bypass_correction_guard", async () =>
        {
            foreach(var sameFile in new[]{true,false})
            {
                var(f,c)=await Loaded();var file=Archive(f,"interrupted",2);var second=Archive(f,"other-after-interruption",4);f.FileSources[file.SourcePath]=file;f.FileSources[second.SourcePath]=second;
                await OpenFile(c,file.SourcePath);f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));
                var prior=c.Snapshot.SourcePath;var measured=c.Snapshot.Points[0].WorkingXUm;
                await Throws(()=>OpenFile(c,sameFile?file.SourcePath:second.SourcePath));
                True(c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1&&c.Snapshot.SourcePath==prior&&c.Snapshot.Points[0].WorkingXUm==measured&&f.Saves==0);
                await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]));
            }
        });
        await Check("failed_first_file_load_preserves_native_candidate_and_does_not_create_return_bookmark", async () =>
        {
            var(f,c)=await Loaded();await Prepared(c);var native=c.Snapshot;var file=Archive(f,"broken",2);f.FileSources[file.SourcePath]=file;
            f.LoadHook=(captured,_)=>captured.SourcePath==file.SourcePath?throw new IOException("Synthetic file read failure"):Task.CompletedTask;
            await Throws(()=>OpenFile(c,file.SourcePath));
            True(c.Snapshot.SourcePath.Length==0&&!c.Snapshot.CanReturnToPreviousWorkspace&&!c.Snapshot.IsReadOnly&&c.Snapshot.CanApply&&c.Snapshot.CandidateRows.SequenceEqual(native.CandidateRows));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);True(f.Saves==1);
        });
    }
    private static Task OpenFile(CManualInspectionController c,string path)=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,null,r=>r with{ResultPath=path});
    private static CManualInspectionSource Archive(FakeBackend f,string name,double dx)
    {
        var source=f.Source;var path=Path.Combine(f.StateDirectory,name+".csv");var at=DateTimeOffset.UtcNow.AddMinutes(1);
        var points=source.Points.Select(p=>
        {
            var measurement=new ST_MANUAL_MEASUREMENT("OK",dx,-1,"FILE-"+name+"-"+p.Key,source.Context.RecipeId,source.Context.RunId,source.Context.CoordinateSignature){RawData="Synthetic explicit-file raw",MeasuredAt=at};
            return p with{Source="IMPORTED",Judge="OK",ErrorXUm=dx,ErrorYUm=-1,MeasurementVersion=0,OriginalMeasurement=measurement,AdoptedFileMeasurement=measurement,ExternalObservedAt=at};
        }).ToArray();
        return source with{SourcePath=path,Description="Synthetic selected file "+name,ReadOnly=false,Points=points,
            Context=source.Context with{SourceResultPath=path},ImportedResults=points.ToDictionary(p=>p.Key,_=>new CManualInspectionImportedResult(path,at,"MANUAL FILE")),ObservationFingerprint="FILE-"+name};
    }
}
