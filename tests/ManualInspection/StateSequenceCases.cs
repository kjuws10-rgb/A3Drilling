using Drilling.Common.Review.ManualInspection;

internal static partial class Program
{
    // This is a composition regression: a single workspace survives 6 different measurement,
    // cancellation, persistence and rollback cycles. It does not create device APIs or raw packets.
    private static void RunStateSequenceChecks()
    {
        Check("seeded_state_sequences_preserve_shared_shot_values_and_consumed_raw_32x6", () =>
        {
            for (var seed = 0; seed < 32; seed++)
            {
                var random = new Random(0x3A001 + seed);
                var w = Workspace([Point(1), Point(1, beam: 1), Point(2), Point(3)]);
                var consumed = new List<KeyValuePair<string, string>>();
                for (var cycle = 0; cycle < 6; cycle++)
                {
                    var prefix = $"S{seed}/C{cycle}";
                    // Undo invalidates the old residual on the restored coordinate basis. A later
                    // cycle must obtain a genuinely new representative before exercising copy.
                    if(!w.GetPoint(Key(2)).IsDirectMeasurement)
                        w.RecordMeasurement(Key(2),Measurement(NextError(random),-NextError(random),raw:prefix+"/COPY-REFERENCE"));
                    var previousTarget = w.GetPoint(Key(3));
                    var beforeCopy = OffsetState(w);
                    var copy = w.PreviewReferenceCopy(Key(2), [Key(3)]);
                    w.ConfirmReferenceCopy(copy, [Key(3)], prefix + " temporary representative");
                    True(w.GetPoint(Key(3)).ProxySource is not null);
                    w.UndoReferenceCopy();
                    True(w.GetPoint(Key(3)) == previousTarget && OffsetState(w) == beforeCopy);

                    // Stop after one point, persist that incomplete state and complete only the
                    // residual point. A retry never grants the original automatic-correction right.
                    var operation = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MeasureAndCorrect, [Key(1), Key(2)]));
                    w.RecordMeasurement(Key(1), Measurement(NextError(random), -NextError(random), raw: prefix + "/RAW1"), operation.Id);
                    w.MarkOperationPointCompleted(operation.Id, Key(1));
                    var stopped = w.CompleteOperation(operation.Id, EN_MANUAL_OPERATION_STATE.Stopped);
                    True(!stopped.AutoCorrectionAllowed && w.HasIncompleteMeasurements);
                    w = RoundTripState(w);
                    True(w.HasIncompleteMeasurements && w.Operation!.RemainingKeys.SequenceEqual([Key(2)]));
                    var retry = w.BeginOperation(w.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, w.Operation!.RemainingKeys, isRetry: true));
                    w.RecordMeasurement(Key(2), Measurement(-NextError(random), NextError(random), raw: prefix + "/RAW2"), retry.Id);
                    w.MarkOperationPointCompleted(retry.Id, Key(2));
                    True(!w.CompleteOperation(retry.Id).AutoCorrectionAllowed && !w.HasIncompleteMeasurements);

                    w.BuildDirectCandidate([Key(1)]);
                    True(w.ValidateCandidate(false).Ok);
                    if (random.Next(2) == 0)
                    {
                        var beforeAbort = OffsetState(w);
                        var reserved = w.CreateApplySnapshot(false);
                        True(w.IsBusy && reserved.ConsumedMeasurements.ContainsKey(Key(1, beam: 1)));
                        w.AbortApply(reserved);
                        True(!w.IsBusy && OffsetState(w) == beforeAbort);
                        foreach (var token in reserved.ConsumedMeasurements.Where(pair => pair.Key == Key(1)))
                            True(!w.WasConsumed(token.Key, token.Value));
                        w.BuildDirectCandidate([Key(1)]); True(w.ValidateCandidate(false).Ok);
                    }
                    var first = w.CreateApplySnapshot(false);
                    w.CommitApplied(first, prefix + "/APPLY1", "synthetic state-sequence writer");
                    consumed.AddRange(first.ConsumedMeasurements);
                    var expectedRestored = OffsetState(w);
                    True(w.ActiveOffsets[Key(1)] == w.ActiveOffsets[Key(1, beam: 1)]);
                    w.BuildDirectCandidate([Key(1)]); True(!w.ValidateCandidate(false).Ok);

                    w.BuildDirectCandidate([Key(2)]); True(w.ValidateCandidate(false).Ok);
                    var second = w.CreateApplySnapshot(false);
                    w.CommitApplied(second, prefix + "/APPLY2", "synthetic state-sequence writer");
                    consumed.AddRange(second.ConsumedMeasurements);
                    var restore = w.CreateRestorePreviousSnapshot();
                    if (random.Next(2) == 0)
                    {
                        var beforeAbort = OffsetState(w); w.AbortRestore(restore);
                        True(OffsetState(w) == beforeAbort && !w.IsBusy);
                        restore = w.CreateRestorePreviousSnapshot();
                    }
                    w.CommitRestored(restore, prefix + "/RESTORE", "synthetic rollback after independent Shot apply");
                    True(OffsetState(w) == expectedRestored);
                    w = RoundTripState(w);
                    foreach (var token in consumed) True(w.WasConsumed(token.Key, token.Value));
                    Throws(()=>w.BuildDirectCandidate([Key(2)]));True(!w.GetPoint(Key(2)).HasFiniteError);

                    // An external result can replace the displayed observation after the real
                    // revision is reconciled. Its presence must not change B or authorize raw reuse.
                    var beforeImport = OffsetState(w);
                    var beforeHistory = w.CaptureSession().History.Count;
                    var observed = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(cycle + seed * 10);
                    var imported = w.GetPoint(Key(3)) with
                    {
                        Source = "IMPORTED", Judge = cycle % 2 == 0 ? "OK" : "NG",
                        ErrorXUm = NextError(random), ErrorYUm = -NextError(random),
                        OriginalMeasurement = null, ProxySource = null
                    };
                    True(w.MergeImportedObservations([imported], new Dictionary<string, DateTimeOffset> { [Key(3)] = observed }) == 1);
                    True(OffsetState(w) == beforeImport && !w.GetPoint(Key(3)).IsDirectMeasurement);
                    Throws(() => w.BuildDirectCandidate([Key(3)]));
                    True(w.CaptureSession().History.Count > beforeHistory);
                    foreach (var token in consumed) True(w.WasConsumed(token.Key, token.Value));
                    True(w.ActiveOffsets[Key(1)] == w.ActiveOffsets[Key(1, beam: 1)]);
                    True(w.ActiveOffsets.Values.All(value => value.IsFinite));
                    for (var i = 1; i < w.Revisions.Count; i++) True(w.Revisions[i].ParentId == w.Revisions[i - 1].Id);
                }
            }
        });

        Check("ng_proxy_session_remeasure_reference_preserves_copy_snapshot_until_explicit_recopy", () =>
        {
            var w = Workspace([Point(1, 4, -3, "NG"), Point(2, judge: "ERROR"), Point(3)]);
            var preview = w.PreviewReferenceCopy(Key(1), [Key(2)]);
            w.ConfirmReferenceCopy(preview, [Key(2)], "synthetic NG representative");
            True(w.GetPoint(Key(2)).Judge == "NG" && !w.ValidateCandidate(true).Ok);
            var oldProxy = w.GetPoint(Key(2)).ProxySource;
            w = RoundTripState(w);
            w.RecordMeasurement(Key(1), Measurement(2, -1, raw: "NEW_OK_REFERENCE"));
            True(w.GetPoint(Key(2)).ProxySource == oldProxy && w.GetPoint(Key(2)).Judge == "NG");
            var freshCopy = w.PreviewReferenceCopy(Key(1), [Key(2)]);
            w.ConfirmReferenceCopy(freshCopy, [Key(2)], "explicit current representative");
            True(w.GetPoint(Key(2)).Judge == "OK" && w.GetPoint(Key(2)).ProxySource!.ReferenceMeasurementToken != oldProxy!.ReferenceMeasurementToken);
            True(w.ValidateCandidate(true).Ok);
            var snapshot = w.CreateApplySnapshot(true); w.CommitApplied(snapshot, "NG_COPY_THEN_OK", "synthetic proxy writer");
            w = RoundTripState(w);
            var restore = w.CreateRestorePreviousSnapshot(); w.CommitRestored(restore, "NG_COPY_ROLLBACK", "synthetic proxy rollback");
            Offset(w.ActiveOffsets[Key(2)], 0, 0);
            True(w.GetPoint(Key(2)).Judge == "UNMEASURED" && w.GetPoint(Key(2)).ProxySource is null && w.GetPoint(Key(2)).OriginalMeasurement is not null);
        });
    }

    private static double NextError(Random random) => random.Next(10, 1500) / 1000d;
    private static string OffsetState(CManualInspectionWorkspace w) => string.Join("|", w.ActiveOffsets.OrderBy(pair => pair.Key)
        .Select(pair => $"{pair.Key}:{pair.Value.XUm:R},{pair.Value.YUm:R}"));
    private static CManualInspectionWorkspace RoundTripState(CManualInspectionWorkspace w)
    {
        var fresh = new CManualInspectionWorkspace();
        fresh.RestoreSessionJson(w.ExportSessionJson(), w.Context!, w.Points, w.ActiveOffsets, InsideField);
        True(OffsetState(fresh) == OffsetState(w) && !fresh.IsBusy);
        return fresh;
    }
}
