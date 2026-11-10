namespace Drilling.Common.Correction
{
    using Drilling.Common.Recipe;
    using Drilling.Common.Interface;
    using Drilling.Common.Managers;
    using Drilling.Common.Station;
    using System.Globalization;

    // Interpolate each horizontal row linearly in X, then use global Lagrange interpolation in Y.
    // This also handles rows whose left/right X coordinates do not match the AK columns.
    public sealed class CAlignInterpolation
    {
        public const string AlgorithmName = "Lagrange";
        private readonly double[] _normalizedY;
        private readonly double[] _weights;
        public IReadOnlyList<ST_ALIGN_INTERPOLATION_ROW> Rows { get; }

        public CAlignInterpolation(IReadOnlyList<ST_ALIGN_INTERPOLATION_KEY> keys, IReadOnlyCollection<string> selectedDkNames)
        {
            var byName = keys.ToDictionary(key => key.Name, StringComparer.Ordinal);
            ST_ALIGN_INTERPOLATION_KEY Find(string name) => byName.TryGetValue(name, out var key) ? key : throw new InvalidOperationException($"{name} 키 위치가 없습니다.");
            var top = new ST_ALIGN_INTERPOLATION_ROW(Find("AK1"), Find("AK3"));
            var bottom = new ST_ALIGN_INTERPOLATION_ROW(Find("AK2"), Find("AK4"));
            ValidateRow(top);
            ValidateRow(bottom);
            if (bottom.Y - top.Y <= 1e-9)
                throw new InvalidOperationException("위쪽 AK와 아래쪽 AK의 Y 위치가 서로 달라야 합니다.");
            var selected = selectedDkNames.Distinct(StringComparer.Ordinal).Select(Find).ToArray();
            var rows = new List<ST_ALIGN_INTERPOLATION_ROW>
            {
                top
            };
            foreach (var group in selected.GroupBy(key => key.Y).OrderBy(group => group.Key))
            {
                var pair = group.OrderBy(key => key.X).ToArray();
                if (pair.Length != 2)
                    throw new InvalidOperationException($"Y={group.Key:0.###} 행에는 DK가 {pair.Length}개 있습니다. 좌우 DK 두 개로 구성된 행을 선택해 주세요.");
                var row = new ST_ALIGN_INTERPOLATION_ROW(pair[0], pair[1]);
                ValidateRow(row);
                if (row.Y <= top.Y || row.Y >= bottom.Y)
                    throw new InvalidOperationException($"{pair[0].Name} / {pair[1].Name}의 Y 위치가 위·아래 AK 사이에 있어야 합니다.");
                rows.Add(row);
            }

            rows.Add(bottom);
            if (rows.Zip(rows.Skip(1), (first, second) => second.Y - first.Y).Any(span => span <= 1e-9))
                throw new InvalidOperationException("선택한 키 행의 Y 간격이 너무 작습니다.");
            Rows = rows.ToArray();
            _normalizedY = rows.Select(row => (row.Y - top.Y) / (bottom.Y - top.Y)).ToArray();
            _weights = _normalizedY.Select((value, index) => 1.0 / _normalizedY.Where((_, other) => other != index).Aggregate(1.0, (product, other) => product * (value - other))).ToArray();
            if (_weights.Any(weight => !double.IsFinite(weight)))
                throw new InvalidOperationException("선택한 키의 Y 간격으로 라그랑쥬 보간을 계산할 수 없습니다.");
        }

        public bool Contains(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || y < Rows[0].Y || y > Rows[^1].Y)
                return false;
            var(first, second, fraction) = FindInterval(y);
            var left = first.Left.X + fraction * (second.Left.X - first.Left.X);
            var right = first.Right.X + fraction * (second.Right.X - first.Right.X);
            return x >= left - 1e-9 && x <= right + 1e-9;
        }

        public void ValidateMeasurements()
        {
            foreach (var row in Rows)
            {
                ReadCorrection(row.Left);
                ReadCorrection(row.Right);
            }
        }

        public ST_ALIGN_CORRECTION Evaluate(double x, double y)
        {
            if (!Contains(x, y))
                throw new InvalidOperationException("검증 위치가 선택한 키로 구성된 보간 범위를 벗어납니다.");
            ValidateMeasurements();
            for (var index = 0; index < Rows.Count; index++)
                if (y == Rows[index].Y)
                    return EvaluateRow(Rows[index], x);
            var normalized = (y - Rows[0].Y) / (Rows[^1].Y - Rows[0].Y);
            double denominator = 0, numeratorX = 0, numeratorY = 0;
            for (var index = 0; index < Rows.Count; index++)
            {
                var term = _weights[index] / (normalized - _normalizedY[index]);
                var value = EvaluateRow(Rows[index], x);
                denominator += term;
                numeratorX += term * value.X;
                numeratorY += term * value.Y;
            }

            return Finite(new ST_ALIGN_CORRECTION(numeratorX / denominator, numeratorY / denominator));
        }

        private (ST_ALIGN_INTERPOLATION_ROW First, ST_ALIGN_INTERPOLATION_ROW Second, double Fraction) FindInterval(double y)
        {
            for (var index = 1; index < Rows.Count; index++)
                if (y <= Rows[index].Y)
                    return (Rows[index - 1], Rows[index], (y - Rows[index - 1].Y) / (Rows[index].Y - Rows[index - 1].Y));
            throw new InvalidOperationException("검증 위치의 Y 구간을 찾을 수 없습니다.");
        }

        private static void ValidateRow(ST_ALIGN_INTERPOLATION_ROW row)
        {
            if (!double.IsFinite(row.Y) || !double.IsFinite(row.Right.Y) || row.Y != row.Right.Y || !double.IsFinite(row.Left.X) || !double.IsFinite(row.Right.X) || row.Right.X - row.Left.X <= 1e-9)
                throw new InvalidOperationException($"{row.Left.Name} / {row.Right.Name}는 같은 Y에서 서로 다른 X 위치에 있어야 합니다.");
        }

        private static ST_ALIGN_CORRECTION ReadCorrection(ST_ALIGN_INTERPOLATION_KEY key) => key.CorrectionX is { } x && key.CorrectionY is { } y && double.IsFinite(x) && double.IsFinite(y) ? new(x, y) : throw new InvalidOperationException($"{key.Name}의 Base / LastLoad X/Y 값이 없습니다. Load 후 다시 확인해 주세요.");
        private static ST_ALIGN_CORRECTION EvaluateRow(ST_ALIGN_INTERPOLATION_ROW row, double x) => Blend(ReadCorrection(row.Left), ReadCorrection(row.Right), (x - row.Left.X) / (row.Right.X - row.Left.X));
        private static ST_ALIGN_CORRECTION Blend(ST_ALIGN_CORRECTION first, ST_ALIGN_CORRECTION second, double fraction) => Finite(new(first.X + fraction * (second.X - first.X), first.Y + fraction * (second.Y - first.Y)));
        private static ST_ALIGN_CORRECTION Finite(ST_ALIGN_CORRECTION value) => double.IsFinite(value.X) && double.IsFinite(value.Y) ? value : throw new InvalidOperationException("보간 계산 결과가 유한한 값이 아닙니다.");
    }

    public sealed record ST_ALIGN_INTERPOLATION_KEY(string Name, double X, double Y, double? CorrectionX = null, double? CorrectionY = null);
    public sealed record ST_ALIGN_INTERPOLATION_ROW(ST_ALIGN_INTERPOLATION_KEY Left, ST_ALIGN_INTERPOLATION_KEY Right)
    {
        public double Y => Left.Y;
    }

    public readonly record struct ST_ALIGN_CORRECTION(double X, double Y);
    public sealed record ST_ALIGN_OFFSET_KEY_LOG(
        string Name, 
        bool Included, 
        double DesignX, 
        double DesignY, 
        double? BaseX, 
        double? BaseY, 
        double? LastLoadX, 
        double? LastLoadY, 
        double? OffsetX, 
        double? OffsetY
    );
    public sealed record ST_ALIGN_OFFSET_LOG(
        string RecipeId, 
        string RecipeName, 
        bool IsInit, 
        DateTimeOffset ChangedAt, 
        DateTimeOffset? LastLoadedAt, 
        bool IsSimulation, 
        IReadOnlyList<ST_ALIGN_OFFSET_KEY_LOG> Keys, 
        IReadOnlyList<ST_ALIGN_SHOT_OFFSET> Shots
    )
    {
        public double? Tolerance { get; init; }
        public IReadOnlyList<ST_ALIGN_SHOT_OFFSET> CalculatedOffsets { get; init; } = [];
        public IReadOnlyList<ST_ALIGN_SHOT_OFFSET> ExcludedOffsets { get; init; } = [];
    }

    public abstract class CAlignOffsetLogFileBase
    {
        public abstract Task<string> Save(ST_ALIGN_OFFSET_LOG entry, CancellationToken cancellationToken = default);
    }

    public sealed record ST_ALIGN_OFFSET_TOLERANCE_CHECK(double Tolerance, int TotalShots, IReadOnlyList<ST_ALIGN_SHOT_OFFSET> ExceededShots)
    {
        public int ApplyCount => TotalShots - ExceededShots.Count;
        public bool CanApplyRemaining => ApplyCount > 0;
        public string Summary => $"{CAlignInterpolation.AlgorithmName} / Setting Tolerance : ±{Tolerance.ToString("0.000", CultureInfo.InvariantCulture)} mm\n" + $"전체 {TotalShots:N0} Shot · 초과 {ExceededShots.Count:N0} Shot · 적용 가능 {ApplyCount:N0} Shot";
    }

    public static class CAlignOffsetTolerance
    {
        public const string SettingKey = "ALIGN_OFFSET_TOLERANCE";
        public const double DefaultValue = 0.5;
        public static double Parse(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tolerance) || !double.IsFinite(tolerance) || tolerance < 0 || tolerance > 100000)
                throw new InvalidOperationException("Setting > COMMON > ALIGN의 Align Offset Tolerance를 0 ~ 100000 mm 범위로 설정해 주세요.");
            return CAlignShotOffsetData.RoundValue(tolerance);
        }

        // Reject the entire shot, including its other axis, if either axis exceeds the limit.
        public static bool Exceeds(ST_ALIGN_SHOT_OFFSET shot, double tolerance) => Math.Abs(shot.X) > tolerance || Math.Abs(shot.Y) > tolerance;
        public static ST_ALIGN_OFFSET_TOLERANCE_CHECK Check(double tolerance, IReadOnlyList<ST_ALIGN_SHOT_OFFSET> offsets) => new(tolerance, offsets.Count, offsets.Where(shot => Exceeds(shot, tolerance)).ToArray());
    }
    // Stage Y=0 when imaging AK2/AK4. Positive Stage motion visits Group2,1,0, then AK1/AK3.
    // Design Y increases downwards, so Stage displacement is subtracted from the lower AK row.
    public static class CAlignKeyGeometry
    {
        public static string StagePositionKey(int group) => group is >= 0 and <= 2 ? $"DK{group}_STAGE_Y_POSITION" : throw new ArgumentOutOfRangeException(nameof(group));
        public static double DesignY(double height, double marginY, double stageY) => height - Math.Clamp(marginY, 0, height / 2) - stageY;
        public static double[] ReadStagePositions(IReadOnlyList<ST_SYSTEM_PARAMETER> settings) => Enumerable.Range(0, 3).Select(group =>
        {
            var key = StagePositionKey(group);
            var text = settings.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? value : throw new InvalidOperationException($"Setting > Motor > ALIGN의 {key}에 유효한 mm 값이 필요합니다.");
        }).ToArray();
        public static ST_ALIGN_INTERPOLATION_KEY[] Create(IReadOnlyDictionary<string, string> recipe, IReadOnlyList<double> stageY)
        {
            double Read(string key) => recipe.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
                ? v : throw new InvalidOperationException($"{key}의 유효한 숫자 값이 필요합니다.");
            var width = Read("GLASS_SIZE_X"); var height = Read("GLASS_SIZE_Y");
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Glass Size는 0보다 커야 합니다.");
            if (stageY.Count != 3 || stageY.Any(v => !double.IsFinite(v)))
                throw new InvalidOperationException("Setting > Motor > ALIGN의 DK0/1/2 Stage Y Position을 확인하세요.");
            var mx = Math.Clamp(Read("AK_MARGIN_X"), 0, width / 2);
            var my = Math.Clamp(Read("AK_MARGIN_Y"), 0, height / 2);
            return new[] { new ST_ALIGN_INTERPOLATION_KEY("AK1", mx, my), new("AK2", mx, height - my), new("AK3", width - mx, my), new("AK4", width - mx, height - my) }
                .Concat(Enumerable.Range(0, 3).SelectMany(group => new[]
                {
                    new ST_ALIGN_INTERPOLATION_KEY($"DK{group * 2 + 1}", mx, DesignY(height, my, stageY[group])),
                    new ST_ALIGN_INTERPOLATION_KEY($"DK{group * 2 + 2}", width - mx, DesignY(height, my, stageY[group]))
                })).ToArray();
        }
    }

    public sealed record ST_WAPAGE_ALIGNBASE_POINT(string Label, string VisionId, string BaseId, string RangeId);

    // A complete published frame. The UI never reads the PLC or an editable recipe to display a run.
    public sealed record ST_DISTORTION_MEASUREMENT_SNAPSHOT(Guid RunId, ST_RECIPE_DEFINITION Recipe, string GlassId,
        IReadOnlyList<int> Groups, IReadOnlyDictionary<string, double> LastLoads, DateTimeOffset? LoadedAt)
    {
        public IReadOnlyList<double> StageYPositions { get; init; } = [];
    }

    /// <summary>One real-communication Distortion transaction, called serially by the Stage worker.</summary>
    public sealed class CAlignDistortionCycle(CInterfaceManager interfaces, CSettingManager settings, CRecipeManager recipes,
        CAlignOffsetLogFileBase audit, Func<CancellationToken, Task<ST_STAGE_PROCESS_REQUEST>> capture, Action<string>? log = null)
    {
        private readonly CAlignWapageService _io = new(interfaces);
        private ST_STAGE_PROCESS_REQUEST? _input, _pending;
        private ST_RECIPE_DEFINITION? _recipe;
        private int[] _groups = [];
        private IReadOnlyList<double> _stageYPositions = [];
        private double _tolerance;
        private IReadOnlyDictionary<string, double>? _results;
        private CAlignInterpolation? _model;
        private IReadOnlyList<ST_ALIGN_SHOT_OFFSET>? _offsets;
        private DateTimeOffset? _loadedAt;
        private bool _saved;
        private ST_DISTORTION_MEASUREMENT_SNAPSHOT? _snapshot;
        public ST_DISTORTION_MEASUREMENT_SNAPSHOT? Snapshot => Volatile.Read(ref _snapshot);
        public string GlassId => _input?.GlassId ?? "";
        public static string ResultId(string parameterKey)
        {
            var parts = parameterKey.Split('_');
            if (parts.Length != 3 || parts[0] != "ALIGNBASE" || parts[2] is not ("X" or "Y") || parts[1].Length < 3
                || !int.TryParse(parts[1].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new InvalidOperationException($"알 수 없는 Align LastLoad 항목: {parameterKey}");
            if (parts[1].StartsWith("AK", StringComparison.Ordinal) && number is >= 1 and <= 4)
                return $"DISTORTION_POS0_CAM{number}{parts[2]}";
            if (parts[1].StartsWith("DK", StringComparison.Ordinal) && number is >= 1 and <= 6)
                return $"DISTORTION_POS{(number - 1) / 2 + 1}_CAM{(number % 2 == 1 ? 2 : 4)}{parts[2]}";
            throw new InvalidOperationException($"알 수 없는 Align LastLoad 항목: {parameterKey}");
        }
        private static string[] Names(IReadOnlyCollection<int> groups) => Enumerable.Range(1, 4).Select(n => $"AK{n}")
            .Concat(groups.Order().SelectMany(group => new[] { $"DK{group * 2 + 1}", $"DK{group * 2 + 2}" })).ToArray();
        private static string[] ParameterKeys(IReadOnlyCollection<int> groups) => Names(groups).SelectMany(name => new[] { $"ALIGNBASE_{name}_X", $"ALIGNBASE_{name}_Y" }).ToArray();
        private static double Read(IReadOnlyDictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException($"{key}의 유효한 숫자 값이 필요합니다.");
            return value;
        }
        private ST_ALIGN_INTERPOLATION_KEY[] Keys()
        {
            var recipe = _recipe ?? throw new InvalidOperationException("Distortion 레시피가 준비되지 않았습니다.");
            var p = recipe.Parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            double? Delta(string name, string axis) => _results is not null && _results.TryGetValue($"ALIGNBASE_{name}_{axis}", out var measured) ? Read(p, $"ALIGNBASE_{name}_{axis}") - measured : null;
            return CAlignKeyGeometry.Create(p, _stageYPositions).Select(key => key with { CorrectionX = Delta(key.Name, "X"), CorrectionY = Delta(key.Name, "Y") }).ToArray();
        }

        public async Task Prepare(CancellationToken token)
        {
            Volatile.Write(ref _snapshot, null);
            _pending = null; _results = null; _offsets = null; _model = null; _loadedAt = null; _saved = false;
            _input = await capture(token).ConfigureAwait(false);
            _recipe = await recipes.RefreshRecipe(_input.RecipeId, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Distortion 레시피를 찾을 수 없습니다.");
            var option = settings.LoadSection(EN_SETTING_TAB.Option).ToArray();
            _stageYPositions = Array.AsReadOnly(CAlignKeyGeometry.ReadStagePositions(settings.LoadSection(EN_SETTING_TAB.Motor)));
            _groups = CRunOptionSettings.Read(option).DistortionGroups.ToArray();
            var op = option.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            _tolerance = CAlignOffsetTolerance.Parse(op.GetValueOrDefault(CAlignOffsetTolerance.SettingKey, "0.5"));
            var resolution = Read(op, CSettingManager.AlignCameraResolutionKey);
            var rangeMm = Read(op, CSettingManager.WapageAlignBaseToleranceKey);
            if (resolution <= 0 || rangeMm < 0) throw new InvalidOperationException("Align 분해능은 양수, Wapage 허용범위는 0 이상이어야 합니다.");
            var pixels = rangeMm / resolution;
            if (!double.IsFinite(pixels)) throw new InvalidOperationException("Wapage pixel 범위를 계산할 수 없습니다.");
            _ = new CAlignInterpolation(Keys(), Names(_groups).Where(name => name.StartsWith("DK", StringComparison.Ordinal)).ToArray());
            var rp = _recipe.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var key in ParameterKeys(_groups)) _ = Read(rp, key);
            // Keep the viewer frame small: shot offsets/history are unrelated to key measurement display.
            var displayKeys = ParameterKeys([0, 1, 2]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            displayKeys.UnionWith(new[] { "GLASS_SIZE_X", "GLASS_SIZE_Y", "AK_MARGIN_X", "AK_MARGIN_Y" });
            var frozenRecipe = _recipe with { Parameters = Array.AsReadOnly(_recipe.Parameters.Where(p => displayKeys.Contains(p.Key)).ToArray()), History = [] };
            Volatile.Write(ref _snapshot, new(Guid.NewGuid(), frozenRecipe, GlassId, Array.AsReadOnly(_groups.ToArray()),
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(new Dictionary<string, double>()), null) { StageYPositions = _stageYPositions });
            var control = new List<(string Id, double Value)> { ("PROCESS_DISTORTION_COUNT", _groups.Length) };
            for (var index = 0; index < 3; index++) control.Add(($"PROCESS_DISTORTION_INDEX_{index}", _groups.Contains(index) ? 1 : 0));
            for (var index = 0; index < 3; index++) control.Add(($"PROCESS_DISTORTION_INDEX_{index}_POSITION", _stageYPositions[index]));
            var visionIds = Enumerable.Range(1, 4).SelectMany(camera => new[] { $"SVID_FINEALN_CAM{camera}_GLS_RESULT_X", $"SVID_FINEALN_CAM{camera}_GLS_RESULT_Y" }).ToArray();
            // Validate every required route before the first PLC write. Source/result data are never fabricated.
            _io.Validate(visionIds, false);
            _io.Validate(ParameterKeys(_groups).Select(ResultId).ToArray(), false);
            _io.Validate(CAlignWapageService.Points.SelectMany(point => new[] { point.BaseId, point.RangeId }).Concat(control.Select(item => item.Id)).ToArray(), true);
            Log($"측정 조건 동결 · Recipe={_recipe.Name}; Groups={string.Join(",", _groups)}; Count={_groups.Length}; Range={pixels:0.####} pixel; " + string.Join(",", control.Where(item => item.Id.EndsWith("_POSITION")).Select(item => $"{item.Id}={item.Value:0.####} mm")));
            var vision = await _io.ReadValues(visionIds, token).ConfigureAwait(false);
            await _io.SetBase(vision, token).ConfigureAwait(false);
            await _io.SetRange(pixels, token).ConfigureAwait(false);
            await _io.WriteValues(control.ToArray(), token).ConfigureAwait(false);
            Log("Wapage Base/Range · Distortion Count/Index/Stage Y 기록 및 읽기 확인 완료");
        }
        public async Task LoadResults(CancellationToken token)
        {
            if (_recipe is null) throw new InvalidOperationException("Distortion 준비가 필요합니다.");
            var keys = ParameterKeys(_groups);
            var values = await _io.ReadValues(keys.Select(ResultId).ToArray(), token).ConfigureAwait(false);
            _results = keys.ToDictionary(key => key, key => values[ResultId(key)], StringComparer.OrdinalIgnoreCase);
            _loadedAt = DateTimeOffset.Now;
            if (Snapshot is { } frame)
                Volatile.Write(ref _snapshot, frame with { LastLoads = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(
                    new Dictionary<string, double>(_results, StringComparer.OrdinalIgnoreCase)), LoadedAt = _loadedAt });
            foreach (var key in keys) Log($"측정값 취득 · {key}={_results[key]:0.####} mm ({ResultId(key)})");
        }
        public Task Calculate(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_results is null || _recipe is null) throw new InvalidOperationException("Distortion 측정 결과가 없습니다.");
            _model = new(Keys(), Names(_groups).Where(name => name.StartsWith("DK", StringComparison.Ordinal)).ToArray());
            var offsets = CAlignShotOffsetData.Calculate(_recipe, _model);
            var exceeded = CAlignOffsetTolerance.Check(_tolerance, offsets).ExceededShots;
            if (exceeded.Count > 0) throw new InvalidOperationException($"Align Offset Tolerance ±{_tolerance:0.000} mm 초과 {exceeded.Count} Shot. 전체 적용 취소. " + string.Join("; ", exceeded.Take(8).Select(shot => $"Cell{shot.CellNo}/{shot.ShotName}: X={shot.X:0.000}, Y={shot.Y:0.000}")));
            _offsets = offsets;
            Log($"Base−LastLoad 라그랑주 계산 완료 · {offsets.Count} Shot · Tolerance ±{_tolerance:0.000} mm · 설계 좌표 기준");
        }, token);
        public async Task Save(CancellationToken token)
        {
            if (_saved) return;
            if (_recipe is null || _input is null || _model is null || _offsets is null || _results is null)
                throw new InvalidOperationException("Distortion 보정 계산이 완료되지 않았습니다.");
            var saved = await recipes.SaveAlignOffsetsIfUnchanged(_recipe, _offsets, token).ConfigureAwait(false);
            var selected = Names(_groups).ToHashSet(StringComparer.Ordinal);
            var p = _recipe.Parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            double? OptionalBase(string name, string axis) => p.TryGetValue($"ALIGNBASE_{name}_{axis}", out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
            var keyLog = Keys().Select(key =>
            {
                var delta = selected.Contains(key.Name) ? new ST_ALIGN_CORRECTION(key.CorrectionX!.Value, key.CorrectionY!.Value) : (ST_ALIGN_CORRECTION?)null;
                return new ST_ALIGN_OFFSET_KEY_LOG(key.Name, selected.Contains(key.Name), key.X, key.Y, OptionalBase(key.Name, "X"), OptionalBase(key.Name, "Y"),
                    _results.GetValueOrDefault($"ALIGNBASE_{key.Name}_X", double.NaN) is var x && double.IsFinite(x) ? x : null,
                    _results.GetValueOrDefault($"ALIGNBASE_{key.Name}_Y", double.NaN) is var y && double.IsFinite(y) ? y : null, delta?.X, delta?.Y);
            }).ToArray();
            var simulation = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).Any(device => device.IsSimulation);
            var path = await audit.Save(new(_recipe.Id, _recipe.Name, false, DateTimeOffset.Now, _loadedAt, simulation, keyLog, _offsets) { Tolerance = _tolerance, CalculatedOffsets = _offsets }, token).ConfigureAwait(false);
            var parameters = new Dictionary<string, string>(_input.Parameters, StringComparer.OrdinalIgnoreCase);
            foreach (var key in parameters.Keys.Where(CAlignShotOffsetData.IsParameterKey).ToArray()) parameters.Remove(key);
            foreach (var item in saved.Parameters.Where(item => CAlignShotOffsetData.IsParameterKey(item.Key))) parameters[item.Key] = item.Value;
            Interlocked.Exchange(ref _pending, _input with { Parameters = parameters });
            _saved = true;
            Log($"이번 Glass Align Offset 갱신 완료 · {_offsets.Count} Shot · Audit={path}");
        }
        public ST_STAGE_PROCESS_REQUEST? TakePreparedRequest(string recipeId)
        {
            var pending = Volatile.Read(ref _pending);
            if (pending is null) return null;
            if (!pending.RecipeId.Equals(recipeId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Distortion과 DoReady의 레시피가 다릅니다.");
            return Interlocked.CompareExchange(ref _pending, null, pending) == pending ? pending : null;
        }
        private void Log(string text) { try { log?.Invoke(text); } catch (Exception error) { System.Diagnostics.Trace.TraceError("Distortion log failed: " + error); } }
    }

    // Button-driven one-shot operations. No polling or automatic publication is added here.
    public sealed class CAlignWapageService
    {
        // Vision source addresses stay unchanged. The user-confirmed destination order is
        // CAM1..4 X -> X1..4, then CAM1..4 Y -> Y1..4 (not the interleaved 0.12 sheet remarks).
        public static IReadOnlyList<ST_WAPAGE_ALIGNBASE_POINT> Points { get; } = Enumerable.Range(0, 8).Select(index =>
        {
            var axis = index < 4 ? "X" : "Y";
            var camera = index % 4 + 1;
            return new ST_WAPAGE_ALIGNBASE_POINT($"CAM{camera} {axis} → {axis}{camera}", $"SVID_FINEALN_CAM{camera}_GLS_RESULT_{axis}",
                $"PROCESS_WAPAGE_ALIGNBASE_{axis}{camera}", $"PROCESS_WAPAGE_ALIGNBASE_RAGNE_{axis}{camera}");
        }).ToArray();
        private readonly Func<IReadOnlyList<ST_MELSEC_MAP_DATA>> _map;
        private readonly Func<IReadOnlyList<ST_INTERFACE_COMM_STATUS>> _connections;
        private readonly Func<string, CancellationToken, double> _read;
        private readonly Action<string, double, CancellationToken> _write;
        public CAlignWapageService(CInterfaceManager? manager) : this(() => manager?.Melsec.GetMapList() ?? [],
            () => manager?.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec) ?? [],
            (id, token) => manager?.Melsec.ReadDoubleNoLog(id, token) ?? throw new InvalidOperationException("MELSEC이 연결되어 있지 않습니다."),
            (id, value, token) => { if (manager is null) throw new InvalidOperationException("MELSEC이 연결되어 있지 않습니다."); manager.Melsec.WriteDouble(id, value, token); }) { }
        public CAlignWapageService(Func<IReadOnlyList<ST_MELSEC_MAP_DATA>> map, Func<IReadOnlyList<ST_INTERFACE_COMM_STATUS>> connections,
            Func<string, CancellationToken, double> read, Action<string, double, CancellationToken> write)
        { _map = map; _connections = connections; _read = read; _write = write; }

        internal Dictionary<string, ST_MELSEC_MAP_DATA> Validate(IReadOnlyCollection<string> ids, bool write)
        {
            var map = _map().ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
            var connections = _connections();
            foreach (var id in ids)
            {
                if (!map.TryGetValue(id, out var row) || !row.Use || string.IsNullOrWhiteSpace(row.Address) || row.Address.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{id}의 MELSEC 주소와 USE 설정을 확인하세요.");
                if (row.DataType != EN_MELSEC_DATA_TYPE.Double || row.Access == EN_MELSEC_ACCESS.Write || write && row.Access != EN_MELSEC_ACCESS.ReadWrite)
                    throw new InvalidOperationException($"{id}의 DOUBLE 형식과 {(write ? "읽기/쓰기" : "읽기")} 권한을 확인하세요.");
                if (id.StartsWith("SVID_", StringComparison.Ordinal) || id.StartsWith("DISTORTION_POS", StringComparison.Ordinal) ? row.Direction != EN_MELSEC_DIRECTION.In : row.Direction != EN_MELSEC_DIRECTION.Out)
                    throw new InvalidOperationException($"{id}의 MELSEC 입출력 방향을 확인하세요.");
                CMelsec.ValidateMapData(row);
                var connection = connections.FirstOrDefault(item => item.Module == EN_EQP_MODULE.Melsec && item.Number == row.DeviceNo);
                if (connection is null || !(connection.IsSimulation || connection.ConnectionState is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation))
                    throw new InvalidOperationException("MELSEC이 연결되어 있지 않습니다.");
            }
            return map;
        }

        public Task<IReadOnlyDictionary<string, double>> Load(CancellationToken token = default)
        {
            var visionOrder = Enumerable.Range(1, 4).SelectMany(camera => new[] { $"SVID_FINEALN_CAM{camera}_GLS_RESULT_X", $"SVID_FINEALN_CAM{camera}_GLS_RESULT_Y" });
            var ids = visionOrder.Concat(Points.Select(point => point.BaseId)).Concat(Points.Select(point => point.RangeId)).ToArray();
            Validate(ids, false);
            return Task.Run<IReadOnlyDictionary<string, double>>(() => Read(ids, token), token);
        }
        internal Task<IReadOnlyDictionary<string, double>> ReadValues(string[] ids, CancellationToken token)
        {
            Validate(ids, false);
            return Task.Run<IReadOnlyDictionary<string, double>>(() => Read(ids, token), token);
        }
        internal Task<IReadOnlyDictionary<string, double>> WriteValues((string Id, double Value)[] values, CancellationToken token) => Write(values, token);
        private Dictionary<string, double> Read(IEnumerable<string> ids, CancellationToken token)
        {
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                var value = _read(id, token); // READ_SCALE is already applied by CMelsec.
                if (!double.IsFinite(value)) throw new InvalidOperationException($"{id}의 MELSEC 값이 유한한 숫자가 아닙니다.");
                values.Add(id, value);
            }
            return values;
        }
        public Task<IReadOnlyDictionary<string, double>> SetBase(IReadOnlyDictionary<string, double> vision, CancellationToken token = default)
        {
            if (vision.Count != 8 || Points.Any(point => !vision.TryGetValue(point.VisionId, out var value) || !double.IsFinite(value)))
                throw new InvalidOperationException("Vision X/Y 8개 값을 먼저 정상적으로 Load해 주세요.");
            return Write(Points.Select(point => (point.BaseId, vision[point.VisionId])).ToArray(), token);
        }
        public Task<IReadOnlyDictionary<string, double>> SetRange(double pixels, CancellationToken token = default)
        {
            if (!double.IsFinite(pixels) || pixels < 0) throw new InvalidOperationException("Wapage Range는 0 이상의 유한한 pixel 값이어야 합니다.");
            return Write(Points.Select(point => (point.RangeId, pixels)).ToArray(), token);
        }
        private Task<IReadOnlyDictionary<string, double>> Write((string Id, double Value)[] values, CancellationToken token)
        {
            var map = Validate(values.Select(item => item.Id).ToArray(), true);
            var expected = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in values)
            {
                var raw = Math.Round(item.Value * map[item.Id].WriteScale, MidpointRounding.AwayFromZero);
                if (!double.IsFinite(raw) || raw < int.MinValue || raw > int.MaxValue)
                    throw new InvalidOperationException($"{item.Id} 값이 MELSEC 저장 범위를 초과합니다.");
                expected.Add(item.Id, raw * map[item.Id].ReadScale);
            }
            return Task.Run<IReadOnlyDictionary<string, double>>(() =>
            {
                var completed = 0;
                try
                {
                    foreach (var item in values)
                    {
                        token.ThrowIfCancellationRequested();
                        _write(item.Id, item.Value, token); // WRITE_SCALE is applied once, inside CMelsec.
                        completed++;
                    }
                    var readback = Read(values.Select(item => item.Id), token);
                    foreach (var pair in expected)
                        if (Math.Abs(readback[pair.Key] - pair.Value) > 1e-7)
                            throw new InvalidOperationException($"{pair.Key} 기록값과 읽은 값이 다릅니다.");
                    return readback;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException($"Wapage Set 확인 실패 (쓰기 완료 {completed}/{values.Length}). 일부 값이 변경되었을 수 있으므로 Load로 현재값을 확인하세요.\n{exception.Message}", exception);
                }
            }, token);
        }
    }


}
