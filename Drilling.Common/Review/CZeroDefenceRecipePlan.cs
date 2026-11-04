namespace Drilling.Common.Review
{
    using System.Globalization;
    using Drilling.Common.Managers;
    using Drilling.Common.Recipe;
    using Drilling.Common.Motion;
    using Drilling.Common.InterLock;

    /// <summary>A recipe/settings snapshot. Creating and validating it never moves a device.</summary>
    public sealed class CZeroDefenceRecipePlan
    {
        public const string CountKey = "ZERO_DEFENCE_POINT_COUNT";
        public static string Key(int head, string field) => $"ZD_H{head:00}_{field}";
        private readonly CRecipeShotMaskingPlan _mask;
        private readonly Dictionary<(int Cell, int Shot), ST_HEAD_SHOT_POINT> _shots;
        private readonly Func<ST_REVIEW_PLAN_POINT, int, ST_REVIEW_BEAM_POSITION> _beamPosition;
        public CZeroDefenceRecipePlan(ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            Recipe = recipe;
            var parameters = recipe.Parameters.Where(p => p.Key.Length > 0).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
            foreach (var setting in settings.Where(s => s.Key.Length > 0))
                parameters[setting.Key] = setting.Value;
            parameters["HEAD_COUNT"] = "8";
            Parameters = parameters;
            Plan = CShotCoordinatePlanBuilder.Build(parameters);
            _shots = Plan.Shots.ToDictionary(p => (p.CellNo, p.CellShotNo));
            _mask = CRecipeShotMaskingPlan.Build(parameters);
            var motion = ST_REVIEW_MOTION_PARAMETERS.Read(settings);
            Profile = new(Read("STAGE_START_POS_Y", 3960), Read("STAGE_SCAN_DIRECTION_Y", -1), Read("STAGE_SPEED", 80), motion.Speed, motion.Acceleration);
            var geometryRecipe = recipe with
            {
                Parameters = parameters.Select(p => new ST_RECIPE_PARAMETER(p.Key, p.Value, "", "", "", Key: p.Key)).ToArray()
            };
            _beamPosition = CReviewManager.CreateShotBeamPositionCalculator(geometryRecipe, Profile.StageDirection);
        }

        public ST_RECIPE_DEFINITION Recipe { get; }
        public IReadOnlyDictionary<string, string> Parameters { get; }
        public ST_SHOT_COORDINATE_PLAN Plan { get; }
        public ST_ZERO_DEFENCE_TIMING_PROFILE Profile { get; }
        public int Count => Int(CountKey, 2);
        public bool HasSelections => Enumerable.Range(1, 8).Any(h => new[] { "CELL", "SHOT", "BEAM" }.Any(f => Int(Key(h, f)) != 0));

        public int Int(string key, int fallback = 0)
        {
            if (!Parameters.TryGetValue(key, out var value))
                return fallback;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new InvalidOperationException($"{key}: 정수 값이 아닙니다.");
            return number;
        }

        private double Read(string key, double fallback)
        {
            if (!Parameters.TryGetValue(key, out var text))
                return fallback;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException($"{key}: 유한한 숫자가 아닙니다.");
            return value;
        }

        public bool CanProcess(ST_HEAD_SHOT_POINT point) => !_mask.EvaluateShot(point).ShouldSkipLaser;
        public ST_RECIPE_SHOT_MASKING_RESULT Mask(ST_HEAD_SHOT_POINT point) => _mask.EvaluateShot(point);
        public ST_ZERO_DEFENCE_POINT GetPoint(int head, int cell, int shot, int beam)
        {
            if (!_shots.TryGetValue((cell, shot), out var point) || point.HeadNo != head)
                throw new InvalidOperationException($"H{head:00}: 해당 Head가 담당하는 Shot이 아닙니다.");
            if (!CanProcess(point))
                throw new InvalidOperationException($"H{head:00}: Masking 또는 Chess로 제외된 Shot입니다.");
            var target = _beamPosition(new(point.SequenceNo, point.ShotKey, head, cell, shot, 0, 0, true, point.DesignX, point.DesignY, 0, 0, 0, 0, EN_REVIEW_POINT_STATE.Ready, "WAIT"), beam);
            var beamDesignX = target.ReviewX - Read("REVIEW_CAMERA_ALIGN_KEY_POS_X", 0) + Read("AK_MARGIN_X", 55);
            var beamDesignY = (target.StageY - Read("REVIEW_CAMERA_ALIGN_KEY_POS_Y", 0)) / Profile.StageDirection + Read("AK_MARGIN_Y", 45);
            if (!double.IsFinite(beamDesignX) || !double.IsFinite(beamDesignY) || beamDesignX < -1e-8 || beamDesignX > Plan.GlassSizeX + 1e-8 || beamDesignY < -1e-8 || beamDesignY > Plan.GlassSizeY + 1e-8)
                throw new InvalidOperationException($"H{head:00}: 선택한 Beam이 Glass 영역 밖입니다.");
            var scale = Math.Abs(Read($"H{head:00}_ENCODER_SCALE", Read("SCAN_ENCODER_SCALE", 16000)));
            if (scale <= 0)
                throw new InvalidOperationException($"H{head:00}: Encoder Scale을 확인해 주세요.");
            var frequency = Read($"H{head:00}_LASER_FREQUENCY", 100) * 1000;
            var shotCount = Read($"H{head:00}_SHOT_COUNT", 1);
            if (frequency <= 0 || shotCount < 0)
                throw new InvalidOperationException($"H{head:00}: 주파수·Shot Count를 확인해 주세요.");
            var machiningSeconds = shotCount / frequency + (Math.Max(0, Read($"H{head:00}_LASER_ON_DELAY", 0)) + Math.Max(0, Read($"H{head:00}_LASER_OFF_DELAY", 0))) / 1000;
            return new(
                head, 
                cell, 
                shot, 
                beam, 
                point.ShotKey, 
                CReviewShotNameFormatter.ToMatrixName(shot, point.GroupCountX), 
                target.ReviewX, 
                target.StageY, 
                point.EncoderWaitPosition / (scale * Profile.StageDirection), 
                machiningSeconds
            );
        }

        public IReadOnlyList<ST_ZERO_DEFENCE_POINT> ReadSelections(bool requireComplete)
        {
            var result = new List<ST_ZERO_DEFENCE_POINT>();
            for (var head = 1; head <= 8; head++)
            {
                var cell = Int(Key(head, "CELL"));
                var shot = Int(Key(head, "SHOT"));
                var beam = Int(Key(head, "BEAM"));
                if (cell == 0 && shot == 0 && beam == 0 && !requireComplete)
                    continue;
                if (cell <= 0 || shot <= 0 || beam <= 0)
                    throw new InvalidOperationException($"H{head:00}: Cell·Shot·Beam을 모두 지정해 주세요.");
                result.Add(GetPoint(head, cell, shot, beam));
            }

            return result;
        }

        public string ValidateForSave()
        {
            try
            {
                if (Count is not (2 or 4))
                    return "0선방어 검사 개수는 2 또는 4만 가능합니다.";
                if (!HasSelections)
                    return ""; // Recipes without a 0-line plan remain usable for other operations.
                return CZeroDefenceTiming.Validate(Count, ReadSelections(requireComplete: true), Profile).Message;
            }
            catch (Exception error)when (error is InvalidOperationException or ArgumentException)
            {
                return error.Message;
            }
        }
    }

    public sealed record ST_ZERO_DEFENCE_TIMING_PROFILE(double StageStartY, double StageDirection, double StageSpeed, double ReviewSpeed, double ReviewAcceleration)
    {
        // MCC chart: 2420 mm process travel, 80 mm/s nominal, 147.1 mm/s² acceleration/deceleration.
        // Stage speed is the actual recipe value; Stage origin is the configured Ready position.
        public double StageStroke { get; init; } = 2420;
        public double StageAcceleration { get; init; } = 147.1;
        public double SettleSeconds { get; init; } = .050;
        public double ArrivalMarginSeconds { get; init; } = .020;
        public double FramesPerSecond { get; init; } = 20;
        public double ExposureSeconds { get; init; } = .000010;
    }

    public sealed record ST_ZERO_DEFENCE_POINT(
        int HeadNo, 
        int CellNo, 
        int ShotNo, 
        int BeamNo, 
        string ShotKey, 
        string ShotName, 
        double ReviewX, 
        double StageY, 
        double MachiningDistance, 
        double MachiningSeconds
    );
    public sealed record ST_ZERO_DEFENCE_TIMING_ISSUE(int GroupNo, int FromHead, int ToHead, string Reason, double AvailableSeconds = 0, double RequiredSeconds = 0);
    public sealed record ST_ZERO_DEFENCE_TRANSITION(int GroupNo, int FromHead, int ToHead, double AvailableSeconds, double RequiredSeconds)
    {
        public double MarginSeconds => AvailableSeconds - RequiredSeconds;
    }

    public sealed record ST_ZERO_DEFENCE_TIMING_RESULT(IReadOnlyList<ST_ZERO_DEFENCE_TIMING_ISSUE> Issues, IReadOnlyList<ST_ZERO_DEFENCE_TRANSITION> Transitions)
    {
        public bool IsValid => Issues.Count == 0;
        public string Message => string.Join("\n", Issues.Select(i => i.Reason));
    }

    /// <summary>No I/O. Validates each glass group, including exposure, X rest-to-rest travel and camera cadence.</summary>
    public static class CZeroDefenceTiming
    {
        public static ST_ZERO_DEFENCE_TIMING_RESULT Validate(int count, IEnumerable<ST_ZERO_DEFENCE_POINT> selections, ST_ZERO_DEFENCE_TIMING_PROFILE profile)
        {
            var issues = new List<ST_ZERO_DEFENCE_TIMING_ISSUE>();
            var transitions = new List<ST_ZERO_DEFENCE_TRANSITION>();
            if (count is not (2 or 4))
                return new([new(0, 0, 0, "검사 개수는 2 또는 4만 가능합니다.")], []);
            var points = selections.ToArray();
            if (points.Any(p => p.HeadNo is < 1 or > 8) || points.GroupBy(p => p.HeadNo).Any(g => g.Count() != 1))
                return new([new(0, 0, 0, "Head별로 검사점 하나만 지정할 수 있습니다.")], []);
            static bool Positive(double value) => double.IsFinite(value) && value > 0;
            if (!Positive(profile.StageStroke) || !Positive(profile.StageSpeed) || !Positive(profile.StageAcceleration) || !Positive(profile.ReviewSpeed) || !Positive(profile.ReviewAcceleration))
                return new([new(0, 0, 0, "Stage 스트로크·속도·가속도와 Review 속도·가속도는 0보다 큰 유한한 값이어야 합니다.")], []);
            if (profile.StageDirection is not (-1 or 1) || !double.IsFinite(profile.StageStartY) || !double.IsFinite(profile.SettleSeconds) || profile.SettleSeconds < 0 || !double.IsFinite(profile.ArrivalMarginSeconds) || profile.ArrivalMarginSeconds < 0 || !double.IsFinite(profile.FramesPerSecond) || profile.FramesPerSecond <= 0 || !double.IsFinite(profile.ExposureSeconds) || profile.ExposureSeconds <= 0)
                return new([new(0, 0, 0, "0선방어 이동·카메라 조건이 올바르지 않습니다.")], []);
            var stage = new CTrapezoidalMotion(profile.StageStroke, profile.StageSpeed, profile.StageAcceleration);
            _ = new CTrapezoidalMotion(0, profile.ReviewSpeed, profile.ReviewAcceleration);
            var samples = new List<(ST_ZERO_DEFENCE_POINT Point, double Time)>();
            foreach (var p in points)
            {
                var group = (p.HeadNo - 1) / count + 1;
                var travel = (p.StageY - profile.StageStartY) * profile.StageDirection;
                if (!double.IsFinite(p.ReviewX) || !double.IsFinite(travel) || travel < stage.AccelerationDistance - 1e-8 || travel + profile.StageSpeed * profile.ExposureSeconds > profile.StageStroke - stage.DecelerationDistance + 1e-8)
                {
                    issues.Add(new(group, p.HeadNo, p.HeadNo, $"H{p.HeadNo:00} {p.ShotName}: 촬영 위치가 MCC 등속 가공 구간 밖입니다. 카메라 AK Y 티칭·Stage 시작 위치를 확인해 주세요."));
                    continue;
                }

                var time = stage.TimeAt(travel);
                if (!double.IsFinite(p.MachiningDistance) || p.MachiningDistance < 0 || p.MachiningDistance > profile.StageStroke || !double.IsFinite(p.MachiningSeconds) || p.MachiningSeconds < 0 || time + 1e-8 < stage.TimeAt(p.MachiningDistance) + p.MachiningSeconds)
                {
                    issues.Add(new(group, p.HeadNo, p.HeadNo, $"H{p.HeadNo:00} {p.ShotName}: 해당 Shot의 가공이 끝나기 전에 카메라 위치를 통과합니다."));
                    continue;
                }

                samples.Add((p, time));
            }

            foreach (var group in samples.GroupBy(s => (s.Point.HeadNo - 1) / count + 1))
            {
                var ordered = group.OrderBy(s => s.Time).ThenBy(s => s.Point.HeadNo).ToArray();
                for (var i = 1; i < ordered.Length; i++)
                {
                    var previous = ordered[i - 1];
                    var next = ordered[i];
                    var move = new CTrapezoidalMotion(Math.Abs(next.Point.ReviewX - previous.Point.ReviewX), profile.ReviewSpeed, profile.ReviewAcceleration);
                    var available = next.Time - previous.Time;
                    var required = Math.Max(1 / profile.FramesPerSecond, profile.ExposureSeconds + move.Duration + profile.SettleSeconds + profile.ArrivalMarginSeconds);
                    transitions.Add(new(group.Key, previous.Point.HeadNo, next.Point.HeadNo, available, required));
                    if (available + 1e-8 < required)
                        issues.Add(new(
                            group.Key, 
                            previous.Point.HeadNo, 
                            next.Point.HeadNo, 
                            $"그룹 {group.Key} · H{previous.Point.HeadNo:00} → H{next.Point.HeadNo:00}: " + $"검사 간격 {available * 1000:0.0} ms / 필요 {required * 1000:0.0} ms " + $"(X 이동 {move.Duration * 1000:0.0} + 안정화 {profile.SettleSeconds * 1000:0.0} + 여유 {profile.ArrivalMarginSeconds * 1000:0.0} ms).", 
                            available, 
                            required
                        ));
                }
            }

            return new(issues, transitions);
        }
    }

    public sealed record ST_ZERO_LINE_CYCLE_PLAN(
        Guid RunId, 
        string RecipeId, 
        string GlassId, 
        int Group, 
        int TotalGroups, 
        bool FollowUp, 
        IReadOnlyList<ST_ZERO_DEFENCE_POINT> Points, 
        ST_REVIEW_PLAN VisionPlan, 
        ST_ZERO_DEFENCE_TIMING_PROFILE Motion
    );
    public sealed record ST_ZERO_LINE_POINT_RESULT(ST_REVIEW_PLAN_POINT Point, string State, int Resolution = 0, Interface.Vision.ST_VISION_MARK_FIND? Mark = null, string Detail = "")
    {
        public bool IsNg => State is "NG" or "NCK" or "ERROR";
    }

    public sealed record ST_ZERO_LINE_CYCLE_STATUS(bool Enabled, string State, string Detail, int Group = 0, IReadOnlyList<ST_ZERO_LINE_POINT_RESULT>? Results = null, string ResultFile = "")
    {
        public ST_ZERO_LINE_CYCLE_PLAN? Plan { get; init; }
        public bool FollowUpRequested { get; init; }
        public bool FollowUpCompleted { get; init; }
        public string Judge => State != "DONE" ? "미완료" : Results?.Any(result => result.IsNg) == true ? "NG" : "OK";
    }

    public sealed record ST_ZERO_LINE_RESULT_DATA(ST_ZERO_LINE_CYCLE_PLAN Plan, ST_ZERO_LINE_CYCLE_STATUS Status, bool StageSimulation, bool VisionSimulation, DateTimeOffset SavedAt);
    /// <summary>Frozen once per DoReady request. No I/O and no head-direction conversion of measurements.</summary>
    public static class CZeroLineCyclePlan
    {
        public static ST_ZERO_LINE_CYCLE_PLAN? Build(ST_RECIPE_DEFINITION recipe, string glassId, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var options = CRunOptionSettings.Read(settings);
            if (!options.ZeroDefence)
                return null;
            var source = new CZeroDefenceRecipePlan(recipe, settings);
            if (CInterLockManager.EvaluateHeadUse(source.Parameters).Any(item => item.BlocksAutoRun))
                throw new InvalidOperationException("AUTO는 HEAD 1~8이 모두 USE여야 합니다. Interlock/Safety를 확인해 주세요.");
            if (source.Count is not (2 or 4))
                throw new InvalidOperationException("0선방어 검사 개수/현재 그룹을 확인해 주세요.");
            var totalGroups = 8 / source.Count;
            var group = Math.Clamp(options.CurrentGroup, 1, totalGroups);
            var selected = source.ReadSelections(requireComplete: true);
            var timing = CZeroDefenceTiming.Validate(source.Count, selected, source.Profile);
            if (!timing.IsValid)
                throw new InvalidOperationException(timing.Message);
            var points = selected.Where(point => (point.HeadNo - 1) / source.Count + 1 == group).OrderBy(point => point.StageY * source.Profile.StageDirection).ThenBy(point => point.HeadNo).ToArray();
            var shots = source.Plan.Shots.ToDictionary(point => (point.CellNo, point.CellShotNo));
            var targets = points.Select((point, index) =>
            {
                var shot = shots[(point.CellNo, point.ShotNo)];
                return new ST_REVIEW_PLAN_POINT(
                    index + 1, 
                    point.ShotKey, 
                    point.HeadNo, 
                    point.CellNo, 
                    point.ShotNo, 
                    shot.GroupCountX, 
                    shot.GroupCountY, 
                    true, 
                    shot.DesignX, 
                    shot.DesignY, 
                    point.ReviewX, 
                    point.StageY, 
                    0, 
                    0, 
                    EN_REVIEW_POINT_STATE.Ready, 
                    "WAIT"
                )
                {
                    BeamNo = point.BeamNo
                };
            }).ToArray();
            var vision = new ST_REVIEW_PLAN(
                recipe.Id, 
                targets.Select(p => p.CellNo).Distinct().Count(), 
                Tolerance("REVIEW_TOLERANCE_X"), 
                Tolerance("REVIEW_TOLERANCE_Y"), 
                CReviewManager.ParseVisionFlipType(source.Parameters.GetValueOrDefault("VisionXFlip"), source.Parameters.GetValueOrDefault("VisionYFlip"), source.Parameters.GetValueOrDefault("VisionXyFlip")), 
                DateTimeOffset.Now, 
                Array.AsReadOnly(targets)
            );
            return new(Guid.NewGuid(), recipe.Id, glassId, group, totalGroups, options.FollowUp, Array.AsReadOnly(points), vision, source.Profile);
            double Tolerance(string key)
            {
                var text = source.Parameters.GetValueOrDefault(key, "0.03");
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0)
                    throw new InvalidOperationException($"{key}: 0 이상의 유한한 허용오차가 필요합니다.");
                return value;
            }
        }
    }
}
