namespace Drilling.Common.Interface
{
    /// <summary>One independent sensor per Head; views only read immutable snapshots.</summary>
    public sealed class CPowerMeasureService : IAsyncDisposable
    {
        private readonly object _sync = new();
        private Dictionary<int, ST_POWER_MEASURE_HEAD> _heads = [];
        private CancellationTokenSource? _cancellation;
        private Task _completion = Task.CompletedTask;
        private string _stageState = "IDLE";
        private string _origin = "";
        public ST_POWER_MEASURE_SNAPSHOT GetSnapshot()
        {
            lock (_sync)
                return new(_cancellation is not null, _heads.Values.OrderBy(h => h.Target.HeadNo).ToArray(), _stageState) { Origin = _origin };
        }

        public static void ValidateTargets(IReadOnlyList<ST_POWER_MEASURE_TARGET> targets)
        {
            var batch = targets.OrderBy(h => h.HeadNo).ToArray();
            if (batch.Length == 0 || batch.Select(h => h.HeadNo).Distinct().Count() != batch.Length || batch.Any(h => h.HeadNo is < 1 or > 8 || h.PowerMeterNo < 0 || !double.IsFinite(h.TargetPower) || h.TargetPower <= 0))
                throw new ArgumentException("Head, PowerMeter 및 0보다 큰 Target Power를 확인해 주세요.");
            if (batch.Any(h => h.Options is not { } options || options.Mode is not ("ROUGH" or "FINE" or "DETAIL") || options.SettleTimeMs is < 0 or > 60000 || options.MeasureCount is < 1 or > 1000 || options.MeasureIntervalMs is < 0 or > 60000))
                throw new ArgumentException("측정 모드, 안정화 시간, 측정 횟수 및 간격을 확인해 주세요.");
            if (batch.Select(h => h.PowerMeterNo).Distinct().Count() != batch.Length)
                throw new ArgumentException("각 Head에는 서로 다른 전용 PowerMeter가 필요합니다.");
            if (batch.Any(h => !double.IsFinite(h.Options.Tolerance) || h.Options.Tolerance < 0 || h.Options.MaxAttempts is < 1 or > 100 || h.Options.CoolingTimeMs is < 0 or > 60000))
                throw new ArgumentException("허용오차, 최대 시도 횟수 및 냉각 시간을 확인해 주세요.");
        }
        public Task Start(IReadOnlyList<ST_POWER_MEASURE_TARGET> targets, IPowerMeasureSession session, IPowerMeasureTable? table = null, CancellationToken token = default, string origin = "MANUAL")
        {
            ValidateTargets(targets);
            var batch = targets.OrderBy(h => h.HeadNo).ToArray();
            ArgumentNullException.ThrowIfNull(session);
            lock (_sync)
            {
                if (_cancellation is not null)
                    throw new InvalidOperationException("PowerMeasure가 이미 진행 중입니다.");
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _cancellation = cancellation;
                _origin = origin;
                _stageState = "CHECKING";
                _heads = batch.ToDictionary(h => h.HeadNo, h => new ST_POWER_MEASURE_HEAD(h, "WAITING", null, null, "측정 준비", []));
                _completion = Task.Run(() => RunBatch(batch, session, table, cancellation));
                return _completion;
            }
        }

        public Task Stop()
        {
            lock (_sync)
            {
                _cancellation?.Cancel();
                return _completion;
            }
        }

