using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;

namespace Drilling.Common.Interface;
[CCommType("Serial", "PowerMeter")]
[CCommType("ModbusSerial", "PowerMeter")]
internal sealed class CPowerMeter(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CSerialComm(data, option)
{
    private const int ResponseTimeoutThreshold = 3;
    private const int ResponsePollIntervalMs = 10;
    private int _consecutiveResponseTimeouts;
    protected override string CommandNewLine
    {
        get
        {
            return "\r";
        }
    }

    protected override string FormatCommand(string function)
    {
        return function.Trim();
    }

    protected override void ConfigureSerialPort(SerialPort serialPort)
    {
        serialPort.RtsEnable = true;
    }

    public override async Task<string> Execute(string function, CancellationToken cancellationToken = default)
    {
        await SerialLock.WaitAsync(cancellationToken);
        try
        {
            if (SerialPort is null || !SerialPort.IsOpen)
            {
                await Connect(cancellationToken);
            }

            if (SerialPort is null || !SerialPort.IsOpen)
            {
                return "";
            }

            LastSent = FormatCommand(function);
            try
            {
                SerialPort.DiscardInBuffer();
                SerialPort.Write(LastSent + CommandNewLine);
                if (IsNoResponseCommand(LastSent))
                {
                    LastReceived = "OK";
                    LastError = "";
                    SetState(EN_COMM_STATE.Online);
                    return LastReceived;
                }

                LastReceived = await Task.Run(ReadResponse, cancellationToken);
                if (string.IsNullOrWhiteSpace(LastReceived))
                {
                    _consecutiveResponseTimeouts++;
                    LastError = "PowerMeter response timeout.";
                    if (_consecutiveResponseTimeouts >= ResponseTimeoutThreshold)
                    {
                        CloseSerialPort();
                        SetError(LastError);
                    }

                    return "";
                }

                _consecutiveResponseTimeouts = 0;
                LastError = "";
                SetState(EN_COMM_STATE.Online);
                return LastReceived;
            }
            catch (TimeoutException)
            {
                var remaining = "";
                try
                {
                    remaining = SerialPort?.ReadExisting().Trim() ?? "";
                }
                catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
                {
                    CloseSerialPort();
                    SetError(ex);
                    return "";
                }

                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    _consecutiveResponseTimeouts = 0;
                    LastReceived = remaining;
                    LastError = "";
                    SetState(EN_COMM_STATE.Online);
                    return LastReceived;
                }

                _consecutiveResponseTimeouts++;
                LastReceived = "";
                LastError = "PowerMeter response timeout.";
                if (_consecutiveResponseTimeouts >= ResponseTimeoutThreshold)
                {
                    CloseSerialPort();
                    SetError(LastError);
                }

                return "";
            }
            catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                CloseSerialPort();
                SetError(ex);
                return "";
            }
        }
        finally
        {
            SerialLock.Release();
        }
    }

    private string ReadResponse()
    {
        var port = SerialPort ?? throw new InvalidOperationException("Serial port is not open.");
        var response = new StringBuilder();
        var timeoutMs = Math.Max(500, port.ReadTimeout);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!port.IsOpen)
            {
                throw new InvalidOperationException("Serial port is closed.");
            }

            if (port.BytesToRead <= 0)
            {
                Thread.Sleep(ResponsePollIntervalMs);
                continue;
            }

            var value = port.ReadByte();
            if (value < 0)
            {
                continue;
            }

            var character = (char)value;
            if (character is '\r' or '\n')
            {
                if (response.Length > 0)
                {
                    break;
                }

                continue;
            }

            response.Append(character);
        }

        return response.ToString().Trim();
    }

    private static bool IsNoResponseCommand(string command)
    {
        return command.Trim().ToLowerInvariant()is "*rst" or "dst" or "dsp";
    }

    public static string Build(EN_POWER_METER_COMMAND command, double parameter = 0.0)
    {
        return command switch
        {
            EN_POWER_METER_COMMAND.ReadPower => "pw?",
            EN_POWER_METER_COMMAND.QueryHardwareDescription => "*ind",
            EN_POWER_METER_COMMAND.QuerySerialNumber => "msn?",
            EN_POWER_METER_COMMAND.QueryWaveLength => "wv?",
            EN_POWER_METER_COMMAND.SetWaveLength => $"wv {ToMeter(parameter).ToString("F8", CultureInfo.InvariantCulture)}",
            EN_POWER_METER_COMMAND.QueryBeamPosition => "pos",
            EN_POWER_METER_COMMAND.StartStreaming => "dst",
            EN_POWER_METER_COMMAND.StopStreaming => "dsp",
            EN_POWER_METER_COMMAND.Reset => "*rst",
            _ => ""
        };
    }

    public static bool IsSuccessResponse(string response)
    {
        return !string.IsNullOrWhiteSpace(response) && !response.Trim().StartsWith("ERR", StringComparison.OrdinalIgnoreCase);
    }

    public static ST_POWER_METER_STATUS Apply(EN_POWER_METER_COMMAND command, double parameter, string response, ST_POWER_METER_STATUS current, bool simulation, double? simulationPower = null)
    {
        var value = simulation ? CreateSimulationResponse(command, parameter, current, simulationPower) : response.Trim();
        if (!simulation && command != EN_POWER_METER_COMMAND.Reset && !IsSuccessResponse(value))
        {
            return current with
            {
                LastError = EN_POWER_METER_ERROR.InvalidResponse,
                MeasuredAt = DateTimeOffset.Now
            };
        }

        var ok = current with
        {
            LastCommand = command.ToString().ToUpperInvariant(),
            LastError = EN_POWER_METER_ERROR.Ok,
            MeasuredAt = DateTimeOffset.Now
        };
        return command switch
        {
            EN_POWER_METER_COMMAND.ReadPower => TryReadPower(value, out var power) ? ApplyPowerValue(ok, power) : ok with { LastError = EN_POWER_METER_ERROR.InvalidResponse },
            EN_POWER_METER_COMMAND.QueryHardwareDescription => ok with
            {
                ModelName = value
            },
            EN_POWER_METER_COMMAND.QuerySerialNumber => ok with
            {
                SerialNumber = value
            },
            EN_POWER_METER_COMMAND.QueryWaveLength => ok with
            {
                WaveLengthNm = ReadWaveLengthNm(value)
            },
            EN_POWER_METER_COMMAND.SetWaveLength => ok with
            {
                WaveLengthNm = parameter
            },
            EN_POWER_METER_COMMAND.QueryBeamPosition => ApplyBeamPosition(ok, value),
            EN_POWER_METER_COMMAND.StartStreaming => ok with
            {
                IsMeasuring = true
            },
            EN_POWER_METER_COMMAND.StopStreaming => ok with
            {
                IsMeasuring = false
            },
            EN_POWER_METER_COMMAND.Reset => ST_POWER_METER_STATUS.Empty with
            {
                Unit = ok.Unit,
                MeasuredAt = DateTimeOffset.Now,
                LastCommand = "RESET"
            },
            _ => ok
        };
    }

    private static ST_POWER_METER_STATUS ApplyPowerValue(ST_POWER_METER_STATUS current, double power)
    {
        var sampleCount = current.SampleCount + 1;
        var average = current.SampleCount <= 0 ? power : ((current.AveragePower * current.SampleCount) + power) / sampleCount;
        var min = current.SampleCount <= 0 ? power : Math.Min(current.MinPower, power);
        var max = current.SampleCount <= 0 ? power : Math.Max(current.MaxPower, power);
        return current with
        {
            MeasuredPower = power,
            AveragePower = average,
            MinPower = min,
            MaxPower = max,
            SampleCount = sampleCount,
            Unit = "W"
        };
    }

    private static ST_POWER_METER_STATUS ApplyBeamPosition(ST_POWER_METER_STATUS current, string value)
    {
        var parts = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return current;
        }

        return current with
        {
            BeamPositionX = ReadDouble(parts[0]),
            BeamPositionY = ReadDouble(parts[1])
        };
    }

    private static string CreateSimulationResponse(EN_POWER_METER_COMMAND command, double parameter, ST_POWER_METER_STATUS current, double? simulationPower)
    {
        var power = simulationPower ?? (current.MeasuredPower <= 0.0 ? 1.204 : current.MeasuredPower + 0.003);
        return command switch
        {
            EN_POWER_METER_COMMAND.ReadPower => power.ToString("F4", CultureInfo.InvariantCulture),
            EN_POWER_METER_COMMAND.QueryHardwareDescription => string.IsNullOrWhiteSpace(current.ModelName) ? "PowerMax" : current.ModelName,
            EN_POWER_METER_COMMAND.QuerySerialNumber => string.IsNullOrWhiteSpace(current.SerialNumber) ? "PM_SIM_0000" : current.SerialNumber,
            EN_POWER_METER_COMMAND.QueryWaveLength => ToMeter(current.WaveLengthNm).ToString("0.########E+0", CultureInfo.InvariantCulture),
            EN_POWER_METER_COMMAND.SetWaveLength => ToMeter(parameter).ToString("0.########E+0", CultureInfo.InvariantCulture),
            EN_POWER_METER_COMMAND.QueryBeamPosition => $"{current.BeamPositionX.ToString("F3", CultureInfo.InvariantCulture)},{current.BeamPositionY.ToString("F3", CultureInfo.InvariantCulture)}",
            _ => "OK"
        };
    }

    private static double ReadWaveLengthNm(string value)
    {
        var number = ReadDouble(value);
        return number < 0.01 ? number * 1_000_000_000.0 : number;
    }

    private static double ToMeter(double waveLengthNm)
    {
        return waveLengthNm <= 0.0 ? 355e-9 : waveLengthNm * 1e-9;
    }

    internal static bool TryReadPower(string value, out double power)
    {
        var text = value.Trim();
        var number = new string(text.TakeWhile(c => char.IsAsciiDigit(c) || c is '-' or '+' or '.' or 'E' or 'e').ToArray());
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out power) && double.IsFinite(power) && power >= 0;
    }

    private static double ReadDouble(string value)
    {
        var normalized = ReadLeadingNumber(value);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
    }

    private static string ReadLeadingNumber(string value)
    {
        bool MatchesChar(char character)
        {
            return char.IsDigit(character) || character == '-' || character == '+' || character == '.' || character == 'E' || character == 'e';
        }

        var chars = value.Trim().TakeWhile(MatchesChar).ToArray();
        return chars.Length == 0 ? "0" : new string (chars);
    }
}

public enum EN_POWER_METER_COMMAND
{
    ReadPower,
    QueryHardwareDescription,
    QuerySerialNumber,
    QueryWaveLength,
    SetWaveLength,
    QueryBeamPosition,
    StartStreaming,
    StopStreaming,
    Reset,
    Refresh
}

public enum EN_POWER_METER_ERROR
{
    Ok = 0,
    Error = 1,
    Timeout = -1,
    InvalidResponse = -2,
    NotSupported = -99
}
