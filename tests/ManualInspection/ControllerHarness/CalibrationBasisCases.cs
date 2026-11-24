using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunCalibrationBasisChecks()
    {
        static void Basis(FakeBackend f,double processed=100,double current=20,double residual=2,double scale=1)
        {
            var source=f.Source;var p=source.Points.Single(p=>p.Key=="P1");var basis=source.CalibrationBasisByOffsetKey[p.OffsetKey] with{BaseOffsetUm=new(processed,0),ReviewOffsetScale=scale};
            var map=source.CalibrationBasisByOffsetKey.ToDictionary(x=>x.Key,x=>x.Value);map[p.OffsetKey]=basis;
            var values=source.Values.ToDictionary(x=>x.Key,x=>x.Value);values[p.Key]=new(current,0);
            f.Source=source with{CalibrationBasisByOffsetKey=map,Values=values,Points=source.Points.Select(x=>x.Key==p.Key?x with{ErrorXUm=residual,ErrorYUm=0,
                OriginalMeasurement=x.OriginalMeasurement! with{ErrorXUm=residual,ErrorYUm=0,CalibrationBasis=basis}}:x).ToArray()};
        }
        await Check("controller_calibrated_bulk_uses_processed_base_not_current_csv_and_keeps_raw",async()=>
        {
            var(f,c)=Fixture();Basis(f);await c.EnsureLoaded();var p=c.Snapshot.Points.Single(p=>p.Key=="P1");
            True(p.WorkingXUm==2&&p.BaseXUm==20&&p.ReferenceFinalXUm==102&&p.CalibrationBasisReady&&p.HasUnsavedCorrection);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);Near(f.Source.Values["P1"].XUm,102);p=c.Snapshot.Points.Single(p=>p.Key=="P1");
            True(p.OriginalXUm==2&&p.WorkingXUm==2&&!p.HasUnsavedCorrection&&f.Saves==1);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply));True(f.Saves==1);
        });
        await Check("controller_scale_half_calculates_recommendation_without_changing_display_residual",async()=>
        {var(f,c)=Fixture();Basis(f,10,20,2,.5);await c.EnsureLoaded();var p=c.Snapshot.Points.Single(p=>p.Key=="P1");True(p.WorkingXUm==2&&p.ReferenceFinalXUm==14&&p.WorkingValueKind=="RESIDUAL");});
        await Check("controller_unknown_basis_allows_measurement_but_not_automatic_correction",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{CalibrationBasisByOffsetKey=new Dictionary<string,ST_MANUAL_CALIBRATION_BASIS>(),
                Points=f.Source.Points.Select(p=>p with{OriginalMeasurement=p.OriginalMeasurement! with{CalibrationBasis=null}}).ToArray()};await c.EnsureLoaded();
            True(c.Snapshot.CellBulkEligibleCounts[1]==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure,["P1"]);True(f.Executed.Count==1&&f.Saves==0);
            await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{Reason="no guessing"}));
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=12,ForcedYUm=0,Reason="explicit final"});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);Near(f.Source.Values["P1"].XUm,12);True(f.Saves==1);
        });
        await Check("controller_absolute_input_preserves_residual_and_is_not_subject_to_raw_delta_limit",async()=>
        {var(f,c)=Fixture();Basis(f);await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=102,ForcedYUm=0,Reason="absolute correction"});var p=c.Snapshot.Points.Single(p=>p.Key=="P1");True(p.ReferenceFinalXUm==102&&p.WorkingXUm==2&&p.OriginalXUm==2&&p.WorkingValueKind=="ABSOLUTE_REVIEW");await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);Near(f.Source.Values["P1"].XUm,102);});
        await Check("controller_absolute_input_on_empty_point_has_no_fake_measurement_or_level",async()=>
        {var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p.Key=="P1"?p with{Source="KEPT",Judge="UNMEASURED",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null}:p).ToArray()};await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=12,ForcedYUm=0,Reason="absolute no camera"});var p=c.Snapshot.Points.Single(p=>p.Key=="P1");True(p.WorkingXUm is null&&p.OriginalXUm is null&&p.ReferenceFinalXUm==12&&p.WorkingValueKind=="ABSOLUTE_REVIEW"&&p.MeasurementXUm is null);});
        await Check("controller_absolute_copy_keeps_target_own_measurement_and_stages_without_save",async()=>
        {var(f,c)=Fixture();Basis(f);await c.EnsureLoaded();await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="copy recommended absolute"});var p=c.Snapshot.Points.Single(p=>p.Key=="P2");True(p.ReferenceFinalXUm==102&&p.WorkingXUm==1&&p.OriginalXUm==1&&p.WorkingValueKind=="ABSOLUTE_REVIEW"&&f.Saves==0);await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);Near(f.Source.Values["P2"].XUm,102);});
        await Check("controller_serialized_basis_without_current_authority_is_not_laundered",async()=>
        {
            var(f,c)=Fixture();Basis(f);await c.EnsureLoaded();var path=Path.Combine(f.StateDirectory,"basis-session.json");c.ChooseFile=(_,_)=>path;await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);
            f.Source=f.Source with{CalibrationBasisByOffsetKey=new Dictionary<string,ST_MANUAL_CALIBRATION_BASIS>()};var restored=new CManualInspectionController(f){ChooseFile=(_,_)=>path};await Do(restored,EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);
            True(restored.Snapshot.Points.Single(p=>p.Key=="P1").ReferenceFinalXUm is null&&restored.Snapshot.CellBulkEligibleCounts[1]==0&&f.Saves==0);
        });
        await Check("controller_same_observation_refresh_rechecks_authority_instead_of_retaining_old_basis",async()=>
        {
            var(f,c)=Fixture();Basis(f);f.Source=f.Source with{ObservationFingerprint="STABLE-OBSERVATION"};await c.EnsureLoaded();True(c.Snapshot.Points.Single(p=>p.Key=="P1").CalibrationBasisReady);
            f.Source=f.Source with{CalibrationBasisByOffsetKey=new Dictionary<string,ST_MANUAL_CALIBRATION_BASIS>()};await c.SynchronizeExternalAsync();
            True(c.Snapshot.Points.Single(p=>p.Key=="P1").ReferenceFinalXUm is null&&!c.Snapshot.Points.Single(p=>p.Key=="P1").CalibrationBasisReady&&f.Saves==0);
        });
        await Check("controller_mode_mismatch_packet_cannot_receive_source_basis_by_backfill",async()=>
        {var(f,c)=Fixture();await c.EnsureLoaded();var engine=(CManualInspectionWorkspace)typeof(CManualInspectionController).GetField("_engine",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(c)!;var p=engine.GetPoint("P1");var raw=p.OriginalMeasurement! with{CalibrationBasis=null,CalibrationBasisUnavailableReason="actual Vision simulation differs",RawToken="MODE-MISMATCH"};engine.RecordMeasurement("P1",raw);True(engine.GetPoint("P1").OriginalMeasurement!.CalibrationBasis is null);await Throws(()=>Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Calculate,["P1"]));True(f.Saves==0);});
        await Check("controller_copy_absolute_no_raw_uses_numeric_branch_instead_of_clearing_target",async()=>
        {
            var(f,c)=Fixture();f.Source=f.Source with{Points=f.Source.Points.Select(p=>p with{Source="KEPT",Judge="UNMEASURED",ErrorXUm=null,ErrorYUm=null,OriginalMeasurement=null}).ToArray()};await c.EnsureLoaded();
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement,["P1"],r=>r with{ForcedXUm=12,ForcedYUm=0,Reason="explicit absolute"});
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P2"],r=>r with{ReferenceKey="P1",Reason="copy no raw absolute"});
            var p=c.Snapshot.Points.Single(p=>p.Key=="P2");True(p.ReferenceFinalXUm==12&&p.IsProxy&&!p.IsMeasurementStateCleared&&p.MeasurementXUm is null&&f.Saves==0);
            await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,["P3"],r=>r with{ReferenceKey="P2",Reason="proxy no raw absolute"});await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply);True(f.Saves==1&&f.Source.Values.Values.All(v=>v.XUm==12));
        });
    }
}
