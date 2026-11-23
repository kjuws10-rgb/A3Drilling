using System.Text.Json;
using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static void RunRetryVisibilityChecks()
    {
        static ST_MANUAL_OPERATION Interrupted(CManualInspectionWorkspace w,bool partial=true)
        {
            var operation=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1),Key(2)]));
            if(partial){w.RecordMeasurement(Key(1),Measurement(7,2,raw:"PARTIAL-NEW"),operation.Id);w.MarkOperationPointCompleted(operation.Id,Key(1));}
            w.CompleteOperation(operation.Id,EN_MANUAL_OPERATION_STATE.Error,"synthetic unfinished response");return w.Operation!;
        }
        Check("retry_new_selection_preview_is_non_mutating_and_original_retry_stays_optional",()=>
        {
            var w=Workspace();var prior=Interrupted(w);var audit=w.Audit.Count;var point=w.GetPoint(Key(2));
            var preview=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(3)]);
            True(!preview.IsRetry&&preview.Keys.SequenceEqual([Key(3)])&&w.Operation==prior&&w.HasIncompleteMeasurements&&w.Audit.Count==audit&&w.GetPoint(Key(2))==point);
            var retry=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,prior.RemainingKeys,true);True(retry.Keys.SequenceEqual([Key(2)]));
        });
        Check("retry_new_begin_archives_original_queue_and_invalidates_only_unreceived_old_residual",()=>
        {
            var w=Workspace();var original=w.GetPoint(Key(2)).OriginalMeasurement;var prior=Interrupted(w);var completed=w.GetPoint(Key(1));var active=w.ActiveOffsets;
            var next=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(3)]));
            var archived=JsonSerializer.Deserialize<ST_MANUAL_OPERATION>(w.Audit.Single(a=>a.Kind=="OP_SUPERSEDED").Message)!;
            True(archived.Id==prior.Id&&archived.CompletedKeys.SequenceEqual([Key(1)])&&archived.RemainingKeys.SequenceEqual([Key(2)])&&next.Id!=prior.Id);
            True(w.GetPoint(Key(1))==completed&&w.GetPoint(Key(2)).OriginalMeasurement==original&&w.GetPoint(Key(2)).Judge=="UNMEASURED"&&w.GetPoint(Key(2)).ErrorXUm is null);
            True(w.ActiveOffsets.All(pair=>pair.Value==active[pair.Key]));
            w.RecordMeasurement(Key(3),Measurement(2,3,raw:"NEW-SELECTION"),next.Id);w.MarkOperationPointCompleted(next.Id,Key(3));w.CompleteOperation(next.Id);
            True(!w.HasIncompleteMeasurements&&!w.PreviewBulkCorrection().Keys.Contains(Key(2))&&w.PreviewBulkCorrection().Keys.Contains(Key(1)));
        });
        Check("retry_manual_move_measure_and_flying_can_replace_idle_failed_queue_without_auto_correction",()=>
        {
            foreach(var kind in new[]{EN_MANUAL_OPERATION_KIND.Move,EN_MANUAL_OPERATION_KIND.Measure,EN_MANUAL_OPERATION_KIND.MoveMeasure,EN_MANUAL_OPERATION_KIND.Flying})
            {
                var w=Workspace();Interrupted(w);var operation=w.BeginOperation(w.PreviewOperation(kind,[Key(3)]));
                if(kind!=EN_MANUAL_OPERATION_KIND.Move)w.RecordMeasurement(Key(3),Measurement(2,1,raw:"MANUAL-NEW"),operation.Id);
                w.MarkOperationPointCompleted(operation.Id,Key(3));var result=w.CompleteOperation(operation.Id);
                True(!result.AutoCorrectionAllowed&&!w.HasIncompleteMeasurements&&w.Context!.RevisionId==Initial.RevisionId);
                True(w.GetPoint(Key(2)).Judge=="UNMEASURED"&&!w.PreviewBulkCorrection().Keys.Contains(Key(2)));
            }
        });
        Check("retry_running_stale_masked_and_legacy_auto_requests_cannot_replace_original_queue",()=>
        {
            var w=Workspace();var running=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1)]));
            Throws(()=>w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(3)]));True(w.Operation!.Id==running.Id);
            w.CompleteOperation(running.Id,EN_MANUAL_OPERATION_STATE.Stopped);var prior=w.Operation;var count=w.Audit.Count;
            Throws(()=>w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect,[Key(3)]));
            Throws(()=>w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,["UNKNOWN"]));
            var preview=w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(3)]);w.SetLimits(DefaultLimits with { MaxTotalUm=201 });
            Throws(()=>w.BeginOperation(preview));True(w.Operation==prior&&!w.Audit.Any(a=>a.Kind=="OP_SUPERSEDED"));
            var masked=Point(3) with { Masked=true };w=Workspace([Point(1),Point(2),masked]);prior=Interrupted(w);
            Throws(()=>w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[masked.Key]));True(w.Operation==prior&&!w.Audit.Any(a=>a.Kind=="OP_SUPERSEDED"));
        });
        Check("retry_previous_operation_late_measurement_and_completion_cannot_change_new_operation",()=>
        {
            var w=Workspace();var prior=Interrupted(w);var next=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(3)]));var before=w.GetPoint(Key(2));
            Throws(()=>w.RecordMeasurement(Key(2),Measurement(9,9,raw:"LATE"),prior.Id));
            Throws(()=>w.MarkOperationPointCompleted(prior.Id,Key(2)));Throws(()=>w.CompleteOperation(prior.Id));
            True(w.Operation!.Id==next.Id&&w.GetPoint(Key(2))==before&&w.Operation.RemainingKeys.SequenceEqual([Key(3)]));
        });
        Check("retry_new_response_ng_is_preserved_even_if_completion_was_interrupted",()=>
        {
            var w=Workspace();var prior=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(1),Key(2)]));
            w.RecordMeasurement(Key(2),Measurement(8,5,"NG","CURRENT-NG"),prior.Id);w.CompleteOperation(prior.Id,EN_MANUAL_OPERATION_STATE.Error,"stop after response");
            var ng=w.GetPoint(Key(2));var next=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Move,[Key(3)]));
            w.MarkOperationPointCompleted(next.Id,Key(3));w.CompleteOperation(next.Id);True(w.GetPoint(Key(2))==ng&&ng.Judge=="NG");
            True(!w.PreviewBulkCorrection().Keys.Contains(Key(2)));w.BuildDirectCandidate([Key(2)]);True(!w.ValidateCandidate(false).Ok);
        });
        Check("retry_session_preserves_superseded_queue_audit_current_remaining_raw_and_ledger",()=>
        {
            var w=Workspace();var prior=Interrupted(w);w.SeedConsumedMeasurements([new(Key(1),"DURABLE-CONSUMED")]);
            var next=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,[Key(3)]));var saved=w.CaptureSession();
            var restored=new CManualInspectionWorkspace();restored.RestoreSession(saved,Initial,w.Points,w.ActiveOffsets,InsideField);
            True(restored.Operation!.Id==next.Id&&restored.Operation.State==EN_MANUAL_OPERATION_STATE.Stopped&&restored.Operation.RemainingKeys.SequenceEqual([Key(3)]));
            True(restored.Audit.Any(a=>a.Kind=="OP_SUPERSEDED"&&JsonSerializer.Deserialize<ST_MANUAL_OPERATION>(a.Message)!.Id==prior.Id));
            True(restored.WasConsumed(Key(1),"DURABLE-CONSUMED")&&restored.GetPoint(Key(2)).OriginalMeasurement is not null&&restored.GetPoint(Key(2)).Judge=="UNMEASURED");
        });
        Check("retry_explicit_original_remaining_never_supersedes_or_reenables_auto_apply",()=>
        {
            var w=Workspace();var prior=Interrupted(w);var retry=w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure,prior.RemainingKeys,true));
            True(!w.Audit.Any(a=>a.Kind=="OP_SUPERSEDED")&&retry.Keys.SequenceEqual([Key(2)]));
            w.RecordMeasurement(Key(2),Measurement(1,2,raw:"RETRY-VALID"),retry.Id);w.MarkOperationPointCompleted(retry.Id,Key(2));
            True(!w.CompleteOperation(retry.Id).AutoCorrectionAllowed&&!w.HasIncompleteMeasurements);
        });
        Check("retry_session_missing_before_versions_is_rejected_instead_of_partly_superseding",()=>
        {
            var w=Workspace();Interrupted(w);var saved=w.CaptureSession();saved=saved with { Operation=saved.Operation! with { MeasurementVersionsBefore=new Dictionary<string,long>() } };
            var restored=Workspace();Throws(()=>restored.RestoreSession(saved,Initial,w.Points,w.ActiveOffsets,InsideField));
            True(restored.Operation is null&&!restored.Audit.Any(a=>a.Kind=="OP_SUPERSEDED"));
        });
    }
}
