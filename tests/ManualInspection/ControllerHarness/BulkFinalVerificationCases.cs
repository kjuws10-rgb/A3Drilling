using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunBulkFinalVerificationChecks()
    {
        await Check("bulk_final_verification_confirms_new_revision_values_and_status_after_actual_apply",async()=>
        {
            var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            True(f.Saves==1&&c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&c.Snapshot.LastSavedRevisionId=="REV-1"&&c.Snapshot.FinalOffsetRevisionId=="REV-1"&&c.Snapshot.StatusText.Contains("확인 완료"));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==1&&c.Snapshot.LastSavedRevisionId=="REV-1");
        });
        await Check("bulk_final_readback_error_does_not_abort_committed_values_or_reconsume_raw_and_can_recover",async()=>
        {
            var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();b.FailRead=true;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            True(f.Saves==1&&c.Snapshot.LastSaveVerificationCode=="READ_FAILED"&&c.Snapshot.StatusText.Contains("재확인 실패")&&!c.Snapshot.HasFinalOffsetSnapshot&&c.Snapshot.UndoOffsetCount==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==3);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==1);
            b.FailRead=false;await c.EnsureLoaded();True(c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&c.Snapshot.FinalOffsetPoints["P1"].XUm==3&&f.Saves==1);
        });
        await Check("bulk_final_followup_revision_is_reported_as_superseded_without_rolling_back_our_commit",async()=>
        {
            var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();b.UseCurrent=false;b.Saved=Saved(f.Source,"OTHER-LATEST",44);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&c.Snapshot.LastSaveVerificationCode=="SUPERSEDED"&&c.Snapshot.LastSavedRevisionId=="REV-1"
                &&c.Snapshot.FinalOffsetRevisionId=="OTHER-LATEST"&&c.Snapshot.FinalOffsetPoints["P1"].XUm==44&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==3&&c.Snapshot.StatusText.Contains("최신값 변경"));
        });
        await Check("bulk_final_same_revision_wrong_values_is_not_claimed_verified",async()=>
        {
            var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();b.UseCurrent=false;b.Saved=Saved(f.Source,"REV-1",44);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&c.Snapshot.LastSaveVerificationCode=="MISMATCH"&&c.Snapshot.StatusText.Contains("내용 불일치")&&c.Snapshot.UndoOffsetCount==1);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==1);
        });
        await Check("offset_init_final_verification_checks_initialized_state_not_only_numeric_zero",async()=>
        {
            var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();
            b.UseCurrent=false;b.Saved=Saved(f.Source,"REV-1",0);await StageLegacyInitDraft(c);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(f.Saves==1&&c.Snapshot.Points.All(p=>p.IsMeasurementOffsetInitialized)&&c.Snapshot.LastSaveVerificationCode=="MISMATCH"&&c.Snapshot.LastSavedRevisionId=="REV-1");
        });
        await Check("offset_undo_redo_refresh_their_own_verified_revision_without_extra_measurement",async()=>
        {
            var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets);True(c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&c.Snapshot.LastSavedRevisionId=="REV-2");
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets);True(c.Snapshot.LastSaveVerificationCode=="VERIFIED"&&c.Snapshot.LastSavedRevisionId=="REV-3"&&f.Saves==3&&f.Executed.Count==0);
        });
        await Check("generic_saved_read_unsupported_and_force_only_never_pretend_a_new_verified_csv",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(c.Snapshot.LastSaveVerificationCode=="UNSUPPORTED"&&f.Saves==1);var revision=c.Snapshot.LastSavedRevisionId;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{Reason="same consumed observation cannot create another application"}));True(c.Snapshot.LastSavedRevisionId==revision&&c.Snapshot.LastSaveVerificationCode=="UNSUPPORTED"&&f.Saves==1);
            f.FailSave=true;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=1,ForcedYUm=0,Reason="new unsaved operator value"});
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(c.Snapshot.LastSavedRevisionId==revision&&f.Saves==1);
        });
        await Check("stale_current_reconnect_refreshes_latest_final_even_when_the_old_request_is_rejected",async()=>
        {
            var(f,_)=Fixture();var b=new FinalBackend(f){UseCurrent=true};var c=new CManualInspectionController(b);await c.EnsureLoaded();
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=2,ForcedYUm=0,Reason="old source"});
            f.Source=f.Source with{Context=f.Source.Context with{RevisionId="EXTERNAL-1"},Values=f.Source.Values.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(22,0))};f.Stale=true;
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==0&&c.Snapshot.ActiveRevisionId=="EXTERNAL-1"&&c.Snapshot.FinalOffsetRevisionId=="EXTERNAL-1"
                &&c.Snapshot.FinalOffsetPoints["P1"].XUm==22&&c.Snapshot.LastSaveVerificationCode=="NONE");
        });
    }
}
