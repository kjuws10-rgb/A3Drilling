using System.Globalization;
using System.IO;
using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;

namespace Drilling.Common.Interface;
[CCommType("Serial", "Attenuator")]
[CCommType("ModbusSerial", "Attenuator")]
internal sealed class CConex_AGP(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CSerialComm(data, option)
{
    public static string Build(EN_ATTENUATOR_COMMAND command, double parameter = 0.0)
    {
        return command switch
        {
            EN_ATTENUATOR_COMMAND.MoveAbs => $"PA:{FormatPositionParameter(parameter)}",
            EN_ATTENUATOR_COMMAND.MoveRel => $"PR:{FormatPositionParameter(parameter)}",
            EN_ATTENUATOR_COMMAND.Home => "OR",
            EN_ATTENUATOR_COMMAND.Stop => "ST",
            EN_ATTENUATOR_COMMAND.ResetController => "RS",
            EN_ATTENUATOR_COMMAND.Refresh => "TP",
            EN_ATTENUATOR_COMMAND.PollCurrentPosition => "TP",
            EN_ATTENUATOR_COMMAND.PollTargetPosition => "TH",
            EN_ATTENUATOR_COMMAND.PollState => "TS",
            EN_ATTENUATOR_COMMAND.GetHomeType => "HT?",
            EN_ATTENUATOR_COMMAND.SetHomeType => $"HT:{FormatIntegerCommandParameter(parameter)}",
            EN_ATTENUATOR_COMMAND.GetNegativeSoftwareLimit => "SL?",
            EN_ATTENUATOR_COMMAND.SetNegativeSoftwareLimit => $"SL:{FormatCommandParameter(parameter)}",
            EN_ATTENUATOR_COMMAND.GetPositiveSoftwareLimit => "SR?",
            EN_ATTENUATOR_COMMAND.SetPositiveSoftwareLimit => $"SR:{FormatCommandParameter(parameter)}",
            EN_ATTENUATOR_COMMAND.GetIGain => "KI?",
            EN_ATTENUATOR_COMMAND.SetIGain => $"KI:{FormatCommandParameter(parameter)}",
            EN_ATTENUATOR_COMMAND.GetPGain => "KP?",
            EN_ATTENUATOR_COMMAND.SetPGain => $"KP:{FormatCommandParameter(parameter)}",
            EN_ATTENUATOR_COMMAND.GetInterpolationFactor => "IF?",
            EN_ATTENUATOR_COMMAND.SetInterpolationFactor => $"IF:{FormatCommandParameter(parameter)}",
            _ => ""
        };
    }

    public static bool IsSuccessResponse(string response)
    {
        return !string.IsNullOrWhiteSpace(response) && !response.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase);
    }

    public static ST_ATTENUATOR_STATUS Apply(EN_ATTENUATOR_COMMAND command, double parameter, string response, ST_ATTENUATOR_STATUS current, bool simulation)
    {
        var value = simulation ? CreateSimulationResponse(command, parameter, current) : response.Trim();
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
            LastError = EN_CONEX_AGP_ERROR.Ok,
            UpdatedAt = DateTimeOffset.Now
        };
        return command switch
        {
            EN_ATTENUATOR_COMMAND.MoveAbs => ok with
            {
                CurrentPosition = simulation ? parameter : ok.CurrentPosition,
                TargetPosition = parameter,
                CommandState = simulation ? "READY" : "MOVING"
            },
            EN_ATTENUATOR_COMMAND.MoveRel => ok with
            {
                CurrentPosition = simulation ? ok.CurrentPosition + parameter : ok.CurrentPosition,
                TargetPosition = simulation ? ok.CurrentPosition + parameter : ok.TargetPosition + parameter,
                CommandState = simulation ? "READY" : "MOVING"
            },
            EN_ATTENUATOR_COMMAND.Home => ok with
            {
                CurrentPosition = simulation ? 0.0 : ok.CurrentPosition,
                TargetPosition = simulation ? 0.0 : ok.TargetPosition,
                CommandState = simulation ? "READY" : "HOMING"
            },
            EN_ATTENUATOR_COMMAND.Stop => ok with
            {
                CommandState = "STOP"
            },
            EN_ATTENUATOR_COMMAND.ResetController => ok with
            {
                CommandState = "NOT_REFERENCED"
            },
            EN_ATTENUATOR_COMMAND.Refresh or EN_ATTENUATOR_COMMAND.PollCurrentPosition => ok with
            {
                CurrentPosition = ReadTaggedDouble(value, "TP", ok.CurrentPosition),
                CommandState = IsMovingState(ok.CommandState) ? ok.CommandState : "READY"
            },
            EN_ATTENUATOR_COMMAND.PollTargetPosition => ok with
            {
                TargetPosition = ReadTaggedDouble(value, "TH", ok.TargetPosition)
            },
            EN_ATTENUATOR_COMMAND.PollState => ok with
            {
                CommandState = ReadControllerState(value, ok.CommandState)
            },
            EN_ATTENUATOR_COMMAND.GetHomeType => ok with
            {
                HomeType = ReadTaggedInt(value, "HT", ok.HomeType)
            },
            EN_ATTENUATOR_COMMAND.SetHomeType => ok with
            {
                HomeType = ValidateInt32CommandParameter(parameter)
            },
            EN_ATTENUATOR_COMMAND.GetNegativeSoftwareLimit => ok with
            {
                NegativeSoftwareLimit = ReadTaggedNullableDouble(value, "SL", ok.NegativeSoftwareLimit)
            },
            EN_ATTENUATOR_COMMAND.SetNegativeSoftwareLimit => ok with
            {
                NegativeSoftwareLimit = NormalizeStoredCommandParameter(parameter)
            },
            EN_ATTENUATOR_COMMAND.GetPositiveSoftwareLimit => ok with
            {
                PositiveSoftwareLimit = ReadTaggedNullableDouble(value, "SR", ok.PositiveSoftwareLimit)
            },
            EN_ATTENUATOR_COMMAND.SetPositiveSoftwareLimit => ok with
            {
                PositiveSoftwareLimit = NormalizeStoredCommandParameter(parameter)
            },
            EN_ATTENUATOR_COMMAND.GetIGain => ok with
            {
                IGain = ReadTaggedNullableDouble(value, "KI", ok.IGain)
            },
            EN_ATTENUATOR_COMMAND.SetIGain => ok with
            {
                IGain = NormalizeStoredCommandParameter(parameter)
            },
            EN_ATTENUATOR_COMMAND.GetPGain => ok with
            {
                PGain = ReadTaggedNullableDouble(value, "KP", ok.PGain)
            },
            EN_ATTENUATOR_COMMAND.SetPGain => ok with
            {
                PGain = NormalizeStoredCommandParameter(parameter)
            },
            EN_ATTENUATOR_COMMAND.GetInterpolationFactor => ok with
            {
                InterpolationFactor = ReadTaggedNullableDouble(value, "IF", ok.InterpolationFactor)
            },
            EN_ATTENUATOR_COMMAND.SetInterpolationFactor => ok with
            {
                InterpolationFactor = NormalizeStoredCommandParameter(parameter)
            },
            _ => ok
        };
    }

    public override async Task<string> Execute(string function, CancellationToken cancellationToken = default)
    {
        await SerialLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteConex(function, cancellationToken);
        }
        finally
        {
            SerialLock.Release();
        }
    }

    private async Task<string> ExecuteConex(string function, CancellationToken cancellationToken)
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
            return ExecuteConex(function);
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

    private string ExecuteConex(string function)
    {
        var parts = function.Split(':', StringSplitOptions.TrimEntries);
        var text = function.Trim().ToUpperInvariant();
        if (text.EndsWith("?", StringComparison.Ordinal))
        {
            var queryCommand = text[..^1];
            return IsParameterQueryCommand(queryCommand) ? SendPolling(text) : "ERR:-2";
        }

        if (IsPollingCommand(text))
        {
            return SendPolling(text);
        }

        var command = parts.Length > 0 ? parts[0].ToUpperInvariant() : text;
        return command switch
        {
            "PA" when IsSingleValueCommand(parts) => SendSelecting("PA", parts[1]),
            "PR" when IsSingleValueCommand(parts) => SendSelecting("PR", parts[1]),
            "OR" when IsNoValueCommand(parts) => SendSelecting("OR", ""),
            "ST" when IsNoValueCommand(parts) => SendSelecting("ST", ""),
            "RS" when IsNoValueCommand(parts) => SendSelecting("RS", ""),
            "HT" when IsSingleValueCommand(parts) => SendSelecting("HT", parts[1]),
            "SL" when IsSingleValueCommand(parts) => SendSelecting("SL", parts[1]),
            "SR" when IsSingleValueCommand(parts) => SendSelecting("SR", parts[1]),
            "KI" when IsSingleValueCommand(parts) => SendSelecting("KI", parts[1]),
            "KP" when IsSingleValueCommand(parts) => SendSelecting("KP", parts[1]),
            "IF" when IsSingleValueCommand(parts) => SendSelecting("IF", parts[1]),
            _ => "ERR:-2"
        };
    }

    private static bool IsNoValueCommand(string[] parts)
    {
        return parts.Length == 1;
    }

    private static bool IsSingleValueCommand(string[] parts)
    {
        return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]);
    }

    private string SendSelecting(string command, string value)
    {
        SerialPort!.DiscardInBuffer();
        SerialPort.DiscardOutBuffer();
        var tx = FormatDeviceCommand(command, value);
        LastSent = tx.TrimEnd('\r', '\n');
        SerialPort.Write(tx);
        if (command.Equals("RS", StringComparison.OrdinalIgnoreCase))
        {
            return "OK";
        }

        Thread.Sleep(50);
        var errorQuery = FormatDeviceCommand("TE", "");
        SerialPort.Write(errorQuery);
        try
        {
            var response = SerialPort.ReadLine();
            if (!TryParseResponse(response, out _, out var responseCommand, out var errorCode) || !responseCommand.Equals("TE", StringComparison.OrdinalIgnoreCase))
            {
                return "ERR:-2";
            }

            var normalizedError = errorCode.Trim();
            return normalizedError.Equals("@", StringComparison.Ordinal) ? "OK" : $"ERR:TE:{normalizedError}";
        }
        catch (TimeoutException)
        {
            return "ERR:-1";
        }
    }

    private string SendPolling(string command)
    {
        SerialPort!.DiscardInBuffer();
        SerialPort.DiscardOutBuffer();
        var tx = FormatDeviceCommand(command, "");
        LastSent = tx.TrimEnd('\r', '\n');
        SerialPort.Write(tx);
        try
        {
            var response = SerialPort.ReadLine();
            return TryParseResponse(response, out _, out var responseCommand, out var value) ? responseCommand.Equals("TS", StringComparison.OrdinalIgnoreCase) ? $"TS:{ReadTsError(value)}:{ReadTsState(value)}" : $"{responseCommand}:{value.Trim()}" : "ERR:-2";
        }
        catch (TimeoutException)
        {
            var response = SerialPort.ReadExisting().Trim();
            if (string.IsNullOrWhiteSpace(response))
            {
                return "ERR:-1";
            }

            return TryParseResponse(response, out _, out var responseCommand, out var value) ? responseCommand.Equals("TS", StringComparison.OrdinalIgnoreCase) ? $"TS:{ReadTsError(value)}:{ReadTsState(value)}" : $"{responseCommand}:{value.Trim()}" : "ERR:-2";
        }
    }

    private string FormatDeviceCommand(string command, string value)
    {
        var address = ReadControllerAddress();
        return $"{address.ToString(CultureInfo.InvariantCulture)}{command}{value}\r\n";
    }

    private static bool IsPollingCommand(string command)
    {
        return command.Equals("TP", StringComparison.OrdinalIgnoreCase) || command.Equals("TH", StringComparison.OrdinalIgnoreCase) || command.Equals("TS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsParameterQueryCommand(string command)
    {
        return command.Equals("HT", StringComparison.OrdinalIgnoreCase) || command.Equals("SL", StringComparison.OrdinalIgnoreCase) || command.Equals("SR", StringComparison.OrdinalIgnoreCase) || command.Equals("KI", StringComparison.OrdinalIgnoreCase) || command.Equals("KP", StringComparison.OrdinalIgnoreCase) || command.Equals("IF", StringComparison.OrdinalIgnoreCase);
    }

    private int ReadControllerAddress()
    {
        if (Data.Extra is not null)
        {
            foreach (var key in new[]
            {
                "ADDRESS",
                "DEV_ADDRESS",
                "CONTROLLER_ADDRESS"
            }

            )
            {
                if (Data.Extra.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var address) && address is >= 1 and <= 31)
                {
                    return address;
                }
            }
        }

        return 1;
    }

    private static bool TryParseResponse(string response, out int address, out string command, out string value)
    {
        address = 0;
        command = "";
        value = "";
        var text = response.Trim();
        if (text.Length < 3 || !char.IsDigit(text[0]))
        {
            return false;
        }

        var twoDigitAddress = text.Length >= 4 && char.IsDigit(text[1]);
        var addressLength = twoDigitAddress ? 2 : 1;
        if (text.Length < addressLength + 2 || !int.TryParse(text[..addressLength], NumberStyles.Integer, CultureInfo.InvariantCulture, out address))
        {
            return false;
        }

        command = text.Substring(addressLength, 2);
        value = text.Length > addressLength + 2 ? text[(addressLength + 2)..] : "";
        return true;
    }

    private static string CreateSimulationResponse(EN_ATTENUATOR_COMMAND command, double parameter, ST_ATTENUATOR_STATUS current)
    {
        return command switch
        {
            EN_ATTENUATOR_COMMAND.PollCurrentPosition or EN_ATTENUATOR_COMMAND.Refresh => $"TP:{current.CurrentPosition.ToString("F3", CultureInfo.InvariantCulture)}",
            EN_ATTENUATOR_COMMAND.PollTargetPosition => $"TH:{current.TargetPosition.ToString("F3", CultureInfo.InvariantCulture)}",
            EN_ATTENUATOR_COMMAND.PollState => "TS:0:READY",
            EN_ATTENUATOR_COMMAND.GetHomeType => $"HT:{(current.HomeType ?? 4).ToString(CultureInfo.InvariantCulture)}",
            EN_ATTENUATOR_COMMAND.GetNegativeSoftwareLimit => $"SL:{(current.NegativeSoftwareLimit ?? 0.0).ToString("F3", CultureInfo.InvariantCulture)}",
            EN_ATTENUATOR_COMMAND.GetPositiveSoftwareLimit => $"SR:{(current.PositiveSoftwareLimit ?? 360.0).ToString("F3", CultureInfo.InvariantCulture)}",
            EN_ATTENUATOR_COMMAND.GetIGain => $"KI:{(current.IGain ?? 800.0).ToString("F3", CultureInfo.InvariantCulture)}",
            EN_ATTENUATOR_COMMAND.GetPGain => $"KP:{(current.PGain ?? 10.0).ToString("F3", CultureInfo.InvariantCulture)}",
            EN_ATTENUATOR_COMMAND.GetInterpolationFactor => $"IF:{(current.InterpolationFactor ?? 1000.0).ToString("F3", CultureInfo.InvariantCulture)}",
            _ => "OK"
        };
    }

    private static EN_CONEX_AGP_ERROR ReadError(string response)
    {
        var value = response.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase) ? response[4..] : "";
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            return EN_CONEX_AGP_ERROR.Error;
        }

        return code switch
        {
            -99 => EN_CONEX_AGP_ERROR.NotSupported,
            -2 => EN_CONEX_AGP_ERROR.InvalidResponse,
            -1 => EN_CONEX_AGP_ERROR.Timeout,
            _ => EN_CONEX_AGP_ERROR.Error
        };
    }

    private static double ReadTaggedDouble(string response, string tag, double defaultValue)
    {
        return ReadTaggedNullableDouble(response, tag, defaultValue) ?? defaultValue;
    }

    private static double? ReadTaggedNullableDouble(string response, string tag, double? defaultValue)
    {
        var prefix = $"{tag}:";
        var value = response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? response[prefix.Length..] : response;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result))
        {
            return defaultValue;
        }

        return result;
    }

    private static int? ReadTaggedInt(string response, string tag, int? defaultValue)
    {
        var prefix = $"{tag}:";
        var value = response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? response[prefix.Length..] : response;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private static string FormatCommandParameter(double value)
    {
        return ValidateFiniteCommandParameter(value).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static double NormalizeStoredCommandParameter(double value)
    {
        return double.Parse(FormatCommandParameter(value), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string FormatPositionParameter(double value)
    {
        return ValidateFiniteCommandParameter(value).ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string FormatIntegerCommandParameter(double value)
    {
        return ValidateInt32CommandParameter(value).ToString(CultureInfo.InvariantCulture);
    }

    private static double ValidateFiniteCommandParameter(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "CONEX-AGP command parameter must be a finite number.");
        }

        return value;
    }

    private static int ValidateInt32CommandParameter(double value)
    {
        var finiteValue = ValidateFiniteCommandParameter(value);
        if (finiteValue < int.MinValue || finiteValue > int.MaxValue || finiteValue != Math.Truncate(finiteValue))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "CONEX-AGP command parameter must be an Int32 integer.");
        }

        return (int)finiteValue;
    }

    private static string ReadControllerState(string response, string defaultState)
    {
        if (!response.StartsWith("TS:", StringComparison.OrdinalIgnoreCase))
        {
            return defaultState;
        }

        var parts = response.Split(':', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 ? NormalizeControllerState(parts[2]) : defaultState;
    }

    private static string NormalizeControllerState(string value)
    {
        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var state))
        {
            return value.ToUpperInvariant();
        }

        return state switch
        {
            >= 0x0A and <= 0x10 => "NOT_REFERENCED",
            0x14 => "CONFIGURATION",
            0x1E => "HOMING",
            0x28 => "MOVING",
            0x32 or 0x33 or 0x34 => "READY",
            0x3C or 0x3D => "DISABLE",
            _ => $"STATE_{state:X2}"};
    }

    private static bool IsMovingState(string value)
    {
        return value.Equals("MOVING", StringComparison.OrdinalIgnoreCase) || value.Equals("HOMING", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTsError(string value)
    {
        return value.Length >= 4 ? value[..4] : "0";
    }

    private static string ReadTsState(string value)
    {
        return value.Length >= 6 ? value.Substring(4, 2) : "0";
    }
}

public enum EN_ATTENUATOR_COMMAND
{
    MoveAbs,
    MoveRel,
    Home,
    Stop,
    ResetController,
    Refresh,
    PollCurrentPosition,
    PollTargetPosition,
    PollState,
    GetHomeType,
    SetHomeType,
    GetNegativeSoftwareLimit,
    SetNegativeSoftwareLimit,
    GetPositiveSoftwareLimit,
    SetPositiveSoftwareLimit,
    GetIGain,
    SetIGain,
    GetPGain,
    SetPGain,
    GetInterpolationFactor,
    SetInterpolationFactor
}

public enum EN_CONEX_AGP_ERROR
{
    Ok = 0,
    Error = 1,
    Timeout = -1,
    InvalidResponse = -2,
    NotSupported = -99
}

// Intentional breaking change during active development.
// ST_ATTENUATOR_STATUS is a solution-local DTO, not a stable external SDK contract.
// Legacy constructor and Deconstruct compatibility are intentionally not preserved.
public sealed record ST_ATTENUATOR_STATUS(
    double CurrentPosition, 
    double TargetPosition, 
    string CommandState, 
    bool CommOk = true, 
    EN_CONEX_AGP_ERROR LastError = EN_CONEX_AGP_ERROR.Ok, 
    DateTimeOffset? UpdatedAt = null, 
    int? HomeType = null, 
    double? NegativeSoftwareLimit = null, 
    double? PositiveSoftwareLimit = null, 
    double? IGain = null, 
    double? PGain = null, 
    double? InterpolationFactor = null
);
