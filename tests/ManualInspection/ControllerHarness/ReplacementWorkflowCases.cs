using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunReplacementWorkflowChecks()
    {
        static void TwoCells(FakeBackend f)
        {f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P3"?p with{CellNo=2}:p).ToArray(),Values=new Dictionary<string,ST_MANUAL_OFFSET>{{"P1",new(20,5)},{"P2",new(10,-2)},{"P3",new(222,-333)}},AppliedOffsetKeys=f.Source.Points.Select(p=>p.Key).ToHashSet()};}
        await Check("manual_copy_only_stages_current_error_and_current_cell_bulk_replaces_base",async()=>
        {
            var(f,c)=Fixture();TwoCells(f);await c.EnsureLoaded();var original=c.Snapshot.Points.Single(p=>p.Key=="P2").OriginalXUm;
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="staged XY replacement"});
            var p=c.Snapshot.Points.Single(p=>p.Key=="P2");True(f.Saves==0&&p.WorkingXUm==original&&p.ReferenceFinalXUm==3&&p.BaseXUm==10&&p.OriginalXUm==original&&c.Snapshot.CandidateRows.Count==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&c.Snapshot.Points.Single(p=>p.Key=="P1").BaseXUm==3&&c.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==3&&c.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm==222);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==1);
        });
        await Check("manual_current_cell_csv_stage_displays_file_values_without_fabricating_raw_then_bulk_saves_only_scope",async()=>
        {
            var(f,c)=Fixture();TwoCells(f);await c.EnsureLoaded();var outside=c.Snapshot.Points.Single(p=>p.Key=="P3");
            f.ImportedBundleValues=f.Source.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(7,-1));await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle,[],r=>r with{ResultPath="synthetic.csv"});
            var p=c.Snapshot.Points.Single(p=>p.Key=="P1");True(c.Snapshot.PendingOffsetCellNo==1&&p.Source=="OFFSET_FILE"&&p.WorkingXUm==3&&p.ReferenceFinalXUm==7&&p.OriginalXUm==3&&f.Saves==0&&c.Snapshot.CanBulkApply);
            True(c.Snapshot.Points.Single(p=>p.Key=="P3").WorkingXUm==outside.WorkingXUm&&c.Snapshot.Points.Single(p=>p.Key=="P3").BaseXUm==222);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=2}));True(f.Saves==0&&c.Snapshot.IsOffsetEditPending);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.CumulativeInitializations.SequenceEqual([false])&&f.Source.Values["P1"].XUm==7&&f.Source.Values["P3"].XUm==222&&!c.Snapshot.IsOffsetEditPending);
        });
        await Check("manual_current_cell_init_is_gray_draft_saved_by_the_common_bulk_only",async()=>
        {
            var(f,c)=Fixture();TwoCells(f);await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets);
            True(c.Snapshot.PendingOffsetCellNo==1&&c.Snapshot.CellBulkEligibleCounts[1]==2&&c.Snapshot.Points.Where(p=>p.CellNo==1).All(p=>p.MeasurementXUm is null)&&f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.CumulativeInitializations.Single()==false&&c.Snapshot.Points.Where(p=>p.CellNo==1).All(p=>p.IsMeasurementOffsetInitialized)&&c.Snapshot.Points.Single(p=>p.CellNo==2).BaseXUm==222);
        });
        await Check("manual_legacy_save_endpoints_require_explicit_cell_and_forward_to_whole_current_cell",async()=>
        {
            foreach(var action in new[]{EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,EN_MANUAL_INSPECTION_UI_ACTION.Apply})
            {
                var(f,c)=Fixture();TwoCells(f);await c.EnsureLoaded();if(action==EN_MANUAL_INSPECTION_UI_ACTION.Apply)await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]);
                await Throws(()=>Do(c,action,["P3"],r=>r with{CurrentCellNo=null}));True(f.Saves==0);
                await Do(c,action,["P3"],r=>r with{CurrentCellNo=1});True(f.Saves==1&&f.Source.Values["P1"].XUm==3&&f.Source.Values["P2"].XUm==1&&f.Source.Values["P3"].XUm==222);
            }
        });
        await Check("manual_old_accumulation_candidate_cannot_use_the_legacy_apply_endpoint",async()=>
        {
            var(f,c)=Fixture();TwoCells(f);await c.EnsureLoaded();var engine=(CManualInspectionWorkspace)typeof(CManualInspectionController).GetField("_engine",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(c)!;
            engine.BuildDirectCandidate(["P1"]);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Apply));True(f.Saves==0&&f.Source.Values["P1"].XUm==20);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.Source.Values["P1"].XUm==3);
        });
        await Check("manual_saved_base_without_error_stays_gray_and_needs_explicit_xy_before_force",async()=>
        {
            var(f,c)=CorrectionFixture();await c.EnsureLoaded();var p=c.Snapshot.Points.Single(p=>p.Key=="P1");True(!p.HasWorkingMeasurement&&p.MeasurementXUm is null&&p.ReferenceFinalXUm is null&&p.CanCopyReferenceState);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{Reason="no XY cannot approve B"}));True(f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=5,ForcedYUm=-1,Reason="explicit input"});
            True(c.Snapshot.Points.Single(p=>p.Key=="P1").OriginalXUm is null);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.Source.Values["P1"]==new ST_MANUAL_OFFSET(5,-1));
        });
        await Check("manual_large_prior_base_is_replaced_by_fresh_operator_xy_and_repeated_confirm_does_not_accumulate",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Values=f.Source.Points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(500,-700))};await c.EnsureLoaded();
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=5,ForcedYUm=1,Reason="replace prior correction"});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.Source.Values["P1"]==new ST_MANUAL_OFFSET(5,1));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=5,ForcedYUm=1,Reason="same values again"});await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==1);
        });
        await Check("manual_staged_copy_session_reopens_without_saving_or_reintroducing_old_base",async()=>
        {
            var(f,c)=Fixture();TwoCells(f);await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="persist staged measurement"});
            var path=Path.Combine(f.StateDirectory,"replacement-session.json");c.ChooseFile=(_,_)=>path;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);
            var reopened=new CManualInspectionController(f){ChooseFile=(_,_)=>path};await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);
            True(f.Saves==0&&reopened.Snapshot.Points.Single(p=>p.Key=="P2").ReferenceFinalXUm==3&&reopened.Snapshot.Points.Single(p=>p.Key=="P2").BaseXUm==10);
            await Do(reopened,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.Source.Values["P2"].XUm==3);
        });
    }
}
