using System.Globalization;
using System.IO;
using System.Text;
using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;

namespace Drilling.Common.Interface;
[CCommType("Serial", "Bet")]
[CCommType("ModbusSerial", "Bet")]
internal sealed class CBeamExpander(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CSerialComm(data, option)
{
    private const int ResponseQuietTimeoutMs = 50;
    private const int ResponsePollIntervalMs = 5;
    private const int MaxResponseLength = 128;
    public static string Build(EN_BET_COMMAND command, double magnification = 0.0, double divergence = 0.0)
    {
        return command switch
        {
            EN_BET_COMMAND.InitMotor => "#I:",
            EN_BET_COMMAND.MoveManual => string.Join(":", "MOVE", magnification.ToString("F3", CultureInfo.InvariantCulture), divergence.ToString("F3", CultureInfo.InvariantCulture)),
            EN_BET_COMMAND.MoveMagnification => $"#2:{ToMotorStep(magnification).ToString(CultureInfo.InvariantCulture)}",
            EN_BET_COMMAND.MoveDivergence => $"#1:{ToMotorStep(divergence).ToString(CultureInfo.InvariantCulture)}",
            EN_BET_COMMAND.Refresh => "#8:",
            EN_BET_COMMAND.PollMagnificationPosition => "#8:",
            EN_BET_COMMAND.PollDivergencePosition => "#7:",
            EN_BET_COMMAND.ResetAlarm => "#0:",
            _ => ""
        };
    }

    public static string BuildLogText(EN_BET_COMMAND command, double magnification = 0.0, double divergence = 0.0)
    {
        return command switch
        {
            EN_BET_COMMAND.MoveManual => string.Join(" / ", $"#1:{ToMotorStep(divergence).ToString(CultureInfo.InvariantCulture)}", $"#2:{ToMotorStep(magnification).ToString(CultureInfo.InvariantCulture)}"),
            EN_BET_COMMAND.MoveMagnification => $"#2:{ToMotorStep(magnification).ToString(CultureInfo.InvariantCulture)}",
            EN_BET_COMMAND.MoveDivergence => $"#1:{ToMotorStep(divergence).ToString(CultureInfo.InvariantCulture)}",
            _ => Build(command, magnification, divergence)};
    }

    public static bool IsSuccessResponse(string response)
    {
        return !string.IsNullOrWhiteSpace(response) && !response.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase);
    }

    public static ST_BET_STATUS Apply(EN_BET_COMMAND command, double magnification, double divergence, string response, ST_BET_STATUS current, bool simulation)
    {
        var value = simulation ? CreateSimulationResponse(command, magnification, divergence, current) : response.Trim();
        if (!simulation && !IsSuccessResponse(value))
        {
            return current with
            {
                CommOk = false,
                LastError = ReadError(value),
                UpdatedAt = DateTimeOffset.Now
            };
        }

        var ok = current with
        {
            CommOk = true,
            LastError = EN_BET_ERROR.Ok,
            UpdatedAt = DateTimeOffset.Now
        };
        return command switch
        {
            EN_BET_COMMAND.InitMotor => ok with
            {
                IsMoving = false,
                MagHomeCompleted = true,
                DivHomeCompleted = true,
                LastCommand = "INIT"
            },
            EN_BET_COMMAND.MoveManual => ok with
            {
                CurrentMagnification = simulation ? magnification : ok.CurrentMagnification,
                TargetMagnification = magnification,
                CurrentDivergence = simulation ? divergence : ok.CurrentDivergence,
                TargetDivergence = divergence,
                MagnificationAxisPosition = simulation ? magnification : ok.MagnificationAxisPosition,
                DivergenceAxisPosition = simulation ? divergence : ok.DivergenceAxisPosition,
                IsMoving = !simulation,
                LastCommand = "MOVE"
            },
            EN_BET_COMMAND.MoveMagnification => ok with
            {
                CurrentMagnification = simulation ? magnification : ok.CurrentMagnification,
                TargetMagnification = magnification,
                MagnificationAxisPosition = simulation ? magnification : ok.MagnificationAxisPosition,
                IsMoving = !simulation,
                LastCommand = "MOVE MAG"
            },
            EN_BET_COMMAND.MoveDivergence => ok with
            {
                CurrentDivergence = simulation ? divergence : ok.CurrentDivergence,
                TargetDivergence = divergence,
                DivergenceAxisPosition = simulation ? divergence : ok.DivergenceAxisPosition,
                IsMoving = !simulation,
                LastCommand = "MOVE DIV"
            },
            EN_BET_COMMAND.Stop => ok with
            {
                IsMoving = false,
                LastCommand = "STOP"
            },
            EN_BET_COMMAND.ResetAlarm => ok with
            {
                AlarmOn = false,
                LastCommand = "RESET"
            },
            EN_BET_COMMAND.Refresh or EN_BET_COMMAND.PollMagnificationPosition => ok with
            {
                CurrentMagnification = ReadTaggedDouble(value, "M2", ok.CurrentMagnification),
                MagnificationAxisPosition = ReadTaggedDouble(value, "M2", ok.MagnificationAxisPosition),
                IsMoving = false
            },
            EN_BET_COMMAND.PollDivergencePosition => ok with
            {
                CurrentDivergence = ReadTaggedDouble(value, "M1", ok.CurrentDivergence),
                DivergenceAxisPosition = ReadTaggedDouble(value, "M1", ok.DivergenceAxisPosition),
                IsMoving = false
            },
            _ => ok
        };
    }

    public override async Task<string> Execute(string function, CancellationToken cancellationToken = default)
    {
        await SerialLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteBeamExpander(function, cancellationToken);
        }
        finally
        {
            SerialLock.Release();
        }
    }

    private async Task<string> ExecuteBeamExpander(string function, CancellationToken cancellationToken)
    {
        if (SerialPort is null || !SerialPort.IsOpen)
        {
            await Connect(cancellationToken);
        }

        if (SerialPort is null || !SerialPort.IsOpen)
        {
            return "";
        }

        LastSent = function;
        string SelectValue()
        {
            return ExecuteBeamExpander(function);
        }

        try
        {
            LastReceived = await Task.Run(SelectValue, cancellationToken);
            LastError = LastReceived.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase) ? LastReceived : "";
            SetResponseState(LastReceived);
            return LastReceived;
        }
        catch (Exception ex)when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
        {
            CloseSerialPort();
            SetError(ex);
            return "";
        }
    }

    private string ExecuteBeamExpander(string function)
    {
        var parts = function.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = function.Trim().ToUpperInvariant();
        if (text is "#I:" or "#I:!" or "#0:" or "#0:!")
        {
            return SendSelecting(text);
        }

        if (text.StartsWith("#1:", StringComparison.Ordinal) || text.StartsWith("#2:", StringComparison.Ordinal))
        {
            return SendSelecting(text);
        }

        if (IsPollingCommand(text))
        {
            return SendPolling(text);
        }

        return parts.Length > 0 ? parts[0].ToUpperInvariant() switch
        {
            "MOVE" when parts.Length >= 3 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mag) && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var div) => SendMove(mag, div),
            _ => "ERR:-2"
        } : "ERR:-2";
    }

    private string SendMove(double magnification, double divergence)
    {
        SerialPort!.DiscardInBuffer();
        SerialPort.DiscardOutBuffer();
        var divCommand = $"#1:{ToMotorStep(divergence).ToString(CultureInfo.InvariantCulture)}";
        var magCommand = $"#2:{ToMotorStep(magnification).ToString(CultureInfo.InvariantCulture)}";
        LastSent = $"{divCommand} / {magCommand}";
        if (!SendAndWaitAck(divCommand))
        {
            return "ERR:-2";
        }

        if (!SendAndWaitAck(magCommand))
        {
            return "ERR:-2";
        }

        return "OK";
    }

    private string SendSelecting(string command)
    {
        SerialPort!.DiscardInBuffer();
        SerialPort.DiscardOutBuffer();
        return SendAndWaitAck(command) ? "OK" : "ERR:-2";
    }

    private string SendPolling(string command)
    {
        var normalized = NormalizePollingCommand(command) switch
        {
            "7" => "M1",
            "8" => "M2",
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "ERR:-2";
        }

        SerialPort!.DiscardInBuffer();
        SerialPort.DiscardOutBuffer();
        SerialPort.Write(NormalizePollingWriteCommand(command));
        try
        {
            var response = ReadDeviceResponse();
            return TryReadBeamPosition(response, out var position) ? $"{normalized}:{position.ToString("F3", CultureInfo.InvariantCulture)}" : "ERR:-2";
        }
        catch (TimeoutException)
        {
            var response = SerialPort.ReadExisting();
            if (string.IsNullOrWhiteSpace(response))
            {
                return "ERR:-1";
            }

            return TryReadBeamPosition(response, out var position) ? $"{normalized}:{position.ToString("F3", CultureInfo.InvariantCulture)}" : "ERR:-2";
        }
    }

    private bool SendAndWaitAck(string command)
    {
        SerialPort!.Write(NormalizeAckWriteCommand(command));
        return true;
    }

    private static string NormalizeAckWriteCommand(string command)
    {
        var text = command.Trim();
        return text.EndsWith('!') ? text[..^1] : text;
    }

    private static string NormalizePollingWriteCommand(string command)
    {
        return NormalizePollingCommand(command) switch
        {
            "7" => "#7:",
            "8" => "#8:",
            _ => command
        };
    }

    private static bool IsPollingCommand(string command)
    {
        var normalized = NormalizePollingCommand(command);
        return normalized is "7" or "8";
    }

    private static string NormalizePollingCommand(string command)
    {
        var text = command.Trim().ToUpperInvariant();
        if (text.StartsWith("#7:", StringComparison.Ordinal))
        {
            return "7";
        }

        if (text.StartsWith("#8:", StringComparison.Ordinal))
        {
            return "8";
        }

        return "";
    }

    private string ReadDeviceResponse()
    {
        var builder = new StringBuilder();
        while (builder.Length < MaxResponseLength)
        {
            if (builder.Length > 0 && !WaitForNextResponseByte())
            {
                break;
            }

            var value = SerialPort!.ReadChar();
            if (value < 0)
            {
                break;
            }

            var character = (char)value;
            if (character is '\r' or '\n')
            {
                if (builder.Length > 0)
                {
                    break;
                }

                continue;
            }

            builder.Append(character);
            if (character == '!')
            {
                break;
            }
        }

        return builder.ToString();
    }

    private bool WaitForNextResponseByte()
    {
        if (SerialPort!.BytesToRead > 0)
        {
            return true;
        }

        var expiresAt = Environment.TickCount64 + ResponseQuietTimeoutMs;
        while (Environment.TickCount64 < expiresAt)
        {
            Thread.Sleep(ResponsePollIntervalMs);
            if (SerialPort.BytesToRead > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int ToMotorStep(double value)
    {
        return Math.Clamp((int)Math.Round(value), 0, 4500);
    }

    private static string CreateSimulationResponse(EN_BET_COMMAND command, double magnification, double divergence, ST_BET_STATUS current)
    {
        return command switch
        {
            EN_BET_COMMAND.PollMagnificationPosition or EN_BET_COMMAND.Refresh => $"M2:{current.CurrentMagnification.ToString("F3", CultureInfo.InvariantCulture)}",
            EN_BET_COMMAND.PollDivergencePosition => $"M1:{current.CurrentDivergence.ToString("F3", CultureInfo.InvariantCulture)}",
            EN_BET_COMMAND.MoveManual => $"MOVE:{magnification.ToString("F3", CultureInfo.InvariantCulture)}:{divergence.ToString("F3", CultureInfo.InvariantCulture)}",
            _ => "OK"
        };
    }

    private static EN_BET_ERROR ReadError(string response)
    {
        var value = response.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase) ? response[4..] : "";
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            return EN_BET_ERROR.Error;
        }

        return code switch
        {
            -99 => EN_BET_ERROR.NotSupported,
            -2 => EN_BET_ERROR.InvalidResponse,
            -1 => EN_BET_ERROR.Timeout,
            _ => EN_BET_ERROR.Error
        };
    }

    private static double ReadTaggedDouble(string response, string tag, double defaultValue)
    {
        var prefix = $"{tag}:";
        var value = response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? response[prefix.Length..] : response;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private static bool TryReadBeamPosition(string response, out double position)
    {
        var text = response.Trim();
        var colonIndex = text.IndexOf(':', StringComparison.Ordinal);
        var value = colonIndex >= 0 ? text[(colonIndex + 1)..] : text;
        bool MatchesChar(char character)
        {
            return char.IsDigit(character) || character == '-' || character == '+' || character == '.';
        }

        value = new string (value.TakeWhile(MatchesChar).ToArray());
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var step) || !double.IsFinite(step))
        {
            position = 0.0;
            return false;
        }

        position = step;
        return true;
    }
}

