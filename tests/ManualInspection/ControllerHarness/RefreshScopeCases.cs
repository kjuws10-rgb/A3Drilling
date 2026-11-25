using System.IO;
using System.Reflection;
using System.Windows;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static long FileEpoch(CManualInspectionController controller)=>(long)typeof(CManualInspectionController).GetField("_fileChangeVersion",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!;
    private static async Task<(FakeBackend Fake,FinalBackend Backend,CManualInspectionController Controller)> RefreshFixture()
    {
        var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();return(f,b,c);
    }
    private static async Task RunRefreshScopeChecks()
    {
        await Check("refresh_scope_clean_tab_entry_checks_current_authority_without_catalog_or_final_reload",async()=>
        {
            var(f,b,c)=await RefreshFixture();var loads=f.Loads;var reads=b.Reads;var checks=f.Ensures;var raw=c.Snapshot.Points.ToArray();
            for(var i=0;i<3;i++)await c.EnsureLoaded();
            True(f.Loads==loads&&b.Reads==reads&&f.Ensures==checks+3&&c.Snapshot.Points.SequenceEqual(raw)&&f.Executed.Count==0&&f.Saves==0&&!c.IsBusy);
        });
        await Check("refresh_scope_clean_ensureloaded_from_worker_keeps_selection_checks_and_changed_observers_on_owner",async()=>
        {
            var(f,b,c)=await RefreshFixture();var wrong=false;var calls=0;var loads=f.Loads;var reads=b.Reads;
            f.EnsureHook=()=>{calls++;wrong|=!Application.Current.Dispatcher.CheckAccess();};
            c.Changed+=(_,_)=>wrong|=!Application.Current.Dispatcher.CheckAccess();
            await Task.Run(()=>c.EnsureLoaded());
            True(!wrong&&calls==1&&f.Loads==loads&&b.Reads==reads&&!c.IsBusy&&f.Executed.Count==0&&f.Saves==0);
        });
        await Check("refresh_scope_actual_idle_csv_watcher_marks_dirty_without_reads_then_tab_entry_merges_once",async()=>
        {
            var(f,b,c)=await RefreshFixture();var directory=Path.Combine(f.StateDirectory,"Watch");Directory.CreateDirectory(directory);f.WatchRoots=[directory];
            var loads=f.Loads;var reads=b.Reads;var epoch=FileEpoch(c);var raw=c.Snapshot.Points[0].OriginalXUm;c.EnableAutomaticUpdates();
            try
            {
                SetIncoming(f,"watched-external",7,DateTimeOffset.UtcNow.AddMinutes(2));
                var path=Path.Combine(directory,"external.csv");for(var n=0;n<10;n++)await File.WriteAllTextAsync(path,"synthetic notification "+n);
                await Until(()=>FileEpoch(c)>epoch);
                True(f.Loads==loads&&b.Reads==reads&&c.Snapshot.Points[0].OriginalXUm==raw&&!c.IsBusy&&File.Exists(path));
                await c.EnsureLoaded();True(f.Loads==loads+1&&b.Reads>reads&&c.Snapshot.Points[0].WorkingXUm==7&&c.Snapshot.Points[0].Source=="IMPORTED");
                loads=f.Loads;reads=b.Reads;await c.EnsureLoaded();True(f.Loads==loads&&b.Reads==reads&&f.Saves==0&&f.Executed.Count==0);
            }
            finally {c.DisableAutomaticUpdates();}
        });
        await Check("refresh_scope_actual_busy_csv_watcher_does_not_delay_successful_measurement_with_catalog_or_final_read",async()=>
        {
            var(f,b,c)=await RefreshFixture();var directory=Path.Combine(f.StateDirectory,"Watch");Directory.CreateDirectory(directory);f.WatchRoots=[directory];
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.BeforePointResult=(_,token)=>release.Task.WaitAsync(token);f.NewX=2.5;f.NewY=-1.25;
            var loads=f.Loads;var reads=b.Reads;var epoch=FileEpoch(c);var phases=new List<string>();c.Changed+=(_,_)=>phases.Add(c.Snapshot.ProgressStage);c.EnableAutomaticUpdates();
            var measuring=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1"]);
            try
            {
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));SetIncoming(f,"watched-while-measuring",9,DateTimeOffset.UtcNow.AddMinutes(2));
                await File.WriteAllTextAsync(Path.Combine(directory,"measurement.csv"),"synthetic source notification");await Until(()=>FileEpoch(c)>epoch);
                True(c.IsBusy&&f.Loads==loads&&b.Reads==reads);release.TrySetResult();await measuring;
                True(!c.IsBusy&&f.Loads==loads&&b.Reads==reads&&c.Snapshot.Points[0].WorkingXUm==2.5&&c.Snapshot.Points[0].OriginalXUm==2.5&&!phases.Contains("REFRESHING")&&f.Saves==0);
                await c.EnsureLoaded();True(f.Loads==loads+1&&c.Snapshot.Points[0].WorkingXUm==9);
            }
            finally {release.TrySetResult();c.DisableAutomaticUpdates();await measuring;}
        });
        await Check("refresh_scope_dirty_file_notifications_do_not_attach_full_sync_to_measure_move_force_copy_or_level_changes",async()=>
        {
            var(f,b,c)=await RefreshFixture();var loads=f.Loads;var reads=b.Reads;var ensures=f.Ensures;var phases=new List<string>();c.Changed+=(_,_)=>phases.Add(c.Snapshot.ProgressStage);
            f.PointHook=_=>c.NotifyFilesChanged();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1"]);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P3"]);
            c.NotifyFilesChanged();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=4,ForcedYUm=-1,Reason="explicit residual input"});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="working copy only"});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,[],r=>r with{LevelMetric="MAX_AXIS",LevelBoundsUm=[3,6,12],LevelNames=["A","B","C","D"]});
            True(f.Loads==loads&&b.Reads==reads&&f.Ensures>=ensures+4&&f.Saves==0&&f.Executed.SequenceEqual(["P1","P3"])&&!phases.Contains("REFRESHING"));
            True(c.Snapshot.Points.Single(p=>p.Key=="P2").WorkingXUm==4&&c.Snapshot.Points.Single(p=>p.Key=="P2").OriginalXUm==1);
        });
        await Check("refresh_scope_busy_explicit_review_notification_defers_both_reads_until_later_explicit_sync",async()=>
        {
            var(f,b,c)=await RefreshFixture();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.BeforePointResult=(_,token)=>release.Task.WaitAsync(token);
            var loads=f.Loads;var reads=b.Reads;var measuring=Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1"]);
            try
            {
                await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));SetIncoming(f,"real-other-review",8,DateTimeOffset.UtcNow.AddMinutes(2));
                for(var n=0;n<4;n++)await c.NotifyExternalReviewChangedAsync();
                True(c.IsBusy&&c.Snapshot.SyncPending&&f.Loads==loads&&b.Reads==reads);release.TrySetResult();await measuring;
                True(!c.IsBusy&&c.Snapshot.SyncPending&&f.Loads==loads&&b.Reads==reads&&c.Snapshot.Points[0].OriginalXUm==f.NewX);
                await c.NotifyExternalReviewChangedAsync();True(f.Loads==loads+1&&b.Reads>reads&&!c.Snapshot.SyncPending&&c.Snapshot.Points[0].WorkingXUm==8&&f.Saves==0);
            }
            finally {release.TrySetResult();await measuring;}
        });
        await Check("refresh_scope_saved_bulk_init_undo_redo_still_read_final_snapshot_without_catalog_reload",async()=>
        {
            var(f,b,c)=await RefreshFixture();var loads=f.Loads;var reads=b.Reads;f.SaveHook=_=>{c.NotifyFilesChanged();return Task.CompletedTask;};
            foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets})
            {
                await Do(c,action);True(f.Loads==loads&&b.Reads>reads&&c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&c.Snapshot.FinalOffsetRevisionId==c.Snapshot.ActiveRevisionId);reads=b.Reads;
            }
            True(f.Saves==4&&f.Executed.Count==0&&c.Snapshot.Points.All(p=>p.BaseXUm==0&&p.IsMeasurementOffsetInitialized));
        });
        await Check("refresh_scope_failed_measurement_and_failed_write_do_not_start_queued_catalog_reload",async()=>
        {
            var(f,b,c)=await RefreshFixture();var loads=f.Loads;var reads=b.Reads;c.NotifyFilesChanged();f.FailKey="P1";
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Measure,["P1"]));True(f.Loads==loads&&b.Reads==reads&&c.Snapshot.HasIncomplete&&!c.IsBusy&&f.Saves==0);
            var(g,b2,c2)=await RefreshFixture();loads=g.Loads;reads=b2.Reads;g.FailSave=true;g.SaveHook=_=>{c2.NotifyFilesChanged();return Task.CompletedTask;};var raw=c2.Snapshot.Points.Select(p=>p.OriginalXUm).ToArray();
            await Throws(()=>Do(c2,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));
            True(g.Loads==loads&&b2.Reads==reads&&g.Saves==0&&c2.Snapshot.Points.All(p=>p.BaseXUm==0)&&raw.SequenceEqual(c2.Snapshot.Points.Select(p=>p.OriginalXUm)));
            g.FailSave=false;await Do(c2,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(g.Saves==1&&g.Loads==loads&&b2.Reads>reads);
        });
        await Check("refresh_scope_file_epoch_arriving_during_load_is_retained_for_next_tab_entry_not_lost_or_drained",async()=>
        {
            var(f,b,c)=await RefreshFixture();SetIncoming(f,"first-observed",7,DateTimeOffset.UtcNow.AddMinutes(2));c.NotifyFilesChanged();var loads=f.Loads;
            f.LoadHook=(_,_)=>{f.LoadHook=null;SetIncoming(f,"during-load-newer",9,DateTimeOffset.UtcNow.AddMinutes(3));c.NotifyFilesChanged();return Task.CompletedTask;};
            await c.EnsureLoaded();True(f.Loads==loads+1&&c.Snapshot.Points[0].WorkingXUm==7);
            await c.EnsureLoaded();True(f.Loads==loads+2&&c.Snapshot.Points[0].WorkingXUm==9);
            var reads=b.Reads;await c.EnsureLoaded();True(f.Loads==loads+2&&b.Reads==reads&&f.Saves==0&&f.Executed.Count==0);
        });
        await Check("refresh_scope_dirty_notification_does_not_bypass_required_current_authority_or_stale_action_guard",async()=>
        {
            var(f,b,c)=await RefreshFixture();var loads=f.Loads;var ensures=f.Ensures;c.NotifyFilesChanged();f.Stale=true;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]));
            True(f.Ensures>ensures&&f.Executed.Count==0&&f.Saves==0);
            f.Stale=false;loads=f.Loads;var reads=b.Reads;await c.EnsureLoaded();True(f.Ensures>ensures&&!c.IsBusy);
            ensures=f.Ensures;await c.EnsureLoaded();True(f.Ensures==ensures+1&&f.Loads>=loads&&f.Executed.Count==0&&f.Saves==0);
        });
    }
}