        public async ValueTask DisposeAsync() => await Stop().ConfigureAwait(false);
        private async Task RunBatch(ST_POWER_MEASURE_TARGET[] targets, IPowerMeasureSession session, IPowerMeasureTable? table, CancellationTokenSource cancellation)
        {
            try
            {
                await session.CheckStageReady(cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                lock (_sync)
                    _stageState = "COMPLETE";
                await Task.WhenAll(targets.Select(target => RunHead(target, session, table, cancellation.Token))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)when (cancellation.IsCancellationRequested)
            {
                lock (_sync)
                    _stageState = "STOPPED";
            }
            catch (Exception error)
            {
                lock (_sync)
                {
                    _stageState = "ERROR";
                    foreach (var(head, state)in _heads.ToArray())
                        if (state.State == "WAITING")
                            _heads[head] = state with
                            {
                                State = "ERROR",
                                Message = $"측정 준비 실패: {error.Message}"};
                }
            }
            finally
            {
                lock (_sync)
                {
                    foreach (var(head, state)in _heads.ToArray())
                        if (state.State == "WAITING")
                            _heads[head] = state with
                            {
                                State = "STOPPED",
                                Message = "대기 작업 취소"
                            };
                    _cancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        private async Task<bool> RunHead(ST_POWER_MEASURE_TARGET target, IPowerMeasureSession session, IPowerMeasureTable? table, CancellationToken token)
        {
            var options = target.Options;
            var samples = new List<ST_POWER_MEASURE_SAMPLE>();
            var clean = true;
            var reachedTarget = false;
            try
            {
                token.ThrowIfCancellationRequested();
                State("POSITIONING", "Head 측정 위치 이동");
                await session.Prepare(target, token).ConfigureAwait(false);
                var start = table?.FindStart(target.HeadNo, target.TargetPower, token) ?? new(null, "PowerTable 값 없음");
                var angle = start.Angle ?? await session.GetAttenuatorPosition(target, token).ConfigureAwait(false);
                if (!double.IsFinite(angle) || angle is < 0 or > 360)
                    throw new InvalidOperationException("유효한 현재 ATT 위치를 확인할 수 없습니다.");
                Update(target.HeadNo, h => h with { InitialAngle = angle, StartDetail = start.Angle.HasValue ? $"PowerTable 보간/조회 → {angle:0.####}°" : $"현재 ATT {angle:0.####}°에서 측정 ({start.Detail})" });
                for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    // With no table estimate, measure at the current position before issuing any ATT move.
                    if (attempt > 1 || start.Angle.HasValue)
                    {
                        State("MOVING", $"ATT {angle:0.####}° 이동");
                        await session.MoveAttenuator(target, angle, token).ConfigureAwait(false);
                        angle = await session.GetAttenuatorPosition(target, token).ConfigureAwait(false);
                    }

                    Update(target.HeadNo, h => h with { Angle = angle });
                    var values = new List<double>();
                    await session.BeginMeasurement(target, token).ConfigureAwait(false);
                    try
                    {
                    State("SETTLING", "레이저 출사 · 안정화 대기");
                    await Task.Delay(options.SettleTimeMs, token).ConfigureAwait(false);
                    for (var i = 0; i < options.MeasureCount; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        State("MEASURING", $"Attempt {attempt} · Sample {i + 1}/{options.MeasureCount}");
                        var value = await session.ReadPower(target, token).ConfigureAwait(false);
                        if (!double.IsFinite(value) || value < 0)
                            throw new InvalidOperationException("유효하지 않은 파워 측정값입니다.");
                        values.Add(value);
                        if (i + 1 < options.MeasureCount)
                            await Task.Delay(options.MeasureIntervalMs, token).ConfigureAwait(false);
                    }

                    }
                    finally { await session.EndMeasurement(target).ConfigureAwait(false); }
                    if (options.CoolingTimeMs > 0)
                    {
                        State("COOLING", "레이저 OFF · 냉각 대기");
                        await Task.Delay(options.CoolingTimeMs, token).ConfigureAwait(false);
                    }
                    var power = values.Average();
                    var error = power - target.TargetPower;
                    var result = Math.Abs(error) <= options.Tolerance ? "OK" : error < 0 ? "LOW" : "HIGH";
                    samples.Add(new(attempt, angle, power, error, result));
                    Update(target.HeadNo, h => h with { Power = power, Samples = Array.AsReadOnly(samples.ToArray()) });
                    token.ThrowIfCancellationRequested();
                    if (result == "OK")
                    {
                        reachedTarget = true;
                        State("FINISHING", "목표 출력 도달 · 종료 처리 중");
                        break;
                    }

                    if (attempt == options.MaxAttempts)
                    {
                        State("NG", "최대 시도 횟수 내 목표 출력에 도달하지 못했습니다.");
                        break;
                    }

                    angle = NextAngle(samples, target.TargetPower);
                }
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
                State("STOPPED", "측정 중지");
            }
            catch (Exception error)
            {
                State("ERROR", error.Message);
            }
            finally
            {
                try
                {
                    await session.Finish(target).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    State("ERROR", $"측정 종료 처리 실패: {error.Message}");
                    clean = false;
                }
            }

            if (reachedTarget && clean)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (table is not null)
                    {
                        State("SAVING", "PowerTable 업데이트 중");
                        var sample = samples[^1];
                        table.SaveMeasurement(target.HeadNo, sample.Angle, sample.Power, token);
                        Update(target.HeadNo, h => h with { TableState = "UPDATED" });
                    }

                    State("OK", table is null ? "목표 출력 도달" : "목표 출력 도달 · PowerTable 업데이트 완료");
                }
                catch (OperationCanceledException)when (token.IsCancellationRequested)
                {
                    State("STOPPED", "측정 중지 · PowerTable 저장 취소");
                }
                catch (Exception error)
                {
                    Update(target.HeadNo, h => h with { TableState = "ERROR" });
                    State("ERROR", $"목표 출력 도달 / PowerTable 저장 실패: {error.Message}");
                }
            }

            return clean;
            void State(string state, string message) => Update(target.HeadNo, h => h with { State = state, Message = message });
        }

        private static double NextAngle(List<ST_POWER_MEASURE_SAMPLE> samples, double target)
        {
            var below = samples.Where(s => s.Power < target).OrderByDescending(s => s.Power).FirstOrDefault();
            var above = samples.Where(s => s.Power > target).OrderBy(s => s.Power).FirstOrDefault();
            if (below is not null && above is not null)
                return Math.Clamp(below.Angle + (target - below.Power) * (above.Angle - below.Angle) / (above.Power - below.Power), 0, 360);
            var best = samples.MinBy(s => Math.Abs(s.Error))!;
            var other = samples.Where(s => s.Angle != best.Angle && Math.Abs(s.Power - best.Power) > 1e-9).MinBy(s => Math.Abs(s.Angle - best.Angle));
            if (other is not null)
            {
                // Measured slope also supports decreasing ATT curves; do not assume larger angle means more W.
                var estimate = Math.Clamp(best.Angle + (target - best.Power) * (other.Angle - best.Angle) / (other.Power - best.Power), 0, 360);
                if (double.IsFinite(estimate) && samples.All(s => Math.Abs(s.Angle - estimate) > 0.000001))
                    return estimate;
            }

            // No slope yet: probe a nearby angle, widening only if the measured response is flat.
            for (var step = 10.0; step <= 640; step *= 2)
                foreach (var candidate in new[]
                {
                    Math.Clamp(best.Angle + step, 0, 360),
                    Math.Clamp(best.Angle - step, 0, 360)
                }

                )
                    if (samples.All(s => Math.Abs(s.Angle - candidate) > 0.000001))
                        return candidate;
            return best.Angle;
        }

        private void Update(int headNo, Func<ST_POWER_MEASURE_HEAD, ST_POWER_MEASURE_HEAD> update)
        {
            lock (_sync)
                _heads[headNo] = update(_heads[headNo]);
        }
    }

    public sealed record ST_POWER_MEASURE_TARGET(int HeadNo, int PowerMeterNo, double TargetPower, ST_POWER_MEASURE_OPTIONS Options);
    public sealed record ST_POWER_MEASURE_OPTIONS(string Mode, int SettleTimeMs, int MeasureCount, int MeasureIntervalMs)
    {
        public double? ToleranceW { get; init; }
        public int? MaximumAttempts { get; init; }
        public int CoolingTimeMs { get; init; } = 100;
        public double Tolerance => ToleranceW ?? (Mode switch
        {
            "ROUGH" => 0.05,
            "DETAIL" => 0.003,
            _ => 0.01
        });
        public int MaxAttempts => MaximumAttempts ?? (Mode switch
        {
            "ROUGH" => 8,
            "DETAIL" => 18,
            _ => 12
        });
    }

    public sealed record ST_POWER_MEASURE_SAMPLE(int Attempt, double Angle, double Power, double Error, string Result);
    public sealed record ST_POWER_MEASURE_HEAD(ST_POWER_MEASURE_TARGET Target, string State, double? Angle, double? Power, string Message, IReadOnlyList<ST_POWER_MEASURE_SAMPLE> Samples)
    {
        public double? InitialAngle { get; init; }
        public string StartDetail { get; init; } = "";
        public string TableState { get; init; } = "";
    }

    public sealed record ST_POWER_MEASURE_SNAPSHOT(bool IsRunning, IReadOnlyList<ST_POWER_MEASURE_HEAD> Heads, string StageState)
    {
        public string Origin { get; init; } = "";
    }
    /// <summary>A session owns sensor positioning, exclusive illumination and cleanup for each head.</summary>
    public interface IPowerMeasureSession
    {
        Task CheckStageReady(CancellationToken token);
        Task Prepare(ST_POWER_MEASURE_TARGET target, CancellationToken token);
        // Read a fresh, valid shared ATT snapshot (no position query from the view).
        Task<double> GetAttenuatorPosition(ST_POWER_MEASURE_TARGET target, CancellationToken token);
        Task MoveAttenuator(ST_POWER_MEASURE_TARGET target, double angle, CancellationToken token);
        Task BeginMeasurement(ST_POWER_MEASURE_TARGET target, CancellationToken token);
        Task EndMeasurement(ST_POWER_MEASURE_TARGET target);
        Task<double> ReadPower(ST_POWER_MEASURE_TARGET target, CancellationToken token);
        Task Finish(ST_POWER_MEASURE_TARGET target);
    }

    /// <summary>Demonstration only: no device interfaces, motion commands or laser output.</summary>
    public sealed class CPowerMeasureSimulationSession : IPowerMeasureSession
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> _angles = new();
        public Task CheckStageReady(CancellationToken token) => Task.Delay(10, token);
        public Task BeginMeasurement(ST_POWER_MEASURE_TARGET target, CancellationToken token) => Task.CompletedTask;
        public Task EndMeasurement(ST_POWER_MEASURE_TARGET target) => Task.CompletedTask;
        public Task Prepare(ST_POWER_MEASURE_TARGET target, CancellationToken token) => Task.Delay(150, token);
        public Task<double> GetAttenuatorPosition(ST_POWER_MEASURE_TARGET target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(_angles.GetOrAdd(target.HeadNo, 55.0));
        }

        public async Task MoveAttenuator(ST_POWER_MEASURE_TARGET target, double angle, CancellationToken token)
        {
            await Task.Delay(220, token).ConfigureAwait(false);
            _angles[target.HeadNo] = angle;
        }

        public async Task<double> ReadPower(ST_POWER_MEASURE_TARGET target, CancellationToken token)
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            return _angles[target.HeadNo] / 360 * (3.0 + target.HeadNo * 0.12);
        }

        public Task Finish(ST_POWER_MEASURE_TARGET target) => Task.CompletedTask;
    }

    public sealed record ST_POWER_MEASURE_START(double? Angle, string Detail);
    /// <summary>Calibration data for a single Head. Simulations must use a separate writable store.</summary>
    public interface IPowerMeasureTable
    {
        ST_POWER_MEASURE_START FindStart(int headNo, double targetPower, CancellationToken token);
        void SaveMeasurement(int headNo, double angle, double measuredPower, CancellationToken token);
    }
}
