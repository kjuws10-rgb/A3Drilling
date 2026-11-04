using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;

namespace Drilling.Common.Motion;
[CMotionControllerType("XPS", "XPS.NET", "XPS_NET", "NEWPORT_XPS")]
internal sealed class CXpsMotion(CInterfaceManager? interfaceManager, int deviceNo = 0) : CMotionController("XPS", interfaceManager, deviceNo)
{
    private const int MinusEndOfRunMask = 1 << 8;
    private const int PlusEndOfRunMask = 1 << 9;
    protected override string CommandPrefix
    {
        get
        {
            return "XPS";
        }
    }

    protected override string BuildAxisCommand(ST_MOTOR_DATA axis, EN_MOTION_COMMAND command, double parameter, double? parameter2 = null)
    {
        var commandText = command switch
        {
            EN_MOTION_COMMAND.ServoOn => "SERVO_ON",
            EN_MOTION_COMMAND.ServoOff => "SERVO_OFF",
            EN_MOTION_COMMAND.Home => "HOME",
            EN_MOTION_COMMAND.MoveAbs => $"MOVE_ABS:{FormatProtocolDouble(parameter, "F6")}",
            EN_MOTION_COMMAND.MoveRel => $"MOVE_REL:{FormatProtocolDouble(parameter, "F6")}",
            EN_MOTION_COMMAND.SetProfile => $"SET_PROFILE:{FormatProtocolDouble(parameter, "F6")}:{FormatProtocolDouble(RequireSecondaryParameter(command, parameter2), "F6")}",
            EN_MOTION_COMMAND.JogPositive => $"JOG:{FormatProtocolDouble(Math.Abs(parameter), "F6")}:{FormatProtocolDouble(Math.Abs(RequireSecondaryParameter(command, parameter2)), "F6")}",
            EN_MOTION_COMMAND.JogNegative => $"JOG:{FormatProtocolDouble(-Math.Abs(parameter), "F6")}:{FormatProtocolDouble(Math.Abs(RequireSecondaryParameter(command, parameter2)), "F6")}",
            EN_MOTION_COMMAND.JogStop => "JOG_STOP",
            EN_MOTION_COMMAND.Stop => "STOP",
            EN_MOTION_COMMAND.ResetAlarm => "RESET_ALARM",
            EN_MOTION_COMMAND.Refresh => "READ",
            _ => "READ"
        };
        return $"{CommandPrefix}:AXIS:{axis.Axis}:{axis.Name}:{GetGroupName(axis)}:{commandText}";
    }

    private static string GetGroupName(ST_MOTOR_DATA axis)
    {
        var axisName = axis.Name.Trim();
        var dotIndex = axisName.IndexOf('.', StringComparison.Ordinal);
        return dotIndex > 0 ? axisName[..dotIndex] : axisName;
    }

    protected override ST_MOTOR_AXIS_STATUS? TryParseAxisStatus(ST_MOTOR_DATA axis, string response)
    {
        var tokens = response.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 5 || !tokens[0].Equals("OK", StringComparison.OrdinalIgnoreCase) || !tokens[1].Equals("AXIS", StringComparison.OrdinalIgnoreCase) || !tokens[3].Equals("FPOS", StringComparison.OrdinalIgnoreCase) || !double.TryParse(tokens[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var position) || !double.IsFinite(position))
        {
            return null;
        }

        var statusText = ReadTokenValue(tokens, "GROUP_STATUS_TEXT");
        if (string.IsNullOrWhiteSpace(statusText) || !TryReadTokenInt(tokens, "MOTION_STATUS", out var motionStatus) || motionStatus is not (0 or 1) || !TryReadTokenInt(tokens, "HARDWARE_STATUS", out var hardwareStatus))
        {
            return null;
        }

        if (!TryReadTokenDouble(tokens, "FVEL", out var feedbackVelocity) || !TryReadTokenDouble(tokens, "FACCEL", out var feedbackAcceleration))
        {
            return null;
        }

        var alarmOn = ContainsAny(statusText, "FAULT", "ERROR", "ALARM", "KILLED");
        var servoOn = !ContainsAny(statusText, "DISABLE", "NOT INITIALIZED", "KILLED");
        var homeCompleted = !ContainsAny(statusText, "NOT REFERENCED", "NOT INITIALIZED", "HOMING");
        var moving = motionStatus == 1;
        var limitMinusOn = (hardwareStatus & MinusEndOfRunMask) != 0;
        var limitPlusOn = (hardwareStatus & PlusEndOfRunMask) != 0;
        var axisId = axis.Name.Trim().ToUpperInvariant();
        return new ST_MOTOR_AXIS_STATUS(
            axisId, 
            string.IsNullOrWhiteSpace(axis.DisplayName) ? axisId : axis.DisplayName, 
            position, 
            position, 
            moving ? double.NaN : position, 
            servoOn, 
            homeCompleted, 
            limitPlusOn, 
            limitMinusOn, 
            alarmOn, 
            feedbackVelocity, 
            feedbackAcceleration, 
            axis.Axis
        );
    }

    private static string ReadTokenValue(IReadOnlyList<string> tokens, string name)
    {
        for (var index = 0; index < tokens.Count - 1; index++)
        {
            if (tokens[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return tokens[index + 1];
            }
        }

        return "";
    }

    private static bool TryReadTokenDouble(IReadOnlyList<string> tokens, string name, out double value)
    {
        return double.TryParse(ReadTokenValue(tokens, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool TryReadTokenInt(IReadOnlyList<string> tokens, string name, out int value)
    {
        return int.TryParse(ReadTokenValue(tokens, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool ContainsAny(string value, params string[] patterns)
    {
        bool MatchesItem(string pattern)
        {
            return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        return patterns.Any(MatchesItem);
    }
}
