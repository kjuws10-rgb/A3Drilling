namespace Drilling.File.PowerTable
{
    using System.Globalization;
    using Drilling.File.Parser;
    using Drilling.Common.Interface;
    using System.Collections.Concurrent;
    using System.Text;

    /// <summary>Resolves a target optical power from a single Head's measured ATT table.</summary>
    public sealed partial class CPowerTableFile : IPowerMeasureTable
    {
#region 공유 상태·초기값
        private readonly string _directory;
        private readonly string? _initialDirectory;
        private static readonly ConcurrentDictionary<string, object> MeasurementLocks = new(StringComparer.OrdinalIgnoreCase);
#endregion
#region 생성·초기화
        /// <param name = "configRoot">The application's Config directory.</param>
        public CPowerTableFile(string configRoot, bool simulation = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
            _directory = Path.GetFullPath(Path.Combine(configRoot, "PowerTable"));
            if (simulation)
            {
                _initialDirectory = _directory;
                _directory = Path.Combine(_directory, "Simulation");
            }
        }
#endregion

#region 기본 처리
        public static CPowerMeasureRun CreateRun(Drilling.Common.Managers.CManager manager, Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_POWER_MEASURE_TARGET> targets, bool automatic)
            => new(manager, recipe, targets, automatic, (_, simulation) => new CPowerTableFile(manager.ConfigRoot, simulation), report => SaveReport(manager.ConfigRoot, report));

        public static string SaveReport(string configRoot, ST_POWER_MEASURE_REPORT report)
        {
            var folder = new string(report.RecipeId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            if (folder is "" or "." or "..") throw new InvalidDataException("유효한 Recipe 이름이 필요합니다.");
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configRoot))!, "Data", "PowerMeasure", folder);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{report.Origin}_{report.StartedAt:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.csv");
            var text = new StringBuilder("Recipe,Origin,StartedAt,FinishedAt,Head,Sensor,Simulation,Target_W,Settle_ms,MeasureCount,Interval_ms,Tolerance_W,MaxAttempts,State,Attempt,ATT_deg,Power_W,Diff_W,Result,Message\r\n");
            foreach (var head in report.Snapshot.Heads)
            {
                var samples = head.Samples.Count > 0 ? head.Samples : new ST_POWER_MEASURE_SAMPLE[] { new(0, head.Angle ?? double.NaN, head.Power ?? double.NaN, double.NaN, head.State) };
                foreach (var sample in samples)
                {
                    object?[] fields = [report.RecipeId, report.Origin, report.StartedAt.ToString("O"), report.FinishedAt.ToString("O"), head.Target.HeadNo, head.Target.PowerMeterNo, report.Simulation[head.Target.HeadNo], head.Target.TargetPower, head.Target.Options.SettleTimeMs, head.Target.Options.MeasureCount, head.Target.Options.MeasureIntervalMs, head.Target.Options.Tolerance, head.Target.Options.MaxAttempts, head.State, sample.Attempt, sample.Angle, sample.Power, sample.Error, sample.Result, head.Message];
                    text.AppendLine(string.Join(',', fields.Select(value => "\"" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\"")));
                }
            }
            var temp = path + ".tmp";
            try { System.IO.File.WriteAllText(temp, text.ToString(), new UTF8Encoding(true)); System.IO.File.Move(temp, path); }
            finally { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
            return path;
        }

        public string GetPath(int headNo)
        {
            if (headNo is < 1 or > 8)
                throw new ArgumentOutOfRangeException(nameof(headNo), "PowerTable의 Head 번호는 1~8이어야 합니다.");
            return Path.Combine(_directory, $"HEAD{headNo:00}.csv");
        }

        /// <summary>
        /// Reads fresh table data, then returns an exact point or a linear interpolation.
        /// Ambiguous tables and powers outside the measured range are rejected; no extrapolation is performed.
        /// </summary>
        public double ResolveAttenuatorAngle(int headNo, double powerWatts, CancellationToken cancellationToken = default)
        {
            // Config/PowerTable ships with illustrative values; replace them with measured calibration
            // before real operation. ATT Min/Max SW Limits are independently editable in Setting.
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetPath(headNo);
            var context = $"HEAD {headNo:00} PowerTable 변환 실패 (요청 {powerWatts.ToString("R", CultureInfo.InvariantCulture)} W).\n파일: {path}";
            try
            {
                if (!double.IsFinite(powerWatts) || powerWatts < 0)
                    throw new InvalidDataException("요청 출력은 0 이상의 유한한 W 값이어야 합니다.");
                if (!System.IO.File.Exists(path))
                    throw new FileNotFoundException("PowerTable 파일이 없습니다.", path);
                CCsvParser.ValidateRequiredHeaders(path, "PowerTable", [["Attenuator_deg"], ["Power_W"]]);
                var rows = CCsvParser.Read(path);
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count < 2)
                    throw new InvalidDataException("PowerTable에는 서로 다른 각도의 측정값이 최소 2개 필요합니다.");
                var points = new List<(double Angle, double Power)>(rows.Count);
                for (var index = 0; index < rows.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = rows[index];
                    var angle = ReadNumber(row, "Attenuator_deg", index + 1);
                    var power = ReadNumber(row, "Power_W", index + 1);
                    if (angle is < 0 or > 360)
                        throw new InvalidDataException($"측정값 {index + 1}: ATT 각도는 0~360 deg 범위여야 합니다.");
                    if (power < 0)
                        throw new InvalidDataException($"측정값 {index + 1}: Power_W는 0 이상이어야 합니다.");
                    points.Add((angle, power));
                }

                points.Sort((left, right) => left.Angle.CompareTo(right.Angle));
                var direction = Math.Sign(points[1].Power - points[0].Power);
                for (var index = 1; index < points.Count; index++)
                {
                    if (points[index].Angle == points[index - 1].Angle)
                        throw new InvalidDataException($"ATT 각도 {Format(points[index].Angle)} deg가 중복되었습니다.");
                    if (direction == 0 || Math.Sign(points[index].Power - points[index - 1].Power) != direction)
                        throw new InvalidDataException("ATT 각도 순서에 따른 W 값은 계속 증가하거나 계속 감소해야 합니다. 같은 W 값 또는 방향이 바뀌는 구간이 있어 목표 각도를 결정할 수 없습니다.");
                }

                var minPower = Math.Min(points[0].Power, points[^1].Power);
                var maxPower = Math.Max(points[0].Power, points[^1].Power);
                if (powerWatts < minPower || powerWatts > maxPower)
                    throw new InvalidDataException($"요청 출력이 PowerTable 범위 {Format(minPower)}~{Format(maxPower)} W를 벗어났습니다.");
                for (var index = 0; index < points.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var point = points[index];
                    if (powerWatts == point.Power)
                        return point.Angle;
                    if (index == 0)
                        continue;
                    var previous = points[index - 1];
                    if (powerWatts > Math.Min(previous.Power, point.Power) && powerWatts < Math.Max(previous.Power, point.Power))
                    {
                        var fraction = (powerWatts - previous.Power) / (point.Power - previous.Power);
                        return previous.Angle + (point.Angle - previous.Angle) * fraction;
                    }
                }

                throw new InvalidDataException("요청 출력에 해당하는 ATT 각도를 찾을 수 없습니다.");
            }
            catch (Exception exception)when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"{context}\n{exception.Message}", exception);
            }
        }

        private static double ReadNumber(IReadOnlyDictionary<string, string> row, string name, int measurementNo)
        {
            var text = CCsvParser.Get(row, name);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidDataException($"측정값 {measurementNo}: {name}은 유한한 숫자여야 합니다. 소수점은 '.'을 사용하세요. 입력: {text}");
            return value;
        }

        private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
