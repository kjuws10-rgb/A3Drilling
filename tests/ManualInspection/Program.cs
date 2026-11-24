using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static int Failed;
    private static readonly ST_MANUAL_CONTEXT Initial = new("QA_RECIPE", "QA_RUN", "REV_INITIAL", "QA_COORDINATES_V1");
    private static readonly ST_MANUAL_LIMITS DefaultLimits = new(30, 200, 5);
    private static readonly ManualInspectionFieldEvaluator InsideField = (_, _, _) => new(true, "synthetic field accepted");

    private static int Main(string[] args)
    {
        Check("load_copies_caller_collections", () =>
        {
            var a = Point(1); var points = new[] { a }; var values = new Dictionary<string, ST_MANUAL_OFFSET> { [a.Key] = new(1, 2) };
            var w = new CManualInspectionWorkspace(); w.Load(Initial, points, values, DefaultLimits, InsideField);
            points[0] = a with { ErrorXUm = 99 }; values[a.Key] = new(99, 99);
            Equal(w.GetPoint(a.Key).ErrorXUm!.Value, 3); Offset(w.ActiveOffsets[a.Key], 1, 2);
        });
        Check("duplicate_point_identity_rejected", () => { var p = Point(1); Throws(() => Workspace([p, p])); });
        Check("nonfinite_point_position_rejected", () => Throws(() => Workspace([Point(1) with { XMm = double.NaN }])));
        Check("nonfinite_measurement_rejected", () => { var w = Workspace(); Throws(() => w.RecordMeasurement(Key(1), Measurement(double.PositiveInfinity, 1))); });
        Check("unknown_judge_never_becomes_ok", () => { var w = Workspace(); Throws(() => w.RecordMeasurement(Key(1), Measurement(1, 1, "UNKNOWN"))); });
        Check("mismatched_measurement_run_rejected", () => { var w = Workspace(); Throws(() => w.RecordMeasurement(Key(1), Measurement(1, 1) with { RunId = "OTHER" })); });
        Check("mismatched_measurement_coordinates_rejected", () => { var w = Workspace(); Throws(() => w.RecordMeasurement(Key(1), Measurement(1, 1) with { CoordinateSignature = "OTHER" })); });
        Check("masked_measurement_rejected", () => { var w = Workspace([Point(1) with { Masked = true }]); Throws(() => w.RecordMeasurement(Key(1), Measurement(1, 1))); });
        Check("empty_or_all_masked_candidate_rejected", () => { var w = Workspace([Point(1) with { Masked = true }]); Throws(() => w.BuildDirectCandidate([])); Throws(() => w.BuildDirectCandidate([Key(1)])); });
        Check("direct_candidate_is_base_plus_error", () =>
        {
            var w = Workspace(values: new() { [Key(1)] = new(7, -8) }); var c = w.BuildDirectCandidate([Key(1)]);
            Offset(c.Rows[0].Base, 7, -8); Offset(c.Rows[0].Delta, 3, -2); Offset(c.Rows[0].Final, 10, -10);
            True(w.ValidateCandidate(false).Ok);
        });
        Check("ng_direct_cannot_validate", () => { var w = Workspace([Point(1, judge: "NG")]); w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok); });
        Check("original_measurement_missing_blocks_apply", () => { var w = Workspace([Point(1) with { OriginalMeasurement = null }]); w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok); });
        Check("copy_same_final_not_same_delta", () =>
        {
            var w = Workspace(values: new() { [Key(1)] = new(3, -2), [Key(2)] = new(-4, 6) });
            var original = w.GetPoint(Key(2)).OriginalMeasurement;
            var preview = w.PreviewReferenceCopy(Key(1), [Key(1), Key(2)]); Offset(preview.Final, 6, -4);
            var c = w.ConfirmReferenceCopy(preview, [Key(1), Key(2)], "QA representative copy"); var target = w.GetPoint(Key(2));
            True(target.Judge == "OK" && target.Source == "MANUAL" && target.ProxySource!.ReferenceKey == Key(1));
            Equal(target.ErrorXUm!.Value, 3); Equal(target.ErrorYUm!.Value, -2); True(target.OriginalMeasurement == original);
            Offset(c.Rows.Single(r => r.Key == Key(2)).Final, 6, -4); Offset(c.Rows.Single(r => r.Key == Key(2)).Delta, 10, -10);
            True(!w.ValidateCandidate(false).Ok); True(w.ValidateCandidate(true).Ok);
        });
        Check("copy_ng_preserves_error_original_and_blocks_validation", () =>
        {
            var a = Point(1, x: 9, y: -1, judge: "NG"); var b = Point(2, judge: "ERROR"); var w = Workspace([a, b]);
            var original = b.OriginalMeasurement; var p = w.PreviewReferenceCopy(a.Key, [b.Key]); w.ConfirmReferenceCopy(p, [b.Key], "NG proxy review");
            var q = w.GetPoint(b.Key); True(q.Judge == "NG" && q.OriginalMeasurement == original && q.OriginalMeasurement!.Judge == "ERROR");
            True(!w.ValidateCandidate(true).Ok);
        });
        Check("proxy_reference_keeps_frozen_root_final", () => { var w = Workspace(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(p, [Key(2)], "QA"); var second=w.PreviewReferenceCopy(Key(2), [Key(3)]);Offset(second.Final,p.Final.XUm,p.Final.YUm);True(second.OriginReferenceKey==Key(1)); });
        Check("copy_preview_rejects_changed_selection", () => { var w = Workspace(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); Throws(() => w.ConfirmReferenceCopy(p, [Key(3)], "QA")); });
        Check("copy_preview_rejects_changed_measurement", () => { var w = Workspace(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.RecordMeasurement(Key(1), Measurement(2, 1, raw: "NEW_RAW")); Throws(() => w.ConfirmReferenceCopy(p, [Key(2)], "QA")); });
        Check("copy_reason_required", () => { var w = Workspace(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); Throws(() => w.ConfirmReferenceCopy(p, [Key(2)], " ")); });
        Check("copy_excludes_masked_and_reference", () =>
        {
            var w = Workspace([Point(1), Point(2), Point(3) with { Masked = true }]); var p = w.PreviewReferenceCopy(Key(1), [Key(1), Key(2), Key(3)]);
            True(p.Targets.Count == 1 && p.Targets[0].Key == Key(2) && p.ExcludedMaskedKeys.SequenceEqual([Key(3)]));
        });
        Check("copy_keeps_unrelated_pending_candidate", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(3)]); var old = w.Candidate!.Rows.Single(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]);
            var c = w.ConfirmReferenceCopy(p, [Key(2)], "QA"); True(c.Rows.Count == 2 && c.Rows.Single(r => r.Key == Key(3)) == old);
        });
        Check("copy_uses_existing_reference_candidate_final", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]);
            True(p.Final == w.Candidate!.Rows.Single().Final);
        });
        Check("copy_undo_restores_working_and_candidate", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(3)]); var old = w.Candidate; var point = w.GetPoint(Key(2));
            var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(p, [Key(2)], "QA"); True(w.CanUndoReferenceCopy);
            w.UndoReferenceCopy(); True(w.Candidate == old && w.GetPoint(Key(2)) == point && !w.CanUndoReferenceCopy);
        });
        Check("copy_undo_rejects_new_measurement", () => { var w = Workspace(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(p, [Key(2)], "QA"); w.RecordMeasurement(Key(2), Measurement(1, 1, raw: "NEW")); Throws(w.UndoReferenceCopy); });
        Check("copy_undo_rejects_changed_limits", () => { var w = Workspace(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(p, [Key(2)], "QA"); w.SetLimits(DefaultLimits with { MaxDeltaUm = 31 }); Throws(w.UndoReferenceCopy); });
        Check("limits_zero_negative_nan_infinity_rejected", () =>
        {
            var w = Workspace(); foreach (var x in new[] { 0d, -1d, double.NaN, double.PositiveInfinity }) Throws(() => w.SetLimits(DefaultLimits with { MaxDeltaUm = x }));
            Throws(() => w.SetLimits(DefaultLimits with { MaxTotalUm = 0 })); Throws(() => w.SetLimits(DefaultLimits with { FieldWidthMm = 0 }));
        });
        Check("delta_limit_is_inclusive_at_boundary", () => { var w = Workspace([Point(1, 30, -30)]); w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok); });
        Check("delta_above_limit_rejected", () => { var w = Workspace([Point(1, 30.000001, 0)]); w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok); });
        Check("total_limit_is_inclusive_at_boundary", () => { var w = Workspace([Point(1, 10, 0)], new() { [Key(1)] = new(190, 0) }); w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok); });
        Check("total_above_limit_rejected", () => { var w = Workspace([Point(1, 10, 0)], new() { [Key(1)] = new(190.000001, 0) }); w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok); });
        Check("field_evaluator_required", () => { var w = Workspace(evaluator: null, omitEvaluator: true); w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok); });
        Check("field_checked_for_unselected_and_sibling_points", () =>
        {
            var p = Point(1); var sibling = Point(1, beam: 1); var other = Point(2); var visited = new List<string>();
            ManualInspectionFieldEvaluator evaluator = (point, final, _) => { visited.Add(point.Key); return new(point.Key != sibling.Key, "synthetic sibling outside field"); };
            var w = Workspace([p, sibling, other], evaluator: evaluator); w.BuildDirectCandidate([p.Key]); True(!w.ValidateCandidate(false).Ok);
            True(visited.ToHashSet().SetEquals(new[] { p.Key, sibling.Key, other.Key }));
        });
        Check("field_evaluator_exception_blocks_not_crashes", () => { var w = Workspace(evaluator: (_, _, _) => throw new InvalidOperationException("synthetic converter error")); w.BuildDirectCandidate([Key(1)]); var v = w.ValidateCandidate(false); True(!v.Ok && v.Errors.Count > 0); });
        Check("same_shot_different_beam_final_conflict_rejected", () =>
        {
            var a = Point(1, 3, 0); var b = Point(1, 4, 0, beam: 1); var w = Workspace([a, b]); w.BuildDirectCandidate([a.Key, b.Key]); True(!w.ValidateCandidate(false).Ok);
        });
        Check("same_shot_same_final_collapses_to_one_csv_key", () =>
        {
            var a = Point(1); var b = Point(1, beam: 1); var w = Workspace([a, b]); w.BuildDirectCandidate([a.Key, b.Key]); True(w.ValidateCandidate(false).Ok);
            var s = w.CreateApplySnapshot(false); True(s.ValuesByOffsetKey.Count == 1); w.AbortApply(s);
        });
        Check("apply_updates_unselected_beam_sibling_base", () =>
        {
            var a = Point(1); var b = Point(1, beam: 1); var w = Workspace([a, b]); w.BuildDirectCandidate([a.Key]); True(w.ValidateCandidate(false).Ok);
            Apply(w, "REV_APPLIED"); Offset(w.ActiveOffsets[a.Key], 3, -2); Offset(w.ActiveOffsets[b.Key], 3, -2);
        });
        Check("snapshot_reservation_preserves_active_until_commit", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); var s = w.CreateApplySnapshot(false);
            True(w.IsBusy && w.Context!.RevisionId == Initial.RevisionId && w.Revisions.Count == 1); Offset(w.ActiveOffsets[Key(1)], 0, 0);
            w.AbortApply(s); True(!w.IsBusy && w.Revisions.Count == 1 && w.Context!.RevisionId == Initial.RevisionId); Offset(w.ActiveOffsets[Key(1)], 0, 0);
            w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
        });
        Check("pending_file_write_blocks_all_mutators", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); var s = w.CreateApplySnapshot(false);
            Throws(() => w.RecordMeasurement(Key(1), Measurement(1, 1))); Throws(() => w.SetLimits(DefaultLimits));
            Throws(() => w.UpdateContext(Initial)); Throws(() => w.BuildDirectCandidate([Key(2)]));
            Throws(() => w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Move, [Key(1)])); w.AbortApply(s);
        });
        Check("tampered_snapshot_rejected_and_original_can_abort", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); var s = w.CreateApplySnapshot(false);
            var wrong = s with { ValuesByOffsetKey = new Dictionary<string, ST_MANUAL_OFFSET> { ["C1/S1"] = new(99, 99) } };
            Throws(() => w.CommitApplied(wrong, "REV_BAD", "QA tamper")); Offset(w.ActiveOffsets[Key(1)], 0, 0); w.AbortApply(s);
        });
        Check("applied_candidate_cannot_be_applied_twice", () => { var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); var s = w.CreateApplySnapshot(false); w.CommitApplied(s, "REV1", "QA"); Throws(() => w.CommitApplied(s, "REV2", "QA")); True(w.Revisions.Count == 2); });
        Check("same_raw_timestamp_attempt_change_does_not_reset_ledger", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1");
            var m = w.GetPoint(Key(1)).OriginalMeasurement!; w.RecordMeasurement(Key(1), m with { MeasuredAt = m.MeasuredAt.AddMinutes(1) });
            w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok);
        });
        Check("same_run_reload_retains_duplicate_ledger", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1");
            w.Load(w.Context!, w.Points, w.ActiveOffsets, DefaultLimits, InsideField); w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok);
        });
        Check("new_raw_can_be_applied_again", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1");
            w.RecordMeasurement(Key(1), Measurement(.5, -.5, raw: "NEW_POST_PROCESS_RAW")); w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
        });
        Check("rollback_writes_new_revision_without_deleting_history", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1");
            var s = w.CreateRestorePreviousSnapshot(); True(s.RestoreTargetRevisionId == Initial.RevisionId); w.CommitRestored(s, "REV_RESTORE", "QA rollback");
            True(w.Context!.RevisionId == "REV_RESTORE" && w.Revisions.Count == 3 && w.Revisions[^1].ParentId == "REV1"); Offset(w.ActiveOffsets[Key(1)], 0, 0);
            Throws(()=>w.BuildDirectCandidate([Key(1)])); True(!w.GetPoint(Key(1)).HasFiniteError&&w.GetPoint(Key(1)).OriginalMeasurement is not null);
        });
        Check("rollback_write_failure_preserves_current", () => { var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1"); var s = w.CreateRestorePreviousSnapshot(); w.AbortRestore(s); True(w.Context!.RevisionId == "REV1" && !w.IsBusy && w.Revisions.Count == 2); Offset(w.ActiveOffsets[Key(1)], 3, -2); });
        Check("new_measurement_invalidates_old_candidate", () => { var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); w.RecordMeasurement(Key(1), Measurement(1, 1, raw: "NEW")); True(!w.ValidateCandidate(false).Ok); });
        Check("limits_change_invalidates_candidate", () => { var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); w.SetLimits(DefaultLimits with { MaxDeltaUm = 40 }); True(!w.ValidateCandidate(false).Ok); });
        Check("coordinate_signature_change_invalidates_candidate", () => { var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); w.UpdateContext(Initial with { CoordinateSignature = "NEW_COORD" }); True(!w.ValidateCandidate(false).Ok); });
        Check("read_only_lineage_blocks_candidate", () => { var w = new CManualInspectionWorkspace(); var p = Point(1); w.Load(Initial with { LineageValid = false }, [p], new Dictionary<string, ST_MANUAL_OFFSET>(), DefaultLimits, InsideField); Throws(() => w.BuildDirectCandidate([p.Key])); });
        Check("operation_preview_rejects_stale_settings", () => { var w = Workspace(); var p = w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1)]); w.SetLimits(DefaultLimits with { MaxTotalUm = 201 }); Throws(() => w.BeginOperation(p)); });
        Check("masked_operation_targets_excluded", () => { var w = Workspace([Point(1), Point(2) with { Masked = true }]); var p = w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1), Key(2)]); True(p.Keys.SequenceEqual([Key(1)]) && p.ExcludedMaskedKeys.SequenceEqual([Key(2)])); });
        Check("old_measurement_cannot_count_as_fresh_completion", () => { var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1)])); Throws(() => w.MarkOperationPointCompleted(op.Id, Key(1))); });
        Check("late_or_unrelated_result_rejected", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1)]));
            Throws(() => w.RecordMeasurement(Key(2), Measurement(1, 1), op.Id)); Throws(() => w.RecordMeasurement(Key(1), Measurement(1, 1), "OTHER_OP"));
            w.CompleteOperation(op.Id, EN_MANUAL_OPERATION_STATE.Stopped); Throws(() => w.RecordMeasurement(Key(1), Measurement(1, 1), op.Id));
        });
        Check("move_only_does_not_modify_measurement", () =>
        {
            var w = Workspace(); var before = w.GetPoint(Key(1)); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.Move, [Key(1)]));
            w.MarkOperationPointCompleted(op.Id, Key(1)); var c = w.CompleteOperation(op.Id); True(w.GetPoint(Key(1)) == before && !c.AutoCorrectionAllowed && !w.HasIncompleteMeasurements);
        });
        Check("automatic_correction_requires_all_new_direct_ok", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1), Key(2)]));
            foreach (var key in op.Keys) { w.RecordMeasurement(key, Measurement(1, -1, raw: "FRESH-" + key), op.Id); w.MarkOperationPointCompleted(op.Id, key); }
            var c = w.CompleteOperation(op.Id); True(c.AutoCorrectionAllowed && c.Operation.State == EN_MANUAL_OPERATION_STATE.Completed);
            True(w.Context!.RevisionId == Initial.RevisionId); w.BuildDirectCandidate(op.Keys); True(w.ValidateCandidate(false).Ok);
        });
        Check("ng_during_automatic_measurement_blocks_only_correction", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1), Key(2)]));
            w.RecordMeasurement(Key(1), Measurement(1, 1, raw: "NEW1"), op.Id); w.MarkOperationPointCompleted(op.Id, Key(1));
            w.RecordMeasurement(Key(2), Measurement(8, 0, "NG", "NEW2"), op.Id); w.MarkOperationPointCompleted(op.Id, Key(2));
            var c = w.CompleteOperation(op.Id); True(!c.AutoCorrectionAllowed && c.Operation.State == EN_MANUAL_OPERATION_STATE.CorrectionBlocked && w.Context!.RevisionId == Initial.RevisionId);
        });
        Check("stopped_operation_blocks_existing_validated_candidate", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(3)]); w.ValidateCandidate(false);
            var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1), Key(2)]));
            w.RecordMeasurement(Key(1), Measurement(1, 1, raw: "NEW1"), op.Id); w.MarkOperationPointCompleted(op.Id, Key(1));
            var c = w.CompleteOperation(op.Id, EN_MANUAL_OPERATION_STATE.Stopped); True(!c.AutoCorrectionAllowed && w.HasIncompleteMeasurements && c.Operation.CompletedKeys.SequenceEqual([Key(1)]));
            Throws(() => w.ValidateCandidate(false)); Throws(() => w.CreateApplySnapshot(false)); Throws(() => w.Load(w.Context!, w.Points, w.ActiveOffsets, DefaultLimits, InsideField));
        });
        Check("retry_preview_does_not_clear_pending_and_retry_is_measure_only", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1), Key(2)]));
            w.CompleteOperation(op.Id, EN_MANUAL_OPERATION_STATE.Error, "synthetic timeout");
            Throws(() => w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1), Key(2)], true));
            var preview = w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1), Key(2)], true); True(w.HasIncompleteMeasurements);
            var retry = w.BeginOperation(preview); foreach (var key in retry.Keys) { w.RecordMeasurement(key, Measurement(1, 1, raw: "RETRY-" + key), retry.Id); w.MarkOperationPointCompleted(retry.Id, key); }
            var c = w.CompleteOperation(retry.Id); True(!c.AutoCorrectionAllowed && !w.HasIncompleteMeasurements && w.Context!.RevisionId == Initial.RevisionId);
        });
        Check("premature_complete_becomes_error", () => { var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1)])); var c = w.CompleteOperation(op.Id); True(c.Operation.State == EN_MANUAL_OPERATION_STATE.Error && !c.AutoCorrectionAllowed && w.HasIncompleteMeasurements); });
        Check("scatter_population_sigma_exclusions_and_origin", () =>
        {
            var ps = new[] { Point(1, 1, 3), Point(2, 3, 7), Point(3, judge: "ERROR"), Point(4, judge: "UNMEASURED"), Point(5) with { Masked = true }, Point(6) with { ErrorXUm = double.NaN } };
            var m = CManualInspectionWorkspace.ComputeScatter(ps); True(m.Total == 6 && m.Count == 2 && m.ExcludedError == 1 && m.ExcludedUnmeasured == 1 && m.ExcludedMasked == 1 && m.ExcludedInvalid == 1);
            Equal(m.MeanXUm!.Value, 2); Equal(m.MeanYUm!.Value, 5); Equal(m.SigmaXUm!.Value, 1); Equal(m.SigmaYUm!.Value, 2); Equal(m.MaxVectorUm!.Value, Math.Sqrt(58));
        });
        Check("scatter_original_and_working_proxy_distinct", () =>
        {
            var w = Workspace([Point(1, 3, -2), Point(2, 1, 1)]); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(p, [Key(2)], "QA");
            var working = CManualInspectionWorkspace.ComputeScatter(w.Points); var original = CManualInspectionWorkspace.ComputeScatter(w.Points, true);
            True(working.ProxyCount == 1 && original.ProxyCount == 0); Equal(working.MeanXUm!.Value, 3); Equal(original.MeanXUm!.Value, 2);
        });
        Check("scatter_empty_does_not_invent_zero_measurement", () => { var m = CManualInspectionWorkspace.ComputeScatter([]); True(m.Count == 0 && m.MeanXUm is null && m.SigmaXUm is null && m.MaxVectorUm is null); });
        Check("ten_thousand_points_no_implicit_record_limit", () =>
        {
            var points = Enumerable.Range(0, 10_000).Select(i => Point(i % 625 + 1, cell: i / 625 + 1, head: i % 8 + 1)).ToArray();
            var w = Workspace(points); var m = CManualInspectionWorkspace.ComputeScatter(w.Points); True(w.Points.Count == 10_000 && m.Count == 10_000);
        });
        Check("direct_working_error_must_match_original_raw_conversion", () =>
        {
            var original = Point(1); var w = Workspace([original with { ErrorXUm = 4, ErrorYUm = -1 }]);
            w.BuildDirectCandidate([original.Key]); True(!w.ValidateCandidate(false).Ok);
        });
        Check("direct_working_ok_cannot_override_original_ng", () =>
        {
            var original = Point(1, judge: "NG"); var w = Workspace([original with { Judge = "OK" }]);
            w.BuildDirectCandidate([original.Key]); True(!w.ValidateCandidate(false).Ok);
        });
        Check("apply_requires_prior_validation", () => { var w = Workspace(); w.BuildDirectCandidate([Key(1)]); Throws(() => w.CreateApplySnapshot(false)); });
        Check("session_roundtrip_retains_provenance_and_revalidates_candidate", () =>
        {
            var w = Workspace(); var copy = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(copy, [Key(2)], "QA copy"); w.ValidateCandidate(true);
            var fresh = Workspace(); fresh.RestoreSessionJson(w.ExportSessionJson(), Initial, fresh.Points, fresh.ActiveOffsets, InsideField);
            True(fresh.GetPoint(Key(2)).ProxySource?.ReferenceKey == Key(1)); True(fresh.GetPoint(Key(2)).OriginalMeasurement == w.GetPoint(Key(2)).OriginalMeasurement);
            True(fresh.Candidate?.State == EN_MANUAL_CANDIDATE_STATE.Draft && fresh.Candidate.Validation is null && !fresh.IsBusy);
            True(fresh.ValidateCandidate(true).Ok);
        });
        Check("session_roundtrip_keeps_applied_raw_ledger", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1");
            var expected = w.Context!; var points = w.Points; var actual = w.ActiveOffsets;
            var fresh = new CManualInspectionWorkspace(); fresh.RestoreSessionJson(w.ExportSessionJson(), expected, points, actual, InsideField);
            True(fresh.Context!.RevisionId == "REV1" && fresh.Revisions.Count == 2); fresh.BuildDirectCandidate([Key(1)]); True(!fresh.ValidateCandidate(false).Ok);
        });
        Check("session_restore_requires_actual_revision_and_values", () =>
        {
            var w = Workspace(); var session = w.CaptureSession(); var fresh = Workspace();
            Throws(() => fresh.RestoreSession(session, Initial with { RevisionId = "OTHER" }, fresh.Points, fresh.ActiveOffsets, InsideField));
            var wrong = fresh.ActiveOffsets.ToDictionary(pair => pair.Key, pair => pair.Value); wrong[Key(1)] = new(1, 1);
            Throws(() => fresh.RestoreSession(session, Initial, fresh.Points, wrong, InsideField));
        });
        Check("session_restore_rejects_geometry_or_mask_mismatch", () =>
        {
            var w = Workspace(); var session = w.CaptureSession(); var fresh = Workspace();
            var moved = fresh.Points.Select(p => p.Key == Key(1) ? p with { YMm = p.YMm + .001 } : p).ToArray();
            Throws(() => fresh.RestoreSession(session, Initial, moved, fresh.ActiveOffsets, InsideField));
            var masked = fresh.Points.Select(p => p.Key == Key(1) ? p with { Masked = true } : p).ToArray();
            Throws(() => fresh.RestoreSession(session, Initial, masked, fresh.ActiveOffsets, InsideField));
        });
        Check("session_restore_missing_proxy_reference_provenance_invalidates_lineage", () =>
        {
            var w = Workspace(); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(p, [Key(2)], "QA"); var s = w.CaptureSession();
            s = s with { Points = s.Points.Select(p => p.Key == Key(2) ? p with { OriginalMeasurement = null,ProxySource=p.ProxySource! with{ReferenceMeasurementToken=""} } : p).ToArray() };
            var fresh = Workspace(); fresh.RestoreSession(s, Initial, fresh.Points, fresh.ActiveOffsets, InsideField);
            True(!fresh.Context!.LineageValid); Throws(() => fresh.BuildDirectCandidate([Key(2)]));
        });
        Check("session_proxy_error_original_is_valid_not_invented", () =>
        {
            var w = Workspace([Point(1), Point(2, judge: "ERROR")]); var p = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(p, [Key(2)], "QA");
            var fresh = Workspace([Point(1), Point(2, judge: "ERROR")]); fresh.RestoreSession(w.CaptureSession(), Initial, fresh.Points, fresh.ActiveOffsets, InsideField);
            True(fresh.Context!.LineageValid && fresh.GetPoint(Key(2)).OriginalMeasurement?.Judge == "ERROR");
        });
        Check("session_running_restores_stopped_without_automatic_motion", () =>
        {
            var w = Workspace(); w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1), Key(2)]));
            var fresh = Workspace(); fresh.RestoreSession(w.CaptureSession(), Initial, fresh.Points, fresh.ActiveOffsets, InsideField);
            True(fresh.Operation?.State == EN_MANUAL_OPERATION_STATE.Stopped && fresh.HasIncompleteMeasurements && !fresh.IsBusy);
            Throws(() => fresh.CreateApplySnapshot(false));
        });
        Check("session_applying_resets_approval_and_pending_file_write", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); var write = w.CreateApplySnapshot(false); var session = w.CaptureSession();
            var fresh = Workspace(); fresh.RestoreSession(session, Initial, fresh.Points, fresh.ActiveOffsets, InsideField);
            True(!fresh.IsBusy && fresh.Candidate?.State == EN_MANUAL_CANDIDATE_STATE.Draft && fresh.Candidate.Validation is null);
            Throws(() => fresh.CreateApplySnapshot(false)); w.AbortApply(write);
        });
        Check("session_pending_completed_overlap_rejected", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1), Key(2)]));
            var s = w.CaptureSession(); s = s with { Operation = op with { CompletedKeys = new[] { Key(1) }, RemainingKeys = new[] { Key(1), Key(2) } } };
            var fresh = Workspace(); Throws(() => fresh.RestoreSession(s, Initial, fresh.Points, fresh.ActiveOffsets, InsideField));
        });
        Check("session_operation_coordinate_context_mismatch_rejected", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1)])); var s = w.CaptureSession();
            s = s with { Operation = op with { CoordinateSignature = "OLD_COORDINATE_CONFIGURATION" } };
            var fresh = Workspace(); Throws(() => fresh.RestoreSession(s, Initial, fresh.Points, fresh.ActiveOffsets, InsideField));
        });
        Check("session_operation_base_revision_mismatch_rejected", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1)])); var s = w.CaptureSession();
            s = s with { Operation = op with { BaseRevisionId = "STALE_REVISION" } };
            var fresh = Workspace(); Throws(() => fresh.RestoreSession(s, Initial, fresh.Points, fresh.ActiveOffsets, InsideField));
        });
        Check("session_revision_parent_must_exist", () =>
        {
            var w = Workspace(); var s = w.CaptureSession(); s = s with { Revisions = s.Revisions.Select(r => r with { ParentId = "MISSING_PARENT" }).ToArray() };
            var fresh = Workspace(); Throws(() => fresh.RestoreSession(s, Initial, fresh.Points, fresh.ActiveOffsets, InsideField));
        });
        Check("capture_error_remains_incomplete_until_new_valid_response", () =>
        {
            foreach (var judge in new[] { "ERROR", "UNMEASURED" })
            {
                var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1)]));
                w.RecordMeasurement(Key(1), Measurement(null, null, judge, "FAILED_RESPONSE"), op.Id);
                Throws(() => w.MarkOperationPointCompleted(op.Id, Key(1)));
                var end = w.CompleteOperation(op.Id, EN_MANUAL_OPERATION_STATE.Error, judge);
                True(w.HasIncompleteMeasurements && end.Operation.RemainingKeys.SequenceEqual([Key(1)]));
            }
        });
        Check("valid_ng_response_is_measured_complete_with_no_auto_correction", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1)]));
            w.RecordMeasurement(Key(1), Measurement(8, 0, "NG", "VALID_NG"), op.Id); w.MarkOperationPointCompleted(op.Id, Key(1));
            var end = w.CompleteOperation(op.Id); True(!w.HasIncompleteMeasurements && !end.AutoCorrectionAllowed && end.Operation.State == EN_MANUAL_OPERATION_STATE.CorrectionBlocked);
        });
        Check("restored_measured_delta_cannot_override_raw_error", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); var s = w.CaptureSession();
            s = s with { Candidate = s.Candidate! with { Rows = s.Candidate.Rows.Select(r => r with { Delta = new(5, -1), Final = new(5, -1) }).ToArray() } };
            var fresh = Workspace(); fresh.RestoreSession(s, Initial, fresh.Points, fresh.ActiveOffsets, InsideField); True(!fresh.ValidateCandidate(false).Ok);
        });
        Check("same_semantic_raw_with_new_response_uid_time_does_not_reset_ledger", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1");
            var m = w.GetPoint(Key(1)).OriginalMeasurement!;
            w.RecordMeasurement(Key(1), m with { RawData = "New response UID=99999; timestamp changed; same semantic target/hole", MeasuredAt = m.MeasuredAt.AddMinutes(1) });
            w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok);
        });
        Check("session_completed_operation_can_reference_applied_parent_revision", () =>
        {
            var w = Workspace(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1)]));
            w.RecordMeasurement(Key(1), Measurement(1, 1, raw: "FRESH_QA"), op.Id); w.MarkOperationPointCompleted(op.Id, Key(1)); True(w.CompleteOperation(op.Id).AutoCorrectionAllowed);
            w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); Apply(w, "REV1");
            var fresh = new CManualInspectionWorkspace(); fresh.RestoreSession(w.CaptureSession(), w.Context!, w.Points, w.ActiveOffsets, InsideField);
            True(fresh.Operation!.BaseRevisionId == Initial.RevisionId && fresh.Context!.RevisionId == "REV1" && !fresh.HasIncompleteMeasurements && !fresh.IsBusy);
        });
        Check("masked_unassigned_head_zero_is_marker_only_without_coordinate_evidence", () =>
        {
            var masked = Point(1, head: 1) with { HeadNo = 0, Masked = true }; var w = Workspace([masked, Point(2)]);
            True(w.Points.Count == 2); Throws(() => w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [masked.Key]));
            Throws(() => w.BuildDirectCandidate([masked.Key]));Throws(()=>w.PreviewReferenceCopy(masked.Key,[Key(2)]));
            Throws(() => Workspace([masked with { Masked = false }]));
        });
        Check("imported_result_is_not_direct_reference_until_new_measurement", () =>
        {
            var imported = Point(1) with { Source = "IMPORTED" }; var w = Workspace([imported, Point(2)]);
            Throws(() => w.BuildDirectCandidate([imported.Key])); Throws(() => w.PreviewReferenceCopy(imported.Key, [Key(2)]));
            w.RecordMeasurement(imported.Key, Measurement(1, 1, raw: "NATIVE_NEW")); w.BuildDirectCandidate([imported.Key]); True(w.ValidateCandidate(false).Ok);
        });
        Check("native_reference_copy_preserves_old_imported_original_frame", () =>
        {
            var imported = Point(2) with { Source = "IMPORTED", OriginalMeasurement = Point(2).OriginalMeasurement! with { CoordinateSignature = "OLD_IMPORTED_FRAME" } };
            var w = Workspace([Point(1), imported]); var copy = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(copy, [Key(2)], "Current native reference for old result");
            True(w.GetPoint(Key(2)).OriginalMeasurement!.CoordinateSignature == "OLD_IMPORTED_FRAME"); True(w.ValidateCandidate(true).Ok);
            var fresh = Workspace([Point(1), imported]); fresh.RestoreSession(w.CaptureSession(), Initial, fresh.Points, fresh.ActiveOffsets, InsideField);
            True(fresh.Context!.LineageValid && fresh.GetPoint(Key(2)).ProxySource?.OriginalSource == "IMPORTED"); True(fresh.ValidateCandidate(true).Ok);
        });
        Check("missing_proxy_original_is_excluded_as_unknown_in_original_scatter", () =>
        {
            var p = Point(1) with { Source = "MANUAL", OriginalMeasurement = null,
                ProxySource = new(Key(2), "OK", 3, -2, new(3, -2), "unknown original", Initial.RunId, DateTimeOffset.UtcNow) };
            var m = CManualInspectionWorkspace.ComputeScatter([p], true); True(m.Count == 0 && m.ExcludedInvalid == 1 && m.ExcludedUnmeasured == 0);
        });
        Check("remeasurement_history_retains_previous_raw_and_proxy_state", () =>
        {
            var w = Workspace(); var before = w.GetPoint(Key(2)).OriginalMeasurement; var copy = w.PreviewReferenceCopy(Key(1), [Key(2)]); w.ConfirmReferenceCopy(copy, [Key(2)], "QA copy");
            w.RecordMeasurement(Key(2), Measurement(.5, .25, raw: "NEW_NATIVE_RAW")); var history = w.CaptureSession().History;
            True(history.Any(h => h.Key == Key(2) && h.Before.ProxySource is not null && h.OriginalBefore == before && h.OriginalAfter?.RawToken == "NEW_NATIVE_RAW"));
            var fresh = Workspace(); fresh.RestoreSession(w.CaptureSession(), Initial, fresh.Points, fresh.ActiveOffsets, InsideField);
            True(fresh.CaptureSession().History.Any(h => h.OriginalBefore?.RawData == before!.RawData));
        });
        Check("file_quantization_is_reflected_in_active_base_and_candidate", () =>
        {
            var w = Workspace([Point(1, 1.234567, -2.345678)]); w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
            var snapshot = w.CreateApplySnapshot(false); Offset(snapshot.ValuesByPointKey[Key(1)], 1.235, -2.346);
            w.CommitApplied(snapshot, "REV_FILE", "QA file precision"); Offset(w.ActiveOffsets[Key(1)], 1.235, -2.346); Offset(w.Candidate!.Rows.Single().Final, 1.235, -2.346);
            var fresh = new CManualInspectionWorkspace(); fresh.RestoreSession(w.CaptureSession(), w.Context!, w.Points, w.ActiveOffsets, InsideField); Offset(fresh.ActiveOffsets[Key(1)], 1.235, -2.346);
        });
        Check("actual_writer_values_beyond_quantization_tolerance_are_rejected", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); var snapshot = w.CreateApplySnapshot(false);
            var wrong = snapshot.ValuesByPointKey.ToDictionary(pair => pair.Key, pair => pair.Value); wrong[Key(1)] = new(3.1, -2);
            Throws(() => w.CommitApplied(snapshot, "REV_BAD_FILE", "QA wrong writer", wrong)); Offset(w.ActiveOffsets[Key(1)], 0, 0); w.AbortApply(snapshot);
        });
        Check("actual_writer_missing_point_is_rejected", () =>
        {
            var w = Workspace(); w.BuildDirectCandidate([Key(1)]); w.ValidateCandidate(false); var snapshot = w.CreateApplySnapshot(false);
            var wrong = snapshot.ValuesByPointKey.Where(pair => pair.Key != Key(2)).ToDictionary(pair => pair.Key, pair => pair.Value);
            Throws(() => w.CommitApplied(snapshot, "REV_MISSING", "QA missing key", wrong)); w.AbortApply(snapshot);
        });
        Check("actual_writer_sibling_beam_mismatch_is_rejected", () =>
        {
            var a = Point(1); var b = Point(1, beam: 1); var w = Workspace([a, b]); w.BuildDirectCandidate([a.Key]); w.ValidateCandidate(false); var snapshot = w.CreateApplySnapshot(false);
            var wrong = snapshot.ValuesByPointKey.ToDictionary(pair => pair.Key, pair => pair.Value); wrong[b.Key] = new(3.00025, -2);
            Throws(() => w.CommitApplied(snapshot, "REV_BEAM_MISMATCH", "QA wrong sibling", wrong)); w.AbortApply(snapshot);
        });
        Check("file_quantization_rechecks_total_limit_before_reserving_write", () =>
        {
            var w = Workspace([Point(1, 1.23459, 0)]); w.SetLimits(DefaultLimits with { MaxTotalUm = 1.2346 }); w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
            Throws(() => w.CreateApplySnapshot(false)); True(!w.IsBusy && w.Context!.RevisionId == Initial.RevisionId);
        });
        Check("file_quantization_rechecks_actual_field_before_reserving_write", () =>
        {
            var w = Workspace([Point(1, 2500.00059, 0)], evaluator: (_, final, limit) => new(Math.Abs(final.XUm / 1000) <= limit.FieldWidthMm / 2, "synthetic field edge"));
            w.SetLimits(new(5000, 5000, 5.0000012)); w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
            Throws(() => w.CreateApplySnapshot(false)); True(!w.IsBusy);
        });
        Check("same_run_old_session_cannot_clear_current_incomplete_measurements", () =>
        {
            var w = Workspace(); var old = w.CaptureSession(); var op = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [Key(1), Key(2)]));
            w.CompleteOperation(op.Id, EN_MANUAL_OPERATION_STATE.Stopped); True(w.HasIncompleteMeasurements);
            Throws(() => w.RestoreSession(old, Initial, w.Points, w.ActiveOffsets, InsideField)); True(w.HasIncompleteMeasurements);
        });

        Check("applied_reference_copy_reuses_active_final_without_double_error", () =>
        {
            var w = Workspace([Point(1, 3, 0), Point(2)], new() { [Key(1)] = new(10, 0) });
            w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok); Apply(w, "REV_REF");
            Offset(w.ActiveOffsets[Key(1)], 13, 0); Offset(w.PreviewReferenceCopy(Key(1), [Key(2)]).Final, 13, 0);
        });
        Check("sibling_beam_apply_consumes_reference_old_raw_for_copy", () =>
        {
            var reference = Point(1, 3, 0); var sibling = Point(1, 3, 0, beam: 1); var target = Point(2);
            var w = Workspace([reference, sibling, target], new() { [reference.Key] = new(10, 0), [sibling.Key] = new(10, 0) });
            w.BuildDirectCandidate([sibling.Key]); True(w.ValidateCandidate(false).Ok); Apply(w, "REV_SIBLING");
            Offset(w.PreviewReferenceCopy(reference.Key, [target.Key]).Final, 13, 0);
            w.BuildDirectCandidate([reference.Key]); True(!w.ValidateCandidate(false).Ok);
        });
        Check("other_shot_apply_does_not_consume_untouched_reference_raw", () =>
        {
            var w = Workspace([Point(1, 3, 0), Point(2)], new() { [Key(1)] = new(10, 0) });
            w.BuildDirectCandidate([Key(2)]); True(w.ValidateCandidate(false).Ok); Apply(w, "REV_OTHER");
            Offset(w.PreviewReferenceCopy(Key(1), [Key(2)]).Final, 13, 0);
            w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
        });
        Check("copy_to_different_groups_keeps_same_unconsumed_reference_final", () =>
        {
            var w = Workspace([Point(1, 3, 0), Point(2), Point(3)], new() { [Key(1)] = new(10, 0) });
            var a = w.PreviewReferenceCopy(Key(1), [Key(2)]); Offset(a.Final, 13, 0); w.ConfirmReferenceCopy(a, [Key(2)], "Group A");
            True(w.ValidateCandidate(true).Ok); var first = w.CreateApplySnapshot(true); w.CommitApplied(first, "REV_GROUP_A", "QA group A");
            var b = w.PreviewReferenceCopy(Key(1), [Key(3)]); Offset(b.Final, 13, 0); w.ConfirmReferenceCopy(b, [Key(3)], "Group B");
            True(w.ValidateCandidate(true).Ok); var second = w.CreateApplySnapshot(true); w.CommitApplied(second, "REV_GROUP_B", "QA group B");
            Offset(w.ActiveOffsets[Key(1)], 10, 0); Offset(w.ActiveOffsets[Key(2)], 13, 0); Offset(w.ActiveOffsets[Key(3)], 13, 0);
        });
        Check("rollback_reference_uses_saved_base_without_readding_consumed_raw", () =>
        {
            var w = Workspace([Point(1, 3, 0), Point(2)], new() { [Key(1)] = new(10, 0) });
            w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok); Apply(w, "REV_REF");
            var restore = w.CreateRestorePreviousSnapshot(); w.CommitRestored(restore, "REV_RESTORE", "QA rollback");
            var copy=w.PreviewReferenceCopy(Key(1), [Key(2)]);Offset(copy.Final,10,0);True(copy.ReferenceValueKind=="APPLIED_OFFSET"&&copy.ErrorXUm is null);
            w.ConfirmReferenceCopy(copy,[Key(2)],"Saved base after rollback");True(w.ValidateCandidate(true).Ok);Offset(w.ActiveOffsets[Key(1)],10,0);
            True(w.GetPoint(Key(1)).OriginalMeasurement is not null&&!w.GetPoint(Key(1)).HasFiniteError);
        });
        Check("session_restore_preserves_consumed_reference_final", () =>
        {
            var w = Workspace([Point(1, 3, 0), Point(2)], new() { [Key(1)] = new(10, 0) });
            w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok); Apply(w, "REV_REF");
            var fresh = new CManualInspectionWorkspace(); fresh.RestoreSession(w.CaptureSession(), w.Context!, w.Points, w.ActiveOffsets, InsideField);
            Offset(fresh.PreviewReferenceCopy(Key(1), [Key(2)]).Final, 13, 0);
        });
        Check("fresh_reference_raw_after_apply_can_form_new_final", () =>
        {
            var w = Workspace([Point(1, 3, 0), Point(2)], new() { [Key(1)] = new(10, 0) });
            w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok); Apply(w, "REV_REF");
            w.RecordMeasurement(Key(1), Measurement(.5, -.25, raw: "FRESH_AFTER_APPLY"));
            Offset(w.PreviewReferenceCopy(Key(1), [Key(2)]).Final, 13.5, -.25);
            w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
        });

        Check("apply_snapshot_consumed_tokens_include_native_siblings", () =>
        {
            var a=Point(1);var sibling=Point(1,beam:1);var unrelated=Point(2);var w=Workspace([a,sibling,unrelated]);
            w.BuildDirectCandidate([a.Key]);True(w.ValidateCandidate(false).Ok);var snapshot=w.CreateApplySnapshot(false);
            True(snapshot.ConsumedMeasurements.Keys.ToHashSet().SetEquals([a.Key,sibling.Key]));
            True(snapshot.ConsumedMeasurements.Values.All(x=>!string.IsNullOrEmpty(x)));w.AbortApply(snapshot);
        });
        Check("manual_target_snapshot_does_not_consume_unrelated_reference_shot", () =>
        {
            var w=Workspace();var preview=w.PreviewReferenceCopy(Key(1),[Key(2)]);w.ConfirmReferenceCopy(preview,[Key(2)],"QA");
            True(w.ValidateCandidate(true).Ok);var snapshot=w.CreateApplySnapshot(true);
            True(!snapshot.ConsumedMeasurements.ContainsKey(Key(1)));w.AbortApply(snapshot);
        });
        Check("tampered_consumed_token_dictionary_rejected_without_commit", () =>
        {
            var w=Workspace();w.BuildDirectCandidate([Key(1)]);w.ValidateCandidate(false);var snapshot=w.CreateApplySnapshot(false);
            var tampered=snapshot with{ConsumedMeasurements=new Dictionary<string,string>{{Key(1),"FORGED"}}};
            Throws(()=>w.CommitApplied(tampered,"REV_FORGED","QA"));True(w.Context!.RevisionId==Initial.RevisionId);w.AbortApply(snapshot);
        });
        Check("aborted_snapshot_does_not_consume_reference_or_sibling_raw", () =>
        {
            var a=Point(1);var sibling=Point(1,beam:1);var w=Workspace([a,sibling,Point(2)]);
            w.BuildDirectCandidate([a.Key]);w.ValidateCandidate(false);var snapshot=w.CreateApplySnapshot(false);w.AbortApply(snapshot);
            w.BuildDirectCandidate([sibling.Key]);True(w.ValidateCandidate(false).Ok);
            Offset(w.PreviewReferenceCopy(a.Key,[Key(2)]).Final,3,-2);
        });

        Check("older_raw_a_after_raw_b_is_still_consumed", () =>
        {
            var w=Workspace();var a=w.GetPoint(Key(1)).OriginalMeasurement!;
            w.BuildDirectCandidate([Key(1)]);w.ValidateCandidate(false);var first=w.CreateApplySnapshot(false);w.CommitApplied(first,"REV_A","QA");
            w.RecordMeasurement(Key(1),Measurement(.5,-.5,raw:"RAW_B"));w.BuildDirectCandidate([Key(1)]);True(w.ValidateCandidate(false).Ok);Apply(w,"REV_B");
            w.RecordMeasurement(Key(1),a);w.BuildDirectCandidate([Key(1)]);True(!w.ValidateCandidate(false).Ok);
            Offset(w.PreviewReferenceCopy(Key(1),[Key(2)]).Final,3.5,-2.5);True(w.WasConsumed(Key(1),first.ConsumedMeasurements[Key(1)]));
        });
        Check("session_keeps_all_consumed_raw_not_only_latest", () =>
        {
            var w=Workspace();var a=w.GetPoint(Key(1)).OriginalMeasurement!;
            w.BuildDirectCandidate([Key(1)]);w.ValidateCandidate(false);Apply(w,"REV_A");w.RecordMeasurement(Key(1),Measurement(.5,0,raw:"RAW_B"));
            w.BuildDirectCandidate([Key(1)]);w.ValidateCandidate(false);Apply(w,"REV_B");
            var fresh=new CManualInspectionWorkspace();fresh.RestoreSession(w.CaptureSession(),w.Context!,w.Points,w.ActiveOffsets,InsideField);
            fresh.RecordMeasurement(Key(1),a);fresh.BuildDirectCandidate([Key(1)]);True(!fresh.ValidateCandidate(false).Ok);
            Offset(fresh.ResolveReferenceFinal(Key(1)),3.5,-2);
        });
        Check("durable_seed_keeps_two_tokens_for_same_point", () =>
        {
            var source=Workspace();source.BuildDirectCandidate([Key(1)]);source.ValidateCandidate(false);var a=source.CreateApplySnapshot(false);source.AbortApply(a);
            source.RecordMeasurement(Key(1),Measurement(.5,0,raw:"RAW_B"));source.BuildDirectCandidate([Key(1)]);source.ValidateCandidate(false);var b=source.CreateApplySnapshot(false);source.AbortApply(b);
            var w=Workspace();w.SeedConsumedMeasurements(a.ConsumedMeasurements.Concat(b.ConsumedMeasurements));
            True(w.WasConsumed(Key(1),a.ConsumedMeasurements[Key(1)])&&w.WasConsumed(Key(1),b.ConsumedMeasurements[Key(1)]));
            w.BuildDirectCandidate([Key(1)]);True(!w.ValidateCandidate(false).Ok);Offset(w.ResolveReferenceFinal(Key(1)),0,0);
            Offset(w.ResolveReferenceFinal(Key(2)),1,1);
        });
        Check("consumed_companion_does_not_block_fresh_driving_beam", () =>
        {
            var a=Point(1);var sibling=Point(1,beam:1);var w=Workspace([a,sibling,Point(2)]);
            w.BuildDirectCandidate([a.Key]);w.ValidateCandidate(false);Apply(w,"REV_A");
            w.RecordMeasurement(a.Key,Measurement(.5,0,raw:"FRESH_DRIVING"));w.BuildDirectCandidate([a.Key]);True(w.ValidateCandidate(false).Ok);
            var next=w.CreateApplySnapshot(false);True(next.ConsumedMeasurements.ContainsKey(sibling.Key));w.CommitApplied(next,"REV_FRESH","QA");
            Offset(w.ActiveOffsets[a.Key],3.5,-2);
        });

        Check("same_run_changed_beam_display_keeps_opaque_history_on_restore", () =>
        {
            var w=Workspace();w.BuildDirectCandidate([Key(1)]);w.ValidateCandidate(false);var snapshot=w.CreateApplySnapshot(false);w.CommitApplied(snapshot,"REV_A","QA");
            var token=snapshot.ConsumedMeasurements[Key(1)];var other=Point(2);var value=w.ActiveOffsets[other.Key];
            w.Load(w.Context!,[other],new Dictionary<string,ST_MANUAL_OFFSET>{{other.Key,value}},DefaultLimits,InsideField);
            var session=w.CaptureSession();True(!session.AppliedMeasurements.ContainsKey(Key(1)));
            var fresh=new CManualInspectionWorkspace();fresh.RestoreSession(session,w.Context!,w.Points,w.ActiveOffsets,InsideField);
            True(fresh.Points.Count==1&&fresh.WasConsumed(Key(1),token));
        });

        Check("same_run_old_session_cannot_erase_new_durable_consumed_history", () =>
        {
            var w=Workspace();var old=w.CaptureSession();
            w.SeedConsumedMeasurements([new KeyValuePair<string,string>(Key(1),"COMMITTED_EXTRA_TOKEN")]);
            w.RestoreSession(old,Initial,w.Points,w.ActiveOffsets,InsideField);
            True(w.WasConsumed(Key(1),"COMMITTED_EXTRA_TOKEN"));
        });

        RunStateSequenceChecks();
        RunOffsetInitChecks();
        RunMeasurementWorkflowChecks();
        RunBulkHistoryChecks();
        RunReferenceValueChecks();
        RunRetryVisibilityChecks();
        RunFirstLoadStateChecks();
        RunInitRecoveryChecks();
        RunRepresentativeApplyChecks();
        RunCellCsvWorkflowChecks();
        RunReplacementWorkflowChecks();
        RunCalibrationBasisChecks();
        var output = args.Length == 2 && args[0] == "--report" ? args[1] : Path.Combine(AppContext.BaseDirectory, "manual-inspection-results.json");
        var report = new { schemaVersion = "a3-manual-inspection-core-tests/1", passed = Failed == 0, count = Results.Count, failed = Failed, tests = Results, sourceFiles = SourceInputs(),
            scope = "Linked production ManualInspection common files only; synthetic values/fake field evaluator. No equipment, WPF, SDK or device scripts executed." };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine($"Core tests: {Results.Count - Failed}/{Results.Count} passed. Report: {output}");
        return Failed == 0 ? 0 : 1;
    }

    private static ST_MANUAL_POINT Point(int shot, double x = 3, double y = -2, string judge = "OK", int cell = 1, int head = 1, int beam = 11)
    {
        var valid = judge is "OK" or "NG"; var m = Measurement(valid ? x : null, valid ? y : null, judge, $"RAW-{cell}-{shot}-{beam}") with{CalibrationBasis=ZeroBasis(cell,shot,head)};
        return new(Key(shot, cell, head, beam), cell, head, shot, beam, (shot % 5) * .1, (shot / 5) * .1)
        { Row = shot / 5, Column = shot % 5, Judge = judge, Source = valid ? "MEASURED" : "KEPT", ErrorXUm = valid ? x : null,
            ErrorYUm = valid ? y : null, OriginalMeasurement = m };
    }
    private static string Key(int shot, int cell = 1, int head = 1, int beam = 11) => $"C{cell:00}/H{head:00}/S{shot}/B{beam:00}";
    private static ST_MANUAL_MEASUREMENT Measurement(double? x, double? y, string judge = "OK", string raw = "RAW_DEFAULT") => new(judge, x, y, raw, Initial.RecipeId, Initial.RunId, Initial.CoordinateSignature) { IsStageSimulation=false,IsVisionSimulation=false,RawData = "SyntheticRaw:" + raw, ScannerErrorXUm = x, ScannerErrorYUm = y };
    private static CManualInspectionWorkspace Workspace(IEnumerable<ST_MANUAL_POINT>? points = null, Dictionary<string, ST_MANUAL_OFFSET>? values = null,
        ManualInspectionFieldEvaluator? evaluator = null, bool omitEvaluator = false)
    {
        var selected=(points ?? [Point(1),Point(2,1,1),Point(3,-1,2)]).ToArray();
        var basis=selected.GroupBy(p=>p.OffsetKey).ToDictionary(g=>g.Key,g=>ZeroBasis(g.First().CellNo,g.First().ShotNo,g.First().HeadNo));
        var w = new CManualInspectionWorkspace(); w.Load(Initial, selected, values ?? [], DefaultLimits, omitEvaluator ? null : evaluator ?? InsideField,calibrationBases:basis); return w;
    }
    private static ST_MANUAL_CALIBRATION_BASIS ZeroBasis(int cell,int shot,int head)=>new("QA-BASIS-C"+cell+"/S"+shot,"QA-PROCESS",Initial.RecipeId,"QA-GLASS",DateTimeOffset.UnixEpoch,
        Initial.CoordinateSignature,"QA-VERIFIED-ZERO",ST_MANUAL_OFFSET.Zero,1){RunId=Initial.RunId,CellNo=cell,ShotNo=shot,HeadNo=head};
    private static void Apply(CManualInspectionWorkspace w, string revision) { var s = w.CreateApplySnapshot(false); w.CommitApplied(s, revision, "QA successful synthetic writer"); }
    private static void Offset(ST_MANUAL_OFFSET value, double x, double y) { Equal(value.XUm, x); Equal(value.YUm, y); }
    private static void Equal(double actual, double expected) { if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-8) throw new InvalidOperationException($"Expected {expected:R}, actual {actual:R}"); }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    private static void Throws(Action action) { var thrown = false; try { action(); } catch { thrown = true; } if (!thrown) throw new InvalidOperationException("Expected a rejection, but call succeeded"); }
    private static void Check(string name, Action test)
    {
        try { test(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failed++; Results.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static object[] SourceInputs([CallerFilePath] string caller = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "..", ".."));
        return Directory.EnumerateFiles(Path.Combine(root, "Drilling.Common", "Review", "ManualInspection"), "*.cs")
            .OrderBy(p => p, StringComparer.Ordinal).Select(p => (object)new { path = Path.GetRelativePath(root, p).Replace('\\', '/'),
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() }).ToArray();
    }
}
