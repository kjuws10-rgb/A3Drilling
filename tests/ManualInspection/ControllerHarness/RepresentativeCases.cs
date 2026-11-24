using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunRepresentativeChecks()
    {
        await Check("reference_error_without_numeric_error_projects_no_value_without_exception_control_flow",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with { Points=f.Source.Points.Select(p=>p.Key=="P1"?p with
                { Judge="ERROR",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=p.OriginalMeasurement! with { Judge="ERROR",ErrorXUm=null,ErrorYUm=null } }:p).ToArray() };
            var exceptions=0;EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observer=(_,e)=>
                { if(e.Exception is InvalidOperationException)exceptions++; };
            AppDomain.CurrentDomain.FirstChanceException+=observer;
            try { await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe); }
            finally { AppDomain.CurrentDomain.FirstChanceException-=observer; }
            True(exceptions==0&&c.Snapshot.Points[0].CanBeReference&&!c.Snapshot.Points[0].CanCopyAsReference
                &&c.Snapshot.Points[0].ReferenceFinalXUm is null);
        });
        await Check("reference_projection_marks_missing_imported_and_masked_points_without_faking_copy_values",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with { Points=f.Source.Points.Select(p=>p.Key switch
                { "P1"=>p with { Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null },
                  "P2"=>p with { Source="IMPORTED",AdoptedFileMeasurement=null },_=>p with { Masked=true } }).ToArray() };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(c.Snapshot.Points.All(p=>p.CanBeReference)&&!c.Snapshot.Points[0].CanCopyAsReference
                &&!c.Snapshot.Points[1].CanCopyAsReference&&c.Snapshot.Points[2].CanCopyAsReference);
            True(c.Snapshot.Points[0].ReferenceFinalXUm is null&&c.Snapshot.Points[1].ReferenceFinalXUm is null);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P3"],r=>r with { ReferenceKey="P2",Reason="unadopted" }));
            True(f.Saves==0&&f.Executed.Count==0);
        });
        await Check("reference_projection_stored_base_is_known_correction_without_fabricating_camera_raw",async()=>
        {
            var(f,c)=CorrectionFixture();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var source=c.Snapshot.Points[0];True(source.CanBeReference&&!source.CanCopyAsReference&&source.CanCopyReferenceState&&source.ReferenceFinalXUm is null);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with { ReferenceKey="P1",Reason="stored base" });
            var target=c.Snapshot.Points[1];True(target.WorkingJudge=="UNMEASURED"&&target.WorkingXUm is null&&target.OriginalXUm is null&&target.IsMeasurementStateCleared);
            Near(target.BaseXUm,0);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P2"]));True(f.Saves==0);
        });
        await Check("reference_proxy_can_supply_another_copy_after_root_changes_and_only_selected_final_is_saved",async()=>
        {
            var(f,c)=await Loaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with { ReferenceKey="P1",Reason="first" });
            var token=c.Snapshot.Points[1].ReferenceFinalXUm;f.NewX=9;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);
            True(c.Snapshot.Points[1].CanCopyAsReference&&c.Snapshot.Points[1].ReferenceFinalXUm==token);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P3"],r=>r with { ReferenceKey="P2",Reason="frozen proxy" });
            var target=c.Snapshot.Points[2];Near(target.ReferenceFinalXUm!.Value,3);True(target.ReferenceKey=="P1"&&target.OriginalXUm==1&&f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P3"]);Near(c.Snapshot.Points[2].BaseXUm,3);Near(c.Snapshot.Points[1].BaseXUm,3);
            True(f.Saves==1&&f.Executed.SequenceEqual(["P1"]));
        });
        await Check("reference_ng_proxy_reverse_copy_keeps_ng_and_remains_unsavable",async()=>
        {
            var(f,c)=await Loaded(ngReference:true);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with { ReferenceKey="P1",Reason="A B NG" });
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P3"],r=>r with { ReferenceKey="P2",Reason="B C NG" });
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with { ReferenceKey="P3",Reason="C B frozen NG" });
            True(c.Snapshot.Points[1].WorkingJudge=="NG"&&c.Snapshot.Points[1].CanBeReference&&c.Snapshot.Points[1].CanCopyAsReference);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P2"]));True(f.Saves==0);
        });
        await Check("reference_masked_source_never_enters_masked_motion_or_masked_correction_target",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with { Points=f.Source.Points.Select(p=>p.Key=="P1"?p with { Masked=true }:p).ToArray() };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with { ReferenceKey="P1",Reason="value only, not motion" });
            True(f.Saves==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P2"]);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]));
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets,["P1"]));True(f.Saves==1&&f.Executed.Count==0);
        });
        await Check("reference_unassigned_head_cannot_turn_unproven_camera_values_into_manual_offset",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with { Points=f.Source.Points.Select(p=>p.Key=="P1"?p with { HeadNo=0,Masked=true }:p).ToArray() };
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);True(c.Snapshot.Points[0].CanBeReference&&!c.Snapshot.Points[0].CanCopyAsReference);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with { ReferenceKey="P1",Reason="unknown channel" }));True(f.Saves==0);
        });
    }
}