#endregion
#region Measurement
        public ST_POWER_MEASURE_START FindStart(int headNo, double targetPower, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var path = GetPath(headNo);
            if (!double.IsFinite(targetPower) || targetPower < 0)
                throw new ArgumentOutOfRangeException(nameof(targetPower));
            // The first simulation can consult the real table, but all simulated results go into Simulation/.
            if (!System.IO.File.Exists(path) && _initialDirectory is not null)
                path = Path.Combine(_initialDirectory, $"HEAD{headNo:00}.csv");
            try
            {
                if (!System.IO.File.Exists(path))
                    return new(null, "PowerTable 파일 없음");
                var points = LoadMeasurementPoints(path, token);
                var exact = points.Where(p => p.Power == targetPower).ToArray();
                if (exact.Length == 1)
                    return new(exact[0].Angle, "PowerTable 실측값");
                if (exact.Length > 1)
                    return new(null, "목표 W에 해당하는 ATT 각도가 여러 개임");
                var candidates = new List<double>();
                for (var i = 1; i < points.Count; i++)
                {
                    var a = points[i - 1];
                    var b = points[i];
                    if (targetPower > Math.Min(a.Power, b.Power) && targetPower < Math.Max(a.Power, b.Power))
                        candidates.Add(a.Angle + (targetPower - a.Power) * (b.Angle - a.Angle) / (b.Power - a.Power));
                }

                return candidates.Count == 1 ? new(candidates[0], "PowerTable 선형 보간") : new(null, candidates.Count == 0 ? "목표 W를 포함하는 PowerTable 구간 없음" : "목표 W에 해당하는 PowerTable 구간이 여러 개임");
            }
            catch (Exception error)when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new(null, $"PowerTable 조회 불가: {error.Message}");
            }
        }

        /// <summary>Keep the final measured W (not the requested W). Replace the same angle, otherwise insert.</summary>
        public void SaveMeasurement(int headNo, double angle, double measuredPower, CancellationToken token)
        {
            var path = GetPath(headNo);
            if (!double.IsFinite(angle) || angle is < 0 or > 360 || !double.IsFinite(measuredPower) || measuredPower < 0)
                throw new ArgumentException("PowerTable에는 유효한 ATT 각도(0~360°)와 측정 W(0 이상)만 저장할 수 있습니다.");
            lock (MeasurementLocks.GetOrAdd(path, _ => new object ()))
            {
                token.ThrowIfCancellationRequested();
                // Never copy the unmeasured seed curve into the simulation results.
                var points = System.IO.File.Exists(path) ? LoadMeasurementPoints(path, token) : [];
                var index = points.FindIndex(p => Math.Abs(p.Angle - angle) <= 0.000001);
                if (index >= 0)
                    points[index] = (angle, measuredPower);
                else
                    points.Add((angle, measuredPower));
                points.Sort((a, b) => a.Angle.CompareTo(b.Angle));
                var text = "Attenuator_deg,Power_W\r\n" + string.Join("\r\n", points.Select(p => $"{p.Angle.ToString("R", CultureInfo.InvariantCulture)},{p.Power.ToString("R", CultureInfo.InvariantCulture)}")) + "\r\n";
                Directory.CreateDirectory(_directory);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    System.IO.File.WriteAllText(temporary, text, new UTF8Encoding(false));
                    token.ThrowIfCancellationRequested();
                    // Atomic replacement also keeps the preceding calibration recoverable.
                    if (System.IO.File.Exists(path))
                        System.IO.File.Replace(temporary, path, path + ".bak");
                    else
                        System.IO.File.Move(temporary, path);
                }
                finally
                {
                    if (System.IO.File.Exists(temporary))
                        System.IO.File.Delete(temporary);
                }
            }
        }

        private static List<(double Angle, double Power)> LoadMeasurementPoints(string path, CancellationToken token)
        {
            CCsvParser.ValidateRequiredHeaders(path, "PowerTable", [["Attenuator_deg"], ["Power_W"]]);
            var rows = CCsvParser.Read(path);
            var points = new List<(double Angle, double Power)>();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                var angle = ReadNumber(row, "Attenuator_deg", points.Count + 1);
                var power = ReadNumber(row, "Power_W", points.Count + 1);
                if (angle is < 0 or > 360 || power < 0)
                    throw new InvalidDataException("PowerTable의 각도 또는 출력 범위가 올바르지 않습니다.");
                points.Add((angle, power));
            }

            points.Sort((a, b) => a.Angle.CompareTo(b.Angle));
            if (points.Select(p => p.Angle).Distinct().Count() != points.Count)
                throw new InvalidDataException("PowerTable에 중복 ATT 각도가 있습니다.");
            return points;
        }
#endregion
    }
}