public enum EN_BET_COMMAND
{
    InitMotor,
    MoveManual,
    MoveMagnification,
    MoveDivergence,
    MoveTable,
    Stop,
    ResetAlarm,
    Refresh,
    PollMagnificationPosition,
    PollDivergencePosition
}

public enum EN_BET_ERROR
{
    Ok = 0,
    Error = 1,
    Timeout = -1,
    InvalidResponse = -2,
    NotSupported = -99
}

public sealed record ST_BET_STATUS(
    double CurrentMagnification, 
    double TargetMagnification, 
    double CurrentDivergence, 
    double TargetDivergence, 
    double MagnificationAxisPosition, 
    double DivergenceAxisPosition, 
    bool IsMoving, 
    bool MagHomeCompleted, 
    bool DivHomeCompleted, 
    bool AlarmOn, 
    bool CommOk = true, 
    EN_BET_ERROR LastError = EN_BET_ERROR.Ok, 
    DateTimeOffset? UpdatedAt = null, 
    string LastCommand = ""
);
public sealed record ST_BET_TABLE_DATA(int Index, int Magnification, int Divergence, string Description)
{
    private const double DefaultRowBeamSize = 32.64;
    public double SpotSize
    {
        get
        {
            return Magnification == 0.0 ? 0.001 : (DefaultRowBeamSize / Magnification) / 1000.0;
        }
    }
}
