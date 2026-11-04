namespace Drilling.Common.InterLock
{
    using System.Diagnostics;
    using System.Globalization;
    using Drilling.Common.Automation;
    using Drilling.Common.Alarm;
    using Drilling.Common.Interface;
    using Drilling.Common.InterLock;
    using Drilling.Common.Managers;
    using Drilling.Common.Motion;
    using Drilling.Common.Station;

    public sealed class CInterLockManager
    {
        private static readonly IReadOnlyList<ST_INTERLOCK_RULE> Rules = [
            Rule("Door", "DOOR_LOCK_SENSOR", true, "Closed", "Door is open", EN_INTERLOCK_LEVEL.Error, EN_INTERLOCK_TARGET.AutoRun | EN_INTERLOCK_TARGET.ManualMove | EN_INTERLOCK_TARGET.LaserOn), 
            Rule("EMS", "EMS", true, "Normal", "Emergency stop is active", EN_INTERLOCK_LEVEL.Error, EN_INTERLOCK_TARGET.AutoRun | EN_INTERLOCK_TARGET.ManualMove | EN_INTERLOCK_TARGET.LaserOn), 
            Rule("Key Switch", "KEY_SWITCH_AUTO", true, "AUTO", "Key switch is not AUTO", EN_INTERLOCK_LEVEL.Warn, EN_INTERLOCK_TARGET.AutoRun), 
            Rule("Laser Shutter", "LASER_SHUTTER_CLOSED", true, "SAFE", "Laser shutter is not closed", EN_INTERLOCK_LEVEL.Warn, EN_INTERLOCK_TARGET.LaserOn), 
            Rule("Leak Sensor", "LEAK_SENSOR", true, "OK", "Leak sensor is active", EN_INTERLOCK_LEVEL.Error, EN_INTERLOCK_TARGET.AutoRun | EN_INTERLOCK_TARGET.LaserOn), 
            Rule("Smoke Temp", "SMOKE_TEMP", true, "OK", "Smoke or over temperature input is active", EN_INTERLOCK_LEVEL.Error, EN_INTERLOCK_TARGET.AutoRun | EN_INTERLOCK_TARGET.LaserOn), 
            Rule("PM Lock", "PM_LOCK", true, "READY", "PM lock is active", EN_INTERLOCK_LEVEL.Error, EN_INTERLOCK_TARGET.AutoRun | EN_INTERLOCK_TARGET.ManualMove | EN_INTERLOCK_TARGET.LaserOn), 
            Rule("Scanner Ready", "SCANNER_READY", true, "READY", "Scanner ready signal is off", EN_INTERLOCK_LEVEL.Warn, EN_INTERLOCK_TARGET.AutoRun), 
            Rule("Vision Ready", "VISION_READY", true, "READY", "Vision ready signal is off", EN_INTERLOCK_LEVEL.Warn, EN_INTERLOCK_TARGET.AutoRun), 
            Rule("Panel In Position", "PANEL_IN_POSITION", true, "OK", "Panel is not in position", EN_INTERLOCK_LEVEL.Warn, EN_INTERLOCK_TARGET.AutoRun), 
            Rule("Vacuum Check", "VACUUM_CHECK", true, "OK", "Vacuum check is off", EN_INTERLOCK_LEVEL.Warn, EN_INTERLOCK_TARGET.AutoRun)
        ];
        public ST_INTERLOCK_SUMMARY Evaluate(ST_DEVICE_STATUS status)
        {
            global::Drilling.Common.InterLock.CInterLockManager.ST_INTERLOCK_CHECK EvaluateInterlockRule(global::Drilling.Common.InterLock.ST_INTERLOCK_RULE rule)
            {
                return EvaluateRule(rule, status);
            }

            ST_INTERLOCK_CHECK[] checks = status.Io.Count == 0 ? [] : Rules.Select(EvaluateInterlockRule).ToArray();
            global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM SelectInterlockItem(global::Drilling.Common.InterLock.CInterLockManager.ST_INTERLOCK_CHECK check)
            {
                return check.Item with
                {
                    BlocksAutoRun = !check.IsOk && check.Rule.Targets.HasFlag(EN_INTERLOCK_TARGET.AutoRun)
                };
            }

            var items = checks.Select(SelectInterlockItem).ToList();
            bool IsAlarmOn(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS axis)
            {
                return axis.AlarmOn;
            }

            var motionOk = !status.Motors.Any(IsAlarmOn);
            foreach (var axis in status.Motors)
            {
                var axisName = string.IsNullOrWhiteSpace(axis.Name) ? axis.AxisId : axis.Name;
                items.Add(new ST_INTERLOCK_ITEM(axisName, axis.AlarmOn ? EN_INTERLOCK_LEVEL.Error : EN_INTERLOCK_LEVEL.Ok, axis.AlarmOn ? "ALARM" : "READY", $"{axis.AxisId}: Alarm {(axis.AlarmOn ? "ON" : "OFF")}") { Group = "MOTION", BlocksAutoRun = axis.AlarmOn });
            }

            var betOk = !status.Bet.AlarmOn;
            items.Add(new ST_INTERLOCK_ITEM("BET", betOk ? EN_INTERLOCK_LEVEL.Ok : EN_INTERLOCK_LEVEL.Warn, betOk ? "READY" : "ALARM", betOk ? "Beam expander normal" : "Beam expander alarm is active") { BlocksAutoRun = !betOk });
            bool MatchesItem(global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM item)
            {
                return item.Level == EN_INTERLOCK_LEVEL.Error;
            }

            var hasError = items.Any(MatchesItem);
            var canAutoRun = IsTargetOk(checks, EN_INTERLOCK_TARGET.AutoRun) && motionOk && betOk;
            var canManualMove = IsTargetOk(checks, EN_INTERLOCK_TARGET.ManualMove) && motionOk;
            var canLaserOn = IsTargetOk(checks, EN_INTERLOCK_TARGET.LaserOn);
            return new ST_INTERLOCK_SUMMARY(canAutoRun, canManualMove, canLaserOn, hasError, items);
        }

        private static ST_INTERLOCK_CHECK EvaluateRule(ST_INTERLOCK_RULE rule, ST_DEVICE_STATUS status)
        {
            bool MatchesItem(global::Drilling.Common.Motion.ST_IO_STATUS channel)
            {
                return channel.Id.Equals(rule.IoId, StringComparison.OrdinalIgnoreCase) || channel.Address.Equals(rule.IoId, StringComparison.OrdinalIgnoreCase);
            }

            var io = status.Io.FirstOrDefault(MatchesItem);
            if (io is null)
            {
                return new ST_INTERLOCK_CHECK(rule, false, new ST_INTERLOCK_ITEM(rule.Signal, EN_INTERLOCK_LEVEL.Warn, "UNKNOWN", $"{rule.IoId} is not registered"));
            }

            var isOk = io.IsOn == rule.ExpectedOn;
            return new ST_INTERLOCK_CHECK(rule, isOk, new ST_INTERLOCK_ITEM(rule.Signal, isOk ? EN_INTERLOCK_LEVEL.Ok : rule.NgLevel, isOk ? rule.OkState : (io.IsOn ? "ON" : "OFF"), isOk ? io.Name : rule.NgDetail));
        }

        private static bool IsTargetOk(IEnumerable<ST_INTERLOCK_CHECK> checks, EN_INTERLOCK_TARGET target)
        {
            bool ShouldIncludeItem(global::Drilling.Common.InterLock.CInterLockManager.ST_INTERLOCK_CHECK check)
            {
                return check.Rule.Targets.HasFlag(target);
            }

            bool IsIsOk(global::Drilling.Common.InterLock.CInterLockManager.ST_INTERLOCK_CHECK check)
            {
                return check.IsOk;
            }

            return checks.Where(ShouldIncludeItem).All(IsIsOk);
        }

        private static ST_INTERLOCK_RULE Rule(string signal, string ioId, bool expectedOn, string okState, string ngDetail, EN_INTERLOCK_LEVEL ngLevel, EN_INTERLOCK_TARGET targets)
        {
            return new ST_INTERLOCK_RULE(signal, ioId, expectedOn, okState, ngDetail, ngLevel, targets);
        }

        private sealed record ST_INTERLOCK_CHECK(ST_INTERLOCK_RULE Rule, bool IsOk, ST_INTERLOCK_ITEM Item);
#region AUTO - Head USE 판정
        public static IReadOnlyList<ST_INTERLOCK_ITEM> EvaluateHeadUse(IReadOnlyDictionary<string, string>? parameters) => Enumerable.Range(1, 8).Select(head =>
        {
            var key = $"H{head:00}_USE";
            var value = parameters?.GetValueOrDefault(key)?.Trim();
            var enabled = value?.ToUpperInvariant()is "ON" or "TRUE" or "1" or "YES";
            var state = enabled ? "USE" : string.IsNullOrWhiteSpace(value) ? "NOT CONFIGURED" : "NOT USE";
            return new ST_INTERLOCK_ITEM($"H{head:00} USE", enabled ? EN_INTERLOCK_LEVEL.Ok : EN_INTERLOCK_LEVEL.Error, state, $"{key} = {(string.IsNullOrWhiteSpace(value) ? "미설정" : value)}. AUTO는 HEAD 1~8이 모두 USE여야 합니다.")
            {
                HeadNo = head,
                BlocksAutoRun = !enabled
            };
        }).ToArray();
#endregion
#region DOE Z - 현재 위치 판정
        public static IReadOnlyList<ST_INTERLOCK_ITEM> EvaluateDoeZ(IReadOnlyDictionary<string, string> parameters, Func<ST_SCANNER_AXIS_SETTING, ST_AUTOMATION_STATUS_SNAPSHOT<ST_AUTOMATION_AXIS_STATUS>> readSnapshot)
        {
            var items = new List<ST_INTERLOCK_ITEM>();
            for (var head = 1; head <= 8; head++)
            {
                var axis = CScannerAxisConfiguration.BuildAxisDefinitions(parameters, head).FirstOrDefault(axis => axis.AxisName == "Z" && axis.IsUsed);
                if (axis.HeadNo == 0)
                    continue;
                void Add(bool ready, string state, string detail) => items.Add(new("DOE Z Position", ready ? EN_INTERLOCK_LEVEL.Ok : EN_INTERLOCK_LEVEL.Error, state, detail) { Group = "HEAD", HeadNo = head, BlocksAutoRun = !ready });
                if (!string.IsNullOrEmpty(axis.ConfigurationError))
                {
                    Add(false, "AXIS NG", axis.ConfigurationError);
                    continue;
                }

                if (!ReadDoeZNumber(parameters, $"H{head:00}_DOE_Z_POSITION", out var target))
                {
                    Add(false, "TARGET NG", "Recipe의 DOE Z 목표 위치를 확인해 주세요.");
                    continue;
                }

                if (!ReadDoeZNumber(parameters, "DOE_Z_TOLERANCE", out var tolerance) || tolerance < 0)
                {
                    Add(false, "SETTING NG", "Setting의 DOE Z Tolerance는 0 이상의 유한한 값이어야 합니다.");
                    continue;
                }

                var snapshot = readSnapshot(axis);
                var age = snapshot.CollectedTimestamp > 0 ? Stopwatch.GetElapsedTime(snapshot.CollectedTimestamp) : DateTimeOffset.UtcNow - snapshot.UpdatedAt;
                var fresh = snapshot.UpdatedAt != default && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(5);
                if (snapshot.State != "OK" || snapshot.Value is not { } status || !fresh)
                {
                    Add(false, snapshot.State == "OK" ? "STALE" : snapshot.State, $"{axis.AxisReference}: 유효한 현재 위치 스냅샷이 필요합니다. {snapshot.Detail}");
                    continue;
                }

                var position = status.PositionFeedback;
                var ready = double.IsFinite(position) && Math.Abs(position - target) <= tolerance + 1e-9;
                Add(ready, ready ? "OK" : "POSITION NG", FormattableString.Invariant($"{axis.AxisReference}: Current {position:0.######} mm / Recipe {target:0.######} mm; Tolerance ±{tolerance:0.######} mm"));
            }

            return items;
        }

        private static bool ReadDoeZNumber(IReadOnlyDictionary<string, string> parameters, string key, out double value)
        {
            value = 0;
            return parameters.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }
#endregion

#region Laser·BET·ATT - 레시피 준비 판정
        public const double OpticPositionTolerance = 0.01;
        public static IReadOnlyList<ST_INTERLOCK_ITEM> EvaluateOptics(
            IReadOnlyDictionary<string, string> parameters, 
            IReadOnlyList<ST_INTERFACE_DATA> devices, 
            Func<int, ST_SHARED_DEVICE_SNAPSHOT<ST_LASER_STATUS>> laser, 
            Func<int, ST_SHARED_DEVICE_SNAPSHOT<ST_BET_STATUS>> bet, 
            Func<int, ST_ATTENUATOR_STATUS_SNAPSHOT> attenuator
        )
        {
            var items = new List<ST_INTERLOCK_ITEM>();
            for (var head = 1; head <= 8; head++)
            {
                if (parameters.TryGetValue($"H{head:00}_USE", out var use) && use.Trim().ToUpperInvariant()is "OFF" or "FALSE" or "0" or "NO")
                    continue;
                var name = $"H{head:00}";
                void Add(string signal, bool ready, string detail, string state = "NOT READY") => items.Add(new($"{name} {signal}", ready ? EN_INTERLOCK_LEVEL.Ok : EN_INTERLOCK_LEVEL.Error, ready ? "READY" : state, detail) { HeadNo = head, BlocksAutoRun = !ready });
                bool Registered(EN_EQP_MODULE module) => devices.Any(device => device.Device == module && device.Number == head - 1);
                if (Registered(EN_EQP_MODULE.TalonLaser))
                {
                    var laserStatus = laser(head - 1);
                    var laserValid = laserStatus is { State: "OK", Value: not null };
                    var value = laserStatus.Value;
                    Add("Laser ON", laserValid && value!.PowerOn, laserValid ? $"Laser={(value!.PowerOn ? "ON" : "OFF")}" : laserStatus.Detail, laserValid ? "OFF" : laserStatus.State);
                    Add("Laser Gate", laserValid && value!.GateOn, laserValid ? $"Gate={(value!.GateOn ? "OPEN" : "CLOSE")}" : laserStatus.Detail, laserValid ? "CLOSE" : laserStatus.State);
                    Add("Laser Shutter", laserValid && value!.ShutterOpen, laserValid ? $"Shutter={(value!.ShutterOpen ? "OPEN" : "CLOSE")}" : laserStatus.Detail, laserValid ? "CLOSE" : laserStatus.State);
                }

                if (Registered(EN_EQP_MODULE.Bet))
                {
                    if (!ReadOpticNumber(parameters, $"{name}_BET_MAGNIFICATION", out var targetMag) || !ReadOpticNumber(parameters, $"{name}_BET_DIVERGENCE", out var targetDiv))
                        Add("BET Position", false, "레시피의 BET MAG/DIV 목표값을 확인해 주세요.", "TARGET NG");
                    else
                    {
                        var status = bet(head - 1);
                        var current = status.Value;
                        var ready = status.State == "OK" && current is { CommOk: true, AlarmOn: false, IsMoving: false, LastError: EN_BET_ERROR.Ok } && NearOpticPosition(current.CurrentMagnification, targetMag) && NearOpticPosition(current.CurrentDivergence, targetDiv);
                        Add(
                            "BET Position", 
                            ready, 
                            current is null ? status.Detail : FormattableString.Invariant($"MAG {current.CurrentMagnification:0.###} / Recipe {targetMag:0.###}, DIV {current.CurrentDivergence:0.###} / Recipe {targetDiv:0.###}; Tolerance ±{OpticPositionTolerance}; Moving={current.IsMoving}, Alarm={current.AlarmOn}, Error={current.LastError}"), 
                            status.State == "OK" ? "POS NG" : status.State
                        );
                    }
                }

                if (Registered(EN_EQP_MODULE.Attenuator))
                {
                    if (!ReadOpticNumber(parameters, $"{name}_ATTENUATOR_POSITION", out var targetAtt))
                        Add("ATT Position", false, "레시피의 ATT 목표각도를 확인해 주세요.", "TARGET NG");
                    else
                    {
                        var status = attenuator(head - 1);
                        var current = status?.Value;
                        var ready = status is { IsValid: true, IsInPosition: true } && status.IsFresh(TimeSpan.FromSeconds(2)) && current is not null && current.CommandState.Trim().ToUpperInvariant()is not ("MOVING" or "HOMING" or "BUSY") && NearOpticPosition(current.CurrentPosition, targetAtt);
                        Add(
                            "ATT Position", 
                            ready, 
                            current is null ? status?.Detail ?? "ATT가 등록되지 않았습니다." : FormattableString.Invariant($"Current {current.CurrentPosition:0.###}° / Recipe {targetAtt:0.###}°; Tolerance ±{OpticPositionTolerance}°; InPosition={status!.IsInPosition}, State={current.CommandState}"), 
                            status is { IsValid: true } && !status.IsFresh(TimeSpan.FromSeconds(2)) ? "STALE" : status is { IsValid: true } ? "POS NG" : status?.State ?? "N/C"
                        );
                    }
                }
            }

            return items;
        }

        private static bool ReadOpticNumber(IReadOnlyDictionary<string, string> parameters, string key, out double value)
        {
            value = 0;
            return parameters.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }

        private static bool NearOpticPosition(double current, double target) => double.IsFinite(current) && Math.Abs(current - target) <= OpticPositionTolerance + 1e-9;
#endregion
    }

    [Flags]
    public enum EN_INTERLOCK_TARGET
    {
        None = 0,
        AutoRun = 1,
        ManualMove = 2,
        LaserOn = 4
    }

    public enum EN_INTERLOCK_LEVEL
    {
        Ok,
        Warn,
        Error
    }

    public sealed record ST_INTERLOCK_ITEM(string Signal, EN_INTERLOCK_LEVEL Level, string State, string Detail)
    {
        public string Group { get; init; } = "";
        public bool BlocksAutoRun { get; init; }
        public int? HeadNo { get; init; }
    }

    public sealed record ST_INTERLOCK_RULE(string Signal, string IoId, bool ExpectedOn, string OkState, string NgDetail, EN_INTERLOCK_LEVEL NgLevel, EN_INTERLOCK_TARGET Targets);
    public sealed record ST_INTERLOCK_SUMMARY(bool CanAutoRun, bool CanManualMove, bool CanLaserOn, bool HasError, IReadOnlyList<ST_INTERLOCK_ITEM> Items);
}
