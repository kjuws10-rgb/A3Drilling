using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static async Task RunNativeFirstLoadChecks()
    {
        await Check("native_first_load_without_review_file_can_copy_empty_state_without_saving_fake_zero",async()=>
        {
            var f=new Fixture();var source=await f.Backend.LoadAsync(null,default);var engine=Engine(source);
            var points=engine.Points.Where(p=>!p.Masked).Take(2).ToArray();True(points.Length==2);
            var reference=points[0];var target=points[1];var before=(await f.Offsets.LoadLatest("QA")).FilePath;
            True(engine.CanCopyReferenceState(reference.Key));
            var preview=engine.PreviewReferenceStateCopy(reference.Key,[target.Key]);
            engine.ConfirmReferenceStateCopy(preview,[target.Key],"native initial empty state");
            var copied=engine.GetPoint(target.Key);
            True(copied.IsMeasurementStateCleared&&copied.StateCopySource is not null&&copied.ErrorXUm is null&&copied.ErrorYUm is null&&copied.Judge=="UNMEASURED");
            True(!engine.PreviewBulkCorrection().Keys.Contains(target.Key)&&engine.CanUndoReferenceCopy);
            True((await f.Offsets.LoadLatest("QA")).FilePath==before);
            engine.UndoReferenceCopy();True(!engine.GetPoint(target.Key).IsMeasurementStateCleared);
        });
        await Check("native_empty_reference_copies_working_state_while_nonzero_saved_model_offset_remains_exact",async()=>
        {
            var f=new Fixture();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",.025,-.014){IsDesignCoordinate=true}]);
            var bytes=System.IO.File.ReadAllBytes(saved.FilePath);var source=await f.Backend.LoadAsync(null,default);var engine=Engine(source);
            var target=engine.Points.First(p=>!p.Masked&&p.HasAppliedOffsetEvidence);
            var reference=engine.Points.First(p=>!p.Masked&&p.OffsetKey!=target.OffsetKey&&engine.CanCopyReferenceState(p.Key));
            var active=engine.ActiveOffsets[target.Key];True(Math.Abs(active.XUm-25)<1e-8&&Math.Abs(active.YUm+14)<1e-8);
            engine.ConfirmReferenceStateCopy(engine.PreviewReferenceStateCopy(reference.Key,[target.Key]),[target.Key],"preserve native active B");
            var point=engine.GetPoint(target.Key);
            True(point.IsMeasurementStateCleared&&!point.IsMeasurementOffsetInitialized&&point.HasAppliedOffsetEvidence);
            True(engine.ActiveOffsets[target.Key]==active&&!engine.PreviewBulkCorrection().Keys.Contains(target.Key));
            True(System.IO.File.ReadAllBytes(saved.FilePath).SequenceEqual(bytes)&&(await f.Offsets.LoadLatest("QA")).FilePath==saved.FilePath);
            engine.UndoReferenceCopy();True(!engine.GetPoint(target.Key).IsMeasurementStateCleared&&engine.ActiveOffsets[target.Key]==active);
        });
    }
}
