namespace Drilling.Common.Review
{
    using Drilling.Common.Recipe;
    using Drilling.Common.Interface.Vision;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using Drilling.Common.Managers;

    /// <summary>Stage-handshake actions. No UI dependencies; only the next recipe load consumes the new offset file.</summary>
    public sealed class CAutoFineReviewCoordinator(
        CReviewManager review, 
        CReviewOffsetFileBase offsets, 
        Func<CancellationToken, Task<ST_AUTO_REVIEW_INPUT>> capture, 
        Func<IReadOnlyList<ST_REVIEW_PLAN_POINT>, CancellationToken, Task<IReadOnlyList<ST_VISION_MARK_FIND>>>? simulationMeasurement = null
    )
    {
        private ST_AUTO_FINE_PLAN? _prepared;
        private ST_FINE_REVIEW_SNAPSHOT? _result;
        private bool _saved;
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        public string Outcome { get; private set; } = "검사 대기";
        // True only after all requested identities were validated and the diagnostic
        // result file was saved. Quality NG is still an inspection completion.
        public bool FollowUpInspectionCompleted => _prepared?.ZeroLineFollowUp == true
            && _result is { State: EN_REVIEW_SEQUENCE_STATE.Completed } result
            && result.ResultSaveError.Length == 0 && !string.IsNullOrWhiteSpace(result.ResultFilePath);

        public async Task Prepare(CancellationToken token)
        {
            _prepared = null;
            _result = null;
            _saved = false;
            var input = await capture(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _prepared = input.ZeroLineFollowUp ? review.CreateZeroLineFollowUpPlan(input, token) : review.CreateAutoFinePlan(input, token);
            Outcome = input.ZeroLineFollowUp
                ? $"{input.GlassId} / {input.Recipe.Name} / 0선 FollowUp · 맨 위 Cell 가운데 행 · Beam 11 / IOF 1 Line · 재시도 없음 · 검사만 수행 / 보정 미적용 · 계획 확정"
                : $"{input.GlassId} / {input.Recipe.Name} / Beam 11 / {input.Options.Method} / {input.Options.LineCount} Line / " + $"즉시 재시도 {(input.Options.ImmediateRetry ? "ON" : "OFF")} / {input.Options.CountMismatchRetries}회 · 계획 확정";
            review.RecordAutoFineLog("AUTO_REVIEW_PLAN", Outcome);
        }

        public async Task Measure(CancellationToken token)
        {
            var plan = _prepared ?? throw new InvalidOperationException("AUTO Review 계획이 없습니다.");
            _result = null;
            var result = await review.StartFineCorrection(plan.Request, _ =>
            {
            }, token, simulationMeasurement).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.State != EN_REVIEW_SEQUENCE_STATE.Completed)
                throw new InvalidOperationException($"AUTO Review가 정상 완료되지 않았습니다: {result.Message}");
            if (result.ResultSaveError.Length > 0 || string.IsNullOrWhiteSpace(result.ResultFilePath))
                throw new IOException($"AUTO Review 결과 저장을 확인할 수 없습니다: {result.ResultSaveError}");
            var measured = ValidateMeasurements(plan, result);
            var ng = measured.Count(point => point.State == EN_REVIEW_POINT_STATE.Ng);
            var method = plan.Request.Method == EN_FINE_INSPECTION_METHOD.Flying ? "IOF" : "PtoP";
            review.RecordAutoFineLog("AUTO_REVIEW_RESULT", $"FINE {method} 검사 결과 · {measured.Length:N0}개 검사 / OK {measured.Length - ng:N0} / NG {ng:N0} · Beam {ST_AUTO_REVIEW_OPTIONS.BeamNo}");
            if (plan.ZeroLineFollowUp)
            {
                Outcome = $"0선 FollowUp IOF 1 Line · 검사 {measured.Length:N0}개 완료 / OK {measured.Length - ng:N0} / NG {ng:N0}개 · 결과 저장 완료 / 보정 미적용";
                result = result with { Message = Outcome };
                review.FineMonitor.Publish(result);
                review.RecordAutoFineLog("ZERO_LINE_FOLLOWUP_RESULT", Outcome);
            }
            _result = result;
            if (ng > 0)
            {
                if (!plan.ZeroLineFollowUp)
                    Outcome = $"검사 {measured.Length:N0}개 완료 / NG {ng:N0}개 · 보정 전체 미적용 · 기존 Review Offset 유지";
                review.RecordAutoFineLog("AUTO_REVIEW_NG", Outcome);
                // Immediate retries are completed by StartFineCorrection. Any remaining NG,
                // including when retry is OFF, fails REVIEW_MEASURE and triggers its CSV HEAVY.
                throw new InvalidDataException(Outcome);
            }
        }

        public async Task Save(CancellationToken token)
        {
            await _saveGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // A caller that waited for another save must recheck completion before reading offsets.
                if (_saved)
                    return;
                await SaveCore(token).ConfigureAwait(false);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        private async Task SaveCore(CancellationToken token)
        {
            var plan = _prepared ?? throw new InvalidOperationException("AUTO Review 계획이 없습니다.");
            var result = _result ?? throw new InvalidOperationException("AUTO Review 검사 결과가 없습니다.");
            token.ThrowIfCancellationRequested();
            if (result.State != EN_REVIEW_SEQUENCE_STATE.Completed || result.ResultSaveError.Length > 0 || string.IsNullOrWhiteSpace(result.ResultFilePath))
                throw new InvalidOperationException("정상 완료 및 결과 파일 저장이 확인된 검사만 보정에 사용할 수 있습니다.");
            var measured = ValidateMeasurements(plan, result);
            var ng = measured.Count(point => point.State == EN_REVIEW_POINT_STATE.Ng);
            if (ng > 0)
            {
                // Inspection completion is different from a quality OK. Keep every old offset when any Shot is NG.
                Outcome = $"검사 {measured.Length:N0}개 완료 / NG {ng:N0}개 · 보정 전체 미적용 · 기존 Review Offset 유지";
                review.RecordAutoFineLog("AUTO_REVIEW_NG", Outcome);
                throw new InvalidDataException(Outcome);
            }

            if (plan.ZeroLineFollowUp)
            {
                // This diagnostic inspection must not even load the current offset file.
                // Stage may complete the Review handshake after the result was saved/displayed.
                _saved = true;
                Outcome = $"0선 FollowUp IOF 1 Line · 전체 OK / {measured.Length:N0}개 결과 저장 완료 · 검사만 수행 / 기존 Review Offset 유지";
                review.RecordAutoFineLog("ZERO_LINE_FOLLOWUP_COMPLETE", Outcome);
                return;
            }

            var deltas = CReviewManager.CalculateFineOffsets(plan.OffsetTargets, measured, plan.Request.Plan.VisionFlipType);
            var latest = await offsets.LoadLatest(plan.Request.Plan.RecipeId, token).ConfigureAwait(false);
            if (!latest.RecipeId.Equals(plan.Request.Plan.RecipeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("기존 Review Offset의 Recipe ID가 일치하지 않습니다.");
            var rows = latest.Rows.ToDictionary(row => (row.CellId, Normalize(row.ShotId)));
            foreach (var delta in deltas)
            {
                token.ThrowIfCancellationRequested();
                var shot = delta.Shot;
                var key = (shot.CellNo, Normalize(CReviewShotNameFormatter.ToMatrixName(shot.ShotNo, shot.PixelCountX)));
                var previous = rows.GetValueOrDefault(key);
                var design = previous is null ? (X: 0d, Y: 0d) : CReviewOffsetData.ToDesign(plan.Parameters, shot.HeadNo, previous.ReviewOffsetX, previous.ReviewOffsetY, previous.IsDesignCoordinate);
                if (!double.IsFinite(design.X) || !double.IsFinite(design.Y))
                    throw new InvalidDataException("기존 Review Offset 값이 유효하지 않습니다.");
                rows[key] = new(shot.CellNo, key.Item2, CAlignShotOffsetData.RoundValue(design.X + delta.X), CAlignShotOffsetData.RoundValue(design.Y + delta.Y))
                {
                    IsDesignCoordinate = true
                };
            }

            var saved = await offsets.SaveNew(plan.Request.Plan.RecipeId, rows.Values.OrderBy(row => row.CellId).ThenBy(row => row.ShotId).ToArray(), token).ConfigureAwait(false);
            _saved = true;
            review.FineMonitor.MarkOffsetSaved(result.RunId, saved.FileName);
            Outcome = $"전체 OK · {deltas.Count:N0} Shots FINE 누적 보정 저장 · 다음 Glass에 적용 / {saved.FileName}";
            review.RecordAutoFineLog("AUTO_REVIEW_OFFSET_SAVED", Outcome);
        }

        private static ST_REVIEW_PLAN_POINT[] ValidateMeasurements(ST_AUTO_FINE_PLAN plan, ST_FINE_REVIEW_SNAPSHOT result)
        {
            // Compare only the requested inspection rows. OffsetTargets intentionally also contains
            // unmeasured rows that receive per-column FINE interpolation after a complete OK inspection.
            var expected = plan.Request.Plan.Points;
            var measured = result.Lines.SelectMany(line => line.Points).ToArray();
            if (!string.Equals(result.RecipeId, plan.Request.Plan.RecipeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AUTO Review 결과의 Recipe ID가 검사 계획과 일치하지 않습니다.");
            if (expected.Count == 0 || measured.Length != expected.Count)
                throw new InvalidOperationException($"AUTO Review 검사점 개수가 일치하지 않습니다: 계획 {expected.Count:N0}개 / 결과 {measured.Length:N0}개.");
            var pending = new HashSet<(string Key, int Head, int Cell, int Shot, int Beam)>();
            foreach (var point in expected)
            {
                if (string.IsNullOrWhiteSpace(point.ShotKey) || !pending.Add(Identity(point)))
                    throw new InvalidOperationException("AUTO Review 검사 계획에 비어 있거나 중복된 검사점이 있습니다.");
            }

            foreach (var point in measured)
            {
                // Retry can reverse the acquisition order; identities, rather than array order, must match.
                if (string.IsNullOrWhiteSpace(point.ShotKey) || !pending.Remove(Identity(point)))
                    throw new InvalidOperationException($"AUTO Review 결과가 중복되었거나 검사 계획과 일치하지 않습니다: " + $"Head {point.HeadNo} / Cell {point.CellNo} / Shot {point.ShotNo} / {point.ShotKey} / Beam {point.BeamNo}.");
                if (point.State is not (EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng))
                    throw new InvalidOperationException("미검사 또는 유효하지 않은 결과가 있어 Review COMPLETE 및 보정 저장을 진행할 수 없습니다.");
            }

            return measured;
            static (string Key, int Head, int Cell, int Shot, int Beam) Identity(ST_REVIEW_PLAN_POINT point) => (point.ShotKey.Trim().ToUpperInvariant(), point.HeadNo, point.CellNo, point.ShotNo, point.BeamNo);
        }

        private static string Normalize(string text) => text.Trim().Replace("-", "_", StringComparison.Ordinal).Replace(".", "_", StringComparison.Ordinal).ToUpperInvariant();
#region AUTO FINE - 계획 생성
        public static ST_AUTO_FINE_PLAN BuildPlan(ST_AUTO_REVIEW_INPUT input, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            // A zero-line diagnostic is exactly one IOF acquisition, independent of
            // the operator's normal multi-line/PtoP/retry choices for later Review runs.
            if (input.ZeroLineFollowUp)
                input = input with { Options = new(1, EN_FINE_INSPECTION_METHOD.Flying, 1, false) };
            if (input.Options.LineCount is not (1 or 3 or 5 or 7) || input.Options.Method is not (EN_FINE_INSPECTION_METHOD.Flying or EN_FINE_INSPECTION_METHOD.PtoP) || input.Options.CountMismatchRetries is < 1 or > 3)
                throw new InvalidOperationException("AUTO Review 검사 방식 또는 라인 수가 올바르지 않습니다.");
            var parameters = input.Recipe.Parameters.Where(item => item.Key.Length > 0).GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
            foreach (var setting in settings.Where(item => item.Key.Length > 0))
                // FollowUp inspects the same frozen Glass/Recipe/Head geometry that
                // produced the zero-line result. Later settings only fill missing keys.
                if (!input.ZeroLineFollowUp || !parameters.ContainsKey(setting.Key))
                    parameters[setting.Key] = setting.Value;
            var frozen = new ReadOnlyDictionary<string, string>(parameters);
            var effectiveRecipe = input.Recipe with
            {
                Parameters = parameters.Select(pair => new ST_RECIPE_PARAMETER(pair.Key, pair.Value, "", "", pair.Value, Key: pair.Key)).ToArray()
            };
            var calculate = CReviewManager.CreateShotBeamPositionCalculator(effectiveRecipe, CScannerAxisTransform.ReadStageDirection(frozen));
            var masking = CRecipeShotMaskingPlan.Build(frozen);
            var shots = CShotCoordinatePlanBuilder.Build(frozen).Shots;
            var all = shots.Select(shot => new ST_REVIEW_PLAN_POINT(
                shot.SequenceNo, 
                shot.ShotKey, 
                shot.HeadNo, 
                shot.CellNo, 
                shot.CellShotNo, 
                shot.GroupCountX, 
                shot.GroupCountY, 
                shot.HeadNo > 0 && !masking.EvaluateShot(shot).ShouldSkipLaser, 
                shot.DesignX, 
                shot.DesignY, 
                0, 
                0, 
                0, 
                0, 
                EN_REVIEW_POINT_STATE.Ready, 
                "WAIT"
            ) { BeamNo = ST_AUTO_REVIEW_OPTIONS.BeamNo }).ToArray();
            var targets = all.Where(point => point.Use).ToArray();
            if (targets.Length == 0)
                throw new InvalidOperationException("AUTO Review 검사 대상 Shot이 없습니다.");
            var source = new ST_REVIEW_PLAN(
                input.Recipe.Id, 
                targets.Select(point => point.CellNo).Distinct().Count(), 
                Tolerance("REVIEW_TOLERANCE_X"), 
                Tolerance("REVIEW_TOLERANCE_Y"), 
                CReviewManager.ParseVisionFlipType(parameters.GetValueOrDefault("VisionXFlip"), parameters.GetValueOrDefault("VisionYFlip"), parameters.GetValueOrDefault("VisionXyFlip")), 
                DateTimeOffset.Now, 
                all
            );
            var selected = CReviewRulePlanBuilder.Build(source, new(EN_REVIEW_CORRECTION_RULE.Fine, EN_REVIEW_CORRECTION_SCOPE.HeadCell, ST_AUTO_REVIEW_OPTIONS.BeamNo, "", EN_REVIEW_REFERENCE_LINE.First, input.Options.LineCount)).ShotKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var topCells = input.ZeroLineFollowUp ? FindTopmostCells(frozen) : null;
            var models = all.Select(point => point.CellNo).Distinct().ToDictionary(cell => cell, cell => CRecipeModelPixelCountReader.ReadCellModelType(parameters, cell));
            var modelCounts = models.Values.GroupBy(model => model).ToDictionary(group => group.Key, group => group.Count());
            var lines = new List<ST_FINE_REVIEW_LINE>();
            foreach (var model in targets.Where(point => selected.Contains(point.ShotKey) && (topCells is null || topCells.Contains(point.CellNo))).GroupBy(point => models[point.CellNo]).OrderByDescending(group => modelCounts[group.Key]).ThenBy(group => group.Key))
            {
                var row = 0;
                foreach (var line in model.GroupBy(point => Math.Round(point.DesignY, 6)).OrderBy(group => group.Key))
                {
                    var leftToRight = row++ % 2 == 0;
                    var points = (leftToRight ? line.OrderBy(point => point.DesignX) : line.OrderByDescending(point => point.DesignX)).ThenBy(point => point.CellNo).ThenBy(point => point.ShotNo).Select(point =>
                    {
                        var position = calculate(point, ST_AUTO_REVIEW_OPTIONS.BeamNo);
                        return point with
                        {
                            ReviewTargetX = position.ReviewX,
                            ReviewTargetY = position.StageY
                        };
                    }).ToArray();
                    lines.Add(new($"B11:{model.Key}:{line.Key.ToString("R", CultureInfo.InvariantCulture)}", lines.Count + 1, model.Key, Array.AsReadOnly(points)));
                }
            }

            if (lines.Count == 0)
                throw new InvalidOperationException("AUTO FINE 검사 라인을 만들 수 없습니다.");
            if (input.ZeroLineFollowUp)
            {
                var points = lines.SelectMany(line => line.Points).OrderBy(point => point.DesignX).ThenBy(point => point.CellNo).ThenBy(point => point.ShotNo).ToArray();
                // Different middle-row heights cannot be silently expanded to multiple
                // acquisitions or replaced with PtoP when the request is one IOF line.
                if (points.Max(point => point.ReviewTargetY) - points.Min(point => point.ReviewTargetY) > .0005
                    || points.Max(point => point.DesignY) - points.Min(point => point.DesignY) > .000001)
                    throw new InvalidOperationException("맨 위 Cell들의 가운데 행이 서로 다른 Y 위치여서 0선 FollowUp IOF 한 줄을 만들 수 없습니다. Cell 배치와 Beam 좌표를 확인해 주세요.");
                var modelNos = lines.Select(line => line.ModelNo).Distinct().ToArray();
                lines = [new("ZERO_FOLLOWUP:TOP_MIDDLE:B11", 1, modelNos.Length == 1 ? modelNos[0] : 0, Array.AsReadOnly(points))];
            }
            var plan = source with
            {
                Points = Array.AsReadOnly(lines.SelectMany(line => line.Points).ToArray()),
                InspectionMethod = input.ZeroLineFollowUp ? "IOF" : source.InspectionMethod
            };
            var request = new ST_FINE_REVIEW_REQUEST(plan, lines.AsReadOnly(), input.Options.Method)
            {
                StopOnNgLine = false,
                CountMismatchRetries = input.ZeroLineFollowUp ? 0 : input.Options.CountMismatchRetries,
                ImmediateRetry = input.Options.ImmediateRetry,
                Context = new(input.Recipe.Name, input.GlassId, "AUTO", input.Options.LineCount, frozen) { ZeroLineFollowUp = input.ZeroLineFollowUp }
            };
            return new(request, input.ZeroLineFollowUp ? Array.Empty<ST_REVIEW_PLAN_POINT>() : Array.AsReadOnly(targets), frozen) { ZeroLineFollowUp = input.ZeroLineFollowUp };
            double Tolerance(string key) => double.TryParse(parameters.GetValueOrDefault(key, "0.03"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : throw new InvalidOperationException($"{key} 설정을 확인해 주세요.");
        }

        private static HashSet<int> FindTopmostCells(IReadOnlyDictionary<string, string> parameters)
        {
            double Read(string key, double fallback = 0) => double.TryParse(parameters.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
            var ranges = new List<(int Cell, double MinimumY)>();
            var cellCount = (int)Read("MAX_CELL_NUMBER");
            for (var cellNo = 1; cellNo <= cellCount; cellNo++)
            {
                var pattern = CRecipeModelPixelCountReader.ReadPatternForCell(parameters, cellNo);
                var definition = new ST_CELL_PATTERN_DEFINITION(cellNo, Read($"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X"), Read($"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y"), Read($"CELL{cellNo}_ROTATION"), Read("PIXEL_SIZE", .09), pattern.PixelCountX, pattern.PixelCountY, pattern.ProcessPitch, (int)Read("SPLITED_BEAM_COUNT", 1), Read("AK_MARGIN_X"), Read("AK_MARGIN_Y"));
                var layout = CCellPatternCalculator.Calculate(definition, includeShotCenters: false);
                if (!layout.IsValid)
                    throw new InvalidOperationException(layout.ValidationMessage);
                // Four corners suffice for a rotated rectangular Shot lattice. Geometry
                // includes masked/inactive points, so exclusions cannot select a lower Cell row.
                var minimumY = new[] { (0, 0), (layout.GroupCountX - 1, 0), (0, layout.GroupCountY - 1), (layout.GroupCountX - 1, layout.GroupCountY - 1) }
                    .Min(corner => CCellPatternCalculator.CalculateShotCenter(definition, corner.Item1, corner.Item2, layout).DesignY);
                ranges.Add((cellNo, minimumY));
            }
            if (ranges.Count == 0)
                throw new InvalidOperationException("0선 FollowUp의 Cell 배치를 확인할 수 없습니다.");
            var firstY = ranges.Min(range => range.MinimumY);
            return ranges.Where(range => Math.Abs(range.MinimumY - firstY) <= .000001).Select(range => range.Cell).ToHashSet();
        }
#endregion
    }

    public sealed record ST_AUTO_REVIEW_OPTIONS(int LineCount = 1, EN_FINE_INSPECTION_METHOD Method = EN_FINE_INSPECTION_METHOD.Flying, int CountMismatchRetries = 1, bool ImmediateRetry = false)
    {
        public const int BeamNo = 11;
    }

    public sealed record ST_AUTO_REVIEW_INPUT(ST_RECIPE_DEFINITION Recipe, string GlassId, ST_AUTO_REVIEW_OPTIONS Options)
    {
        public bool ZeroLineFollowUp { get; init; }
    }
    public sealed record ST_AUTO_FINE_PLAN(ST_FINE_REVIEW_REQUEST Request, IReadOnlyList<ST_REVIEW_PLAN_POINT> OffsetTargets, IReadOnlyDictionary<string, string> Parameters)
    {
        public bool ZeroLineFollowUp { get; init; }
    }
    /// <summary>Frozen run metadata. AUTO supplies its glass ID and Run Option choices when it starts FINE.</summary>
    public sealed record ST_FINE_REVIEW_CONTEXT(string RecipeName, string GlassId, string Origin, int LinesPerCell, IReadOnlyDictionary<string, string> Parameters)
    {
        public bool ZeroLineFollowUp { get; init; }
    }
    public sealed record ST_FINE_REVIEW_MONITOR(
        Guid RunId, 
        long Revision, 
        ST_FINE_REVIEW_REQUEST Request, 
        ST_FINE_REVIEW_SNAPSHOT Progress, 
        int StageDeviceNo, 
        DateTimeOffset StartedAt, 
        string OffsetFile = ""
    );
    /// <summary>Latest immutable FINE snapshot only. No device I/O, disk reads, UI callbacks or queued frames.</summary>
    public sealed class CFineReviewMonitorStore
    {
        private readonly object _gate = new();
        private ST_FINE_REVIEW_MONITOR? _latest;
        private long _revision;
        public ST_FINE_REVIEW_MONITOR? GetSnapshot()
        {
            lock (_gate)
                return _latest;
        }

        internal Guid Begin(ST_FINE_REVIEW_REQUEST request, int stageDeviceNo)
        {
            lock (_gate)
                if (_latest is { } staged && staged.RunId == request.PreparedMonitorRunId && staged.Progress.State == EN_REVIEW_SEQUENCE_STATE.Idle)
                {
                    _latest = staged with
                    {
                        Revision = ++_revision,
                        Progress = staged.Progress with
                        {
                            State = EN_REVIEW_SEQUENCE_STATE.Running,
                            Message = "FINE 검사 시작"
                        }
                    };
                    return staged.RunId;
                }

            return Replace(request, stageDeviceNo, EN_REVIEW_SEQUENCE_STATE.Running, "검사 준비 중");
        }

        internal Guid Stage(ST_FINE_REVIEW_REQUEST request, int stageDeviceNo) => Replace(request, stageDeviceNo, EN_REVIEW_SEQUENCE_STATE.Idle, "검사 계획 확정 · Stage Review START 대기");
        private Guid Replace(ST_FINE_REVIEW_REQUEST request, int stageDeviceNo, EN_REVIEW_SEQUENCE_STATE state, string message)
        {
            var context = request.Context;
            if (context is not null)
                request = request with
                {
                    Context = context with
                    {
                        Parameters = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(context.Parameters, StringComparer.OrdinalIgnoreCase))
                    }
                };
            // Copy collection containers once. Published line/point records are immutable and reused between revisions.
            request = request with
            {
                Plan = request.Plan with
                {
                    Points = Array.AsReadOnly(request.Plan.Points.ToArray())
                },
                Lines = Array.AsReadOnly(request.Lines.Select(line => line with { Points = Array.AsReadOnly(line.Points.ToArray()) }).ToArray())
            };
            var id = Guid.NewGuid();
            var initial = new ST_FINE_REVIEW_SNAPSHOT(request.Plan.RecipeId, request.Lines, state, message)
            {
                RunId = id
            };
            lock (_gate)
                _latest = new(id, ++_revision, request, initial, stageDeviceNo, DateTimeOffset.Now);
            return id;
        }

        internal void Publish(ST_FINE_REVIEW_SNAPSHOT snapshot)
        {
            lock (_gate)
                if (_latest is { } latest && latest.RunId == snapshot.RunId)
                    _latest = latest with
                    {
                        Revision = ++_revision,
                        Progress = snapshot
                    };
        }

        public void MarkOffsetSaved(Guid runId, string file)
        {
            lock (_gate)
                if (_latest is { } latest && latest.RunId == runId && !string.IsNullOrWhiteSpace(file))
                    _latest = latest with
                    {
                        Revision = ++_revision,
                        OffsetFile = file
                    };
        }
    }
}
