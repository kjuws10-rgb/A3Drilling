using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;
using System.IO;

internal static partial class Program
{
    private static async Task RunFinalOffsetSyncChecks()
    {
        await Check("final_saved_snapshot_is_separate_from_residual_and_engine_baseline",async()=>
        {
            var(f,backend,c)=await LoadedFinal();var raw=c.Snapshot.Points[0].WorkingXUm;
            backend.Saved=Saved(f.Source,"EXTERNAL-FINE",12);
            await c.NotifyExternalReviewChangedAsync();
            True(c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.FinalOffsetFileName=="EXTERNAL-FINE.csv");
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,12);True(c.Snapshot.Points[0].WorkingXUm==raw&&f.Saves==0&&f.Executed.Count==0);
        });
        await Check("final_saved_refresh_preserves_unapplied_candidate_and_native_raw",async()=>
        {
            var(f,b,c)=await LoadedFinal();await Prepared(c);var before=c.Snapshot.CandidateRows.ToArray();var raw=c.Snapshot.Points.ToArray();var loads=f.Loads;
            b.Saved=Saved(f.Source,"ROUGH-AFTER-DRAFT",18);await c.NotifyExternalReviewChangedAsync();
            True(c.Snapshot.SyncPending&&c.Snapshot.CandidateRows.SequenceEqual(before)&&c.Snapshot.Points.SequenceEqual(raw)&&f.Loads==loads);
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,18);True(c.Snapshot.ActiveRevisionId=="INITIAL"&&f.Saves==0);
        });
        await Check("final_saved_refresh_during_offset_init_preserves_pending_replacement",async()=>
        {
            var(f,b,c)=await LoadedFinal();await StageLegacyInitDraft(c);var before=c.Snapshot.CandidateRows.ToArray();
            b.Saved=Saved(f.Source,"SIMPLE-WHILE-INIT",9);await c.NotifyExternalReviewChangedAsync();
            True(c.Snapshot.IsOffsetEditPending&&c.Snapshot.OffsetInitPending&&c.Snapshot.CandidateRows.SequenceEqual(before));
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,9);True(f.Saves==0&&f.Executed.Count==0);
        });
        await Check("final_saved_refresh_preserves_explicit_file_workspace_and_its_candidate",async()=>
        {
            var(f,b,c)=await LoadedFinal();f.FileSources["selected-archive.csv"]=f.Source with { SourcePath="selected-archive.csv" };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,null,r=>r with { ResultPath="selected-archive.csv" });
            await Prepared(c);var candidate=c.Snapshot.CandidateRows.ToArray();var points=c.Snapshot.Points.ToArray();var loads=f.Loads;
            b.Saved=Saved(f.Source,"LATEST-WHILE-ARCHIVE",21);await c.NotifyExternalReviewChangedAsync();
            True(c.Snapshot.SourcePath=="selected-archive.csv");
            True(c.Snapshot.CanReturnToPreviousWorkspace&&c.Snapshot.CandidateRows.SequenceEqual(candidate)&&c.Snapshot.Points.SequenceEqual(points)&&f.Loads==loads);
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,21);True(f.Saves==0);
        });
        await Check("final_saved_refresh_during_incomplete_measurement_preserves_remaining",async()=>
        {
            var(f,b,c)=await LoadedFinal();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var loads=f.Loads;
            b.Saved=Saved(f.Source,"FINAL-WITH-REMAINING",7);await c.NotifyExternalReviewChangedAsync();
            True(c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1&&c.Snapshot.CompletedCount==1&&f.Loads==loads);
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,7);True(f.Saves==0);
        });
        await Check("final_saved_refresh_during_busy_does_not_reenter_live_adapter",async()=>
        {
            var(f,b,c)=await LoadedFinal();f.Hold=true;var run=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));var loads=f.Loads;b.Saved=Saved(f.Source,"BUSY-FINAL",8);
            await c.NotifyExternalReviewChangedAsync();True(c.IsBusy&&f.Loads==loads&&c.Snapshot.HasFinalOffsetSnapshot);Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,8);
            c.RequestStop();await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Release.TrySetResult();await run;
            True(c.Snapshot.HasIncomplete&&f.Saves==0);
        });
        await Check("final_saved_empty_init_and_real_zero_remain_distinct",async()=>
        {
            var(f,b,c)=await LoadedFinal();b.Saved=Saved(f.Source,"ZERO-STATE",0) with
            { RowCount=2,InitializedRowCount=1,InitializedOffsetKeys=new HashSet<string>([f.Source.Points[1].OffsetKey,f.Source.Points[2].OffsetKey]),AppliedPointKeys=new HashSet<string>(["P1","P3"]) };
            await c.NotifyExternalReviewChangedAsync();True(c.Snapshot.FinalOffsetPoints.Values.All(value=>value.XUm==0));
            True(c.Snapshot.FinalInitializedPointKeys.SetEquals(["P2","P3"])&&c.Snapshot.FinalAppliedPointKeys.SetEquals(["P1","P3"]));
            True(c.Snapshot.FinalOffsetRowCount==2&&c.Snapshot.FinalOffsetInitializedRowCount==1&&f.Saves==0);
        });
        await Check("final_saved_model_frame_mismatch_clears_old_saved_projection",async()=>
        {
            var(f,b,c)=await LoadedFinal();True(c.Snapshot.HasFinalOffsetSnapshot);
            b.Saved=Saved(f.Source,"WRONG",13) with { RecipeId="OTHER" };await c.NotifyExternalReviewChangedAsync();
            True(!c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.FinalOffsetPoints.Count==0&&c.Snapshot.FinalOffsetFilePath==""
                &&c.Snapshot.FinalOffsetStatusText.Contains("실패")&&f.Saves==0);
        });
        await Check("final_saved_nonfinite_or_missing_display_key_is_not_published",async()=>
        {
            var(f,b,c)=await LoadedFinal();b.Saved=Saved(f.Source,"INVALID",1) with { ValuesByPointKey=new Dictionary<string,ST_MANUAL_OFFSET>{["P1"]=new(double.NaN,0)} };
            await c.NotifyExternalReviewChangedAsync();True(!c.Snapshot.HasFinalOffsetSnapshot&&f.Saves==0);
        });
        await Check("final_saved_same_path_changed_sha_replaces_display_without_raw_rewrite",async()=>
        {
            var(f,b,c)=await LoadedFinal();var raw=c.Snapshot.Points[0].OriginalXUm;var old=c.Snapshot.FinalOffsetFilePath;
            b.Saved=b.Saved! with { ContentSha256="DIFFERENT",RevisionId="REPLACED-IN-PLACE",ValuesByPointKey=f.Source.Points.ToDictionary(point=>point.Key,_=>new ST_MANUAL_OFFSET(19,-2)) };
            await c.NotifyExternalReviewChangedAsync();True(c.Snapshot.FinalOffsetFilePath==old&&c.Snapshot.FinalOffsetContentSha256=="DIFFERENT"&&c.Snapshot.Points[0].OriginalXUm==raw);
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,19);
        });
        await Check("final_saved_successful_apply_and_undo_immediately_update_saved_metadata",async()=>
        {
            var(f,b,c)=await LoadedFinal();b.UseCurrent=true;await Prepared(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(c.Snapshot.FinalOffsetRevisionId=="REV-1");Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,3);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(c.Snapshot.FinalOffsetRevisionId=="REV-2");Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);True(c.Snapshot.FinalOffsetRevisionId=="REV-3");Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,3);True(f.Saves==3);
        });
        await Check("final_export_ignores_pending_draft_and_keeps_candidate_unchanged",async()=>
        {
            var(f,b,c)=await LoadedFinal();await StageLegacyInitDraft(c);var before=c.Snapshot.CandidateRows.ToArray();
            b.Saved=Saved(f.Source,"EXPORT-NEWEST",16);var path=Path.Combine(f.StateDirectory,"final.json");
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets,null,r=>r with { ResultPath=path });
            True(b.Exports==1&&b.ExportedPath==path&&c.Snapshot.IsOffsetEditPending&&c.Snapshot.CandidateRows.SequenceEqual(before)&&f.Saves==0);
            Near(b.Exported!.ValuesByPointKey["P1"].XUm,16);True(c.Snapshot.FinalOffsetRevisionId=="EXPORT-NEWEST");
        });
        await Check("final_snapshot_failed_read_does_not_discard_pending_work",async()=>
        {
            var(f,b,c)=await LoadedFinal();await Prepared(c);var before=c.Snapshot.CandidateRows.ToArray();b.FailRead=true;
            await c.NotifyExternalReviewChangedAsync();True(!c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.CandidateRows.SequenceEqual(before)&&c.Snapshot.SyncPending&&f.Saves==0);
        });
        await Check("final_snapshot_read_is_single_flight_and_does_not_starve_on_repeated_notifications",async()=>
        {
            var(f,b,c)=await LoadedFinal();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.ReadHook=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};var reads=b.Reads;
            var first=c.NotifyExternalReviewChangedAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second=c.NotifyExternalReviewChangedAsync();await Task.Delay(20);True(b.Reads==reads+1);
            release.TrySetResult();b.ReadHook=null;await Task.WhenAll(first,second);True(c.Snapshot.HasFinalOffsetSnapshot&&f.Saves==0);
        });
        await Check("final_no_file_snapshot_has_empty_metadata_not_a_saved_zero_file",async()=>
        {
            var(f,b,c)=await LoadedFinal();b.Saved=Saved(f.Source,"INITIAL_ZERO",0) with { FilePath="",FileName="",SavedAt=null,ContentSha256="",RowCount=0,InitializedRowCount=0,
                AppliedPointKeys=new HashSet<string>(),InitializedOffsetKeys=f.Source.Points.Select(point=>point.OffsetKey).ToHashSet() };
            await c.NotifyExternalReviewChangedAsync();True(c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.FinalOffsetFilePath==""&&c.Snapshot.FinalOffsetSavedAt is null
                &&c.Snapshot.FinalAppliedPointKeys.Count==0&&c.Snapshot.FinalInitializedPointKeys.Count==3&&c.Snapshot.FinalOffsetStatusText.Contains("파일 없음"));
        });
        await Check("final_own_save_waits_for_old_inflight_read_then_reloads_committed_revision",async()=>
        {
            var(f,b,c)=await LoadedFinal();b.UseCurrent=true;b.CaptureBeforeRead=true;await Prepared(c);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.ReadHook=async token=>{b.ReadHook=null;entered.TrySetResult();await release.Task.WaitAsync(token);};
            var notification=c.NotifyExternalReviewChangedAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var apply=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);await Until(()=>f.Saves==1);release.TrySetResult();await Task.WhenAll(notification,apply);
            True(c.Snapshot.FinalOffsetRevisionId=="REV-1"&&c.Snapshot.ActiveRevisionId=="REV-1"&&f.Saves==1);Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,3);
        });
        await Check("final_delayed_old_frame_read_cannot_replace_new_model_frame_preview",async()=>
        {
            var(f,b,c)=await LoadedFinal();b.UseCurrent=true;b.CaptureBeforeRead=true;await Prepared(c);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.ReadHook=async token=>{b.ReadHook=null;entered.TrySetResult();await release.Task.WaitAsync(token);};
            var notification=c.NotifyExternalReviewChangedAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await c.NotifyExternalReviewChangedAsync(); // Queue an old-frame notification before the Run changes.
            f.Source=f.Source with { Context=f.Source.Context with { RunId="NEXT-RUN" },Values=f.Source.Points.ToDictionary(point=>point.Key,_=>new ST_MANUAL_OFFSET(17,0)) };
            var load=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);release.TrySetResult();await Task.WhenAll(notification,load);
            True(c.Snapshot.RunId=="NEXT-RUN"&&c.Snapshot.HasFinalOffsetSnapshot&&f.Saves==0);Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,17);
        });
        await Check("final_saved_snapshot_copies_read_values_before_publishing",async()=>
        {
            var(f,b,c)=await LoadedFinal();var values=f.Source.Points.ToDictionary(point=>point.Key,_=>new ST_MANUAL_OFFSET(4,0));
            b.Saved=Saved(f.Source,"IMMUTABLE",4) with { ValuesByPointKey=values };await c.NotifyExternalReviewChangedAsync();values["P1"]=new(999,999);
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm,4);True(f.Saves==0);
        });
        await Check("final_coalesced_notification_retries_failed_old_read_in_explicit_file_workspace",async()=>
        {
            var(f,b,c)=await LoadedFinal();f.FileSources["archive.csv"]=f.Source with { SourcePath="archive.csv" };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,null,r=>r with { ResultPath="archive.csv" });await Prepared(c);
            var candidate=c.Snapshot.CandidateRows.ToArray();var loads=f.Loads;var reads=b.Reads;
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.ReadHook=async token=>{b.ReadHook=null;entered.TrySetResult();await release.Task.WaitAsync(token);throw new IOException("old revision changed before read completed");};
            var first=c.NotifyExternalReviewChangedAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            b.Saved=Saved(f.Source,"COALESCED-NEWEST",24);await c.NotifyExternalReviewChangedAsync();release.TrySetResult();await first;
            True(b.Reads==reads+2&&c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.FinalOffsetRevisionId=="COALESCED-NEWEST"
                &&c.Snapshot.SourcePath=="archive.csv"&&c.Snapshot.CandidateRows.SequenceEqual(candidate)&&f.Loads==loads&&f.Saves==0);
        });
        await Check("final_continuous_notifications_yield_after_one_coalesced_read",async()=>
        {
            var(f,b,c)=await LoadedFinal();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,null,r=>r with { ResultPath="archive.csv" });
            var rounds=0;var reads=b.Reads;b.ReadHook=async _=>{rounds++;if(rounds==3)b.ReadHook=null;await c.NotifyExternalReviewChangedAsync();};
            await c.NotifyExternalReviewChangedAsync();True(rounds==2&&b.Reads==reads+2);
            await Until(()=>b.Reads>=reads+4);True(b.Reads==reads+4&&c.Snapshot.HasFinalOffsetSnapshot&&f.Saves==0&&f.Executed.Count==0);
        });
        await Check("final_cancelled_read_clears_coalesced_pending_without_unrequested_retry",async()=>
        {
            var(f,b,c)=await LoadedFinal();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile,null,r=>r with { ResultPath="archive.csv" });
            using var cancel=new CancellationTokenSource();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.ReadHook=async token=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);};var reads=b.Reads;
            var first=c.NotifyExternalReviewChangedAsync(cancel.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await c.NotifyExternalReviewChangedAsync();
            cancel.Cancel();await Throws(()=>first);await Task.Delay(30);True(b.Reads==reads+1&&!c.Snapshot.HasFinalOffsetSnapshot&&f.Saves==0);
            b.ReadHook=null;await c.NotifyExternalReviewChangedAsync();True(c.Snapshot.HasFinalOffsetSnapshot&&b.Reads==reads+2);
        });
    }

    private static CManualInspectionSavedOffsetSnapshot Saved(CManualInspectionSource source,string revision,double value)
        => new(source.Context.RecipeId,source.Context.RunId,source.Context.CoordinateSignature,revision,
            revision+".csv",revision+".csv",DateTimeOffset.UtcNow,"SHA-"+revision,source.Points.Count,0,
            source.Points.ToDictionary(point=>point.Key,_=>new ST_MANUAL_OFFSET(value,-value)),new HashSet<string>(StringComparer.Ordinal),source.Points.Select(point=>point.Key).ToHashSet(StringComparer.Ordinal));

    private static async Task<(FakeBackend Fake,FinalBackend Backend,CManualInspectionController Controller)> LoadedFinal()
    {
        var(f,_)=Fixture();var backend=new FinalBackend(f){Saved=Saved(f.Source,"INITIAL",0)};var controller=new CManualInspectionController(backend);
        await Do(controller,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);return(f,backend,controller);
    }

    private sealed class FinalBackend(FakeBackend inner):IManualInspectionBackend
    {
        public CManualInspectionSavedOffsetSnapshot? Saved,Exported;
        public bool FailRead,UseCurrent,CaptureBeforeRead;public int Reads,Exports;public string? ExportedPath;
        public Func<CancellationToken,Task>? ReadHook;
        public bool IsSimulation=>inner.IsSimulation;public string StateDirectory=>inner.StateDirectory;
        public IReadOnlyList<KeyValuePair<string,string>> ConsumedMeasurements=>inner.ConsumedMeasurements;
        public Task<CManualInspectionSource> LoadAsync(string? path,CancellationToken token,IReadOnlyList<string>? keys=null)=>inner.LoadAsync(path,token,keys);
        public Task EnsureCurrentAsync(CManualInspectionSource source,CancellationToken token)=>inner.EnsureCurrentAsync(source,token);
        public void GuardOperation()=>inner.GuardOperation();
        public Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource source,string key,EN_MANUAL_OPERATION_KIND kind,Action<string> progress,CancellationToken token)
            =>inner.ExecutePointAsync(source,key,kind,progress,token);
        public Task ExecuteFlyingAsync(CManualInspectionSource source,IReadOnlyList<string> keys,Action<CManualInspectionMeasurement> measured,Action<string> progress,CancellationToken token)
            =>inner.ExecuteFlyingAsync(source,keys,measured,progress,token);
        public Task<string> SaveAsync(CManualInspectionSource source,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,CancellationToken token,IReadOnlyDictionary<string,string>? consumed=null)
            =>inner.SaveAsync(source,values,identity,rows,token,consumed);
        public Task<string> SaveCumulativeAsync(CManualInspectionSource source,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,bool initialize,CancellationToken token,IReadOnlyDictionary<string,string>? consumed=null)
            =>inner.SaveCumulativeAsync(source,values,identity,rows,initialize,token,consumed);
        public async Task<CManualInspectionSavedOffsetSnapshot?> ReadSavedOffsetSnapshotAsync(CManualInspectionSource source,CancellationToken token)
        {
            CManualInspectionSavedOffsetSnapshot? Current()=>UseCurrent?Program.Saved(inner.Source,inner.Source.Context.RevisionId,0) with
                { ValuesByPointKey=inner.Source.Values,InitializedOffsetKeys=inner.Source.StoredInitializedOffsetKeys??new HashSet<string>() }:Saved;
            var captured=CaptureBeforeRead?Current():null;
            Reads++;if(ReadHook is not null)await ReadHook(token);token.ThrowIfCancellationRequested();if(FailRead)throw new IOException("saved read failed");
            return CaptureBeforeRead?captured:Current();
        }
        public async Task<CManualInspectionSavedOffsetSnapshot?> ExportFinalOffsetBundleAsync(CManualInspectionSource source,string path,CancellationToken token)
        { var snapshot=await ReadSavedOffsetSnapshotAsync(source,token);if(snapshot?.RecipeId!=source.Context.RecipeId)throw new InvalidOperationException("wrong model");Exports++;ExportedPath=path;return Exported=snapshot; }
        public void Stop()=>inner.Stop();
    }
}
