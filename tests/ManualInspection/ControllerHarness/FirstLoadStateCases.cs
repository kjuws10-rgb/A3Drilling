using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunFirstLoadStateChecks()
    {
        static void Empty(FakeBackend f)=>f.Source=f.Source with{
            Points=f.Source.Points.Select(p=>p with{Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null,IsMeasurementOffsetInitialized=true,MeasurementVersion=0}).ToArray(),
            StoredInitializedOffsetKeys=f.Source.Points.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal)};
        static Task Copy(CManualInspectionController c,string source="P1",string target="P2")=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,[target],r=>r with{ReferenceKey=source,Reason="copy captured absence"});
        static string Checkpoint(FakeBackend f)=>Directory.GetFiles(Path.Combine(f.StateDirectory,"Sessions"),"*.json").Single();
        await Check("first_load_empty_reference_copies_absent_state_without_save_motion_or_fake_zero",async()=>
        {
            var(f,c)=Fixture();Empty(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(c.Snapshot.Points.All(p=>p.CanCopyReferenceState&&!p.CanCopyAsReference));
            await Copy(c);var p=c.Snapshot.Points.Single(p=>p.Key=="P2");
            True(p.IsMeasurementStateCleared&&p.WorkingJudge=="UNMEASURED"&&p.MeasurementXUm is null&&p.MeasurementYUm is null&&!p.HasMeasurementHistory&&p.AppliedLevel is null&&!c.Snapshot.CanBulkApply&&f.Saves==0&&f.Executed.Count==0);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P2"]));True(f.Saves==0);
        });
        await Check("first_load_blank_state_preserves_existing_nonzero_base_and_original_then_undo",async()=>
        {
            var(f,c)=Fixture();var blank=f.Source.Points[0] with{Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null};
            f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P1"?blank:p).ToArray(),Values=f.Source.Values.ToDictionary(p=>p.Key,p=>p.Key=="P2"?new ST_MANUAL_OFFSET(13,-8):p.Value),AppliedOffsetKeys=new HashSet<string>{"P2"}};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);var original=c.Snapshot.Points.Single(p=>p.Key=="P2").OriginalXUm;await Copy(c);
            var p=c.Snapshot.Points.Single(p=>p.Key=="P2");True(p.IsMeasurementStateCleared&&!p.IsMeasurementOffsetInitialized&&p.BaseXUm==13&&p.BaseYUm==-8&&p.OriginalXUm==original&&!p.UsesAppliedMeasurement&&p.MeasurementXUm is null);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy);True(!c.Snapshot.Points.Single(p=>p.Key=="P2").IsMeasurementStateCleared&&f.Saves==0);
        });
        await Check("first_load_state_copy_roundtrip_and_fresh_actual_zero_measurement",async()=>
        {
            var(f,c)=Fixture();Empty(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Copy(c);
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(reopened.Snapshot.Points.Single(p=>p.Key=="P2").IsMeasurementStateCleared);
            f.NewX=f.NewY=0;await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P2"]);var p=reopened.Snapshot.Points.Single(p=>p.Key=="P2");
            True(!p.IsMeasurementStateCleared&&p.WorkingJudge=="OK"&&p.WorkingXUm==0&&p.WorkingYUm==0&&p.HasWorkingMeasurement&&reopened.Snapshot.CanBulkApply&&f.Saves==0);
        });
        await Check("first_load_auto_incomplete_recovery_backs_up_exact_bytes_and_starts_idle",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var checkpoint=Checkpoint(f);var original=await File.ReadAllBytesAsync(checkpoint);
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var backups=Directory.GetFiles(Path.Combine(f.StateDirectory,"Recovery"),"incomplete-operation-*.json");
            True(backups.Length==1&&File.ReadAllBytes(backups[0]).SequenceEqual(original)&&!File.ReadAllBytes(checkpoint).SequenceEqual(original)&&!reopened.Snapshot.HasIncomplete&&!reopened.Snapshot.IsOffsetEditPending&&f.Executed.Count==2&&f.Saves==0);
            var p1=reopened.Snapshot.Points.Single(p=>p.Key=="P1");var p2=reopened.Snapshot.Points.Single(p=>p.Key=="P2");True(p1.WorkingXUm==f.NewX&&p2.WorkingJudge=="UNMEASURED"&&p2.OriginalXUm==1&&!p2.HasWorkingMeasurement);
            var json=await File.ReadAllTextAsync(checkpoint);True(json.Contains("OP_RECOVERED_IDLE")&&json.Contains("RemainingKeys"));
            f.FailKey=null;await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P3"]);True(f.Executed.Last()=="P3"&&!reopened.Snapshot.HasIncomplete);
        });
        await Check("first_load_auto_recovery_preserves_completed_consumption_and_offset_history",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);var b=c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm;
            f.FailKey="P3";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P2","P3"]));var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(!reopened.Snapshot.HasIncomplete&&reopened.Snapshot.UndoOffsetCount==1&&reopened.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==b&&reopened.Snapshot.Points.Single(p=>p.Key=="P2").WorkingXUm==f.NewX&&f.Saves==1);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]);True(f.Saves==2&&reopened.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==b);
        });
        await Check("first_load_explicit_session_and_same_workspace_reload_keep_incomplete_queue",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));
            var file=Path.Combine(f.StateDirectory,"explicit-incomplete.json");c.ChooseFile=(_,_)=>file;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);
            var(g,other)=Fixture();g.Source=f.Source;other.ChooseFile=(_,_)=>file;await Do(other,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);True(other.Snapshot.HasIncomplete&&g.Executed.Count==0);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe));True(c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1&&f.Executed.Count==2&&!Directory.Exists(Path.Combine(f.StateDirectory,"Recovery")));
        });
        await Check("first_load_recovery_backup_failure_never_overwrites_checkpoint_or_executes",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var checkpoint=Checkpoint(f);var original=await File.ReadAllBytesAsync(checkpoint);
            var blocker=Path.Combine(f.StateDirectory,"Recovery");await File.WriteAllTextAsync(blocker,"directory blocker");var reopened=new CManualInspectionController(f);
            for(var i=0;i<2;i++){await Throws(()=>Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe));True(File.ReadAllBytes(checkpoint).SequenceEqual(original)&&f.Executed.Count==2&&f.Saves==0);}
            File.Delete(blocker);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(!reopened.Snapshot.HasIncomplete&&Directory.GetFiles(blocker,"incomplete-operation-*.json").Length==1);
        });
        await Check("first_load_corrupt_operation_is_validated_before_archive_or_idle_release",async()=>
        {
            foreach(var bad in new[]{"missingVersion","foreignKey","foreignCoordinate"})
            {
                var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var checkpoint=Checkpoint(f);var node=JsonNode.Parse(await File.ReadAllTextAsync(checkpoint))!;
                var operation=node["Session"]!["Operation"]!;if(bad=="missingVersion")operation["MeasurementVersionsBefore"]=new JsonObject();else if(bad=="foreignKey")operation["RemainingKeys"]=new JsonArray("UNKNOWN");else operation["CoordinateSignature"]="FOREIGN";
                await File.WriteAllTextAsync(checkpoint,node.ToJsonString());var original=await File.ReadAllBytesAsync(checkpoint);var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
                True(File.ReadAllBytes(checkpoint).SequenceEqual(original)&&!Directory.Exists(Path.Combine(f.StateDirectory,"Recovery"))&&f.Executed.Count==2&&f.Saves==0);
            }
        });
        await Check("first_load_explicit_invalid_copy_or_init_does_not_discard_incomplete_queue",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));var checkpoint=Checkpoint(f);
            foreach(var request in new[]{new CManualInspectionUiRequest{ReferenceKey="MISSING",Keys=["P3"],Reason="bad"},new(){ReferenceKey="P1",Keys=["MISSING"],Reason="bad"},new(){ReferenceKey="P1",Keys=["P3"],Reason=" "}})
                {await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,request.Keys,r=>request with{ExpectedVersion=r.ExpectedVersion}));True(c.Snapshot.HasIncomplete&&c.Snapshot.RemainingCount==1);}
            await Throws(()=>StageLegacyInitDraft(c,null,r=>r with{CurrentCellNo=99}));True(c.Snapshot.HasIncomplete&&!c.Snapshot.IsOffsetEditPending&&!File.ReadAllText(checkpoint).Contains("OP_RECOVERED_IDLE"));
        });
        await Check("first_load_explicit_copy_preflights_post_retirement_reference_and_never_uses_old_raw",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));await Copy(c,"P2","P3");
            var p=c.Snapshot.Points.Single(p=>p.Key=="P3");True(!c.Snapshot.HasIncomplete&&p.IsMeasurementStateCleared&&p.WorkingJudge=="UNMEASURED"&&p.CandidateXUm is null&&p.OriginalXUm==1&&f.Saves==0&&f.Executed.Count==2);
        });
        await Check("first_load_explicit_init_can_leave_stopped_queue_but_does_not_save_or_change_base",async()=>
        {
            var(f,c)=await Loaded();f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1","P2"]));await StageLegacyInitDraft(c);
            True(!c.Snapshot.HasIncomplete&&c.Snapshot.IsOffsetEditPending&&f.Saves==0&&f.Executed.Count==2);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit);True(!c.Snapshot.IsOffsetEditPending&&!c.Snapshot.HasIncomplete);
        });
        await Check("first_load_foreign_model_does_not_import_captured_blank_state",async()=>
        {
            var(f,c)=Fixture();Empty(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Copy(c);var previous=c.Snapshot;
            f.Source=f.Source with{Context=f.Source.Context with{RecipeId="OTHER",RunId="OTHER-RUN",CoordinateSignature="OTHER-COORD"}};await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(c.Snapshot.RecipeId=="OTHER"&&c.Snapshot.Points.All(p=>!p.IsMeasurementStateCleared)&&f.Saves==0);
        });
        await Check("first_load_file_return_after_other_shot_save_keeps_captured_absence_and_new_base",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P1"?p with{Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null}:p).ToArray()};
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Copy(c);var old=c.Snapshot.Points.Single(p=>p.Key=="P2");
            var file=Archive(f,"blank-state-return",2);f.FileSources[file.SourcePath]=file;await OpenFile(c,file.SourcePath);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P3"]);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace);var actual=c.Snapshot.Points.Single(p=>p.Key=="P2");
            True(actual.IsMeasurementStateCleared&&actual.Source=="MANUAL_STATE"&&actual.WorkingXUm is null&&actual.OriginalXUm==old.OriginalXUm&&c.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm==2&&f.Saves==1);
            var reopened=new CManualInspectionController(f);await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(reopened.Snapshot.Points.Single(p=>p.Key=="P2").IsMeasurementStateCleared);
        });
        await Check("first_load_failed_cleared_point_stays_empty_after_explicit_copy_and_startup_recovery",async()=>
        {
            foreach(var reopen in new[]{false,true})
            {
                var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P1"?p with{Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null}:p).ToArray(),Values=f.Source.Values.ToDictionary(p=>p.Key,p=>p.Key=="P2"?new ST_MANUAL_OFFSET(13,-8):p.Value),AppliedOffsetKeys=new HashSet<string>{"P2"}};
                await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);await Copy(c);f.FailKey="P2";await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P2"]));
                if(reopen){c=new(f);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);}await Copy(c,"P2","P3");var p2=c.Snapshot.Points.Single(p=>p.Key=="P2");var p3=c.Snapshot.Points.Single(p=>p.Key=="P3");
                True(!c.Snapshot.HasIncomplete&&p2.IsMeasurementStateCleared&&!p2.UsesAppliedMeasurement&&p2.BaseXUm==13&&p2.MeasurementXUm is null&&p3.IsMeasurementStateCleared&&p3.WorkingXUm is null&&p3.CandidateXUm is null&&f.Saves==0&&f.Executed.Count==1);
            }
        });
    }
}
