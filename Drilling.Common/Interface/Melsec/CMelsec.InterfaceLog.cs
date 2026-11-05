namespace Drilling.Common.Interface
{
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    public sealed partial class CMelsec
    {
        // Diagnostic observations only. They neither publish feedback nor request I/O.
        // Both collections are protected by _pollLock; the logger runs outside it.
        private readonly Dictionary<string, MelsecLogSample> _pollLogSamples = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<MelsecLogEntry> _pendingMelsecLogs = new();

        private sealed record MelsecLogSample(ushort[]? Words, EN_MELSEC_CACHE_STATE Quality, string Error);
        private sealed record MelsecLogEntry(ST_MELSEC_MAP_DATA Data, ST_MELSEC_ADDRESS Address,
            ushort[]? Words, string Operation, string Source, string Result,
            EN_MELSEC_CACHE_STATE Quality, DateTimeOffset ObservedAt,
            EN_MELSEC_CACHE_STATE? PreviousQuality = null, string Detail = "");

        private void QueuePollReadLog(PollItem item, ushort[] blockWords, int blockStart, EN_MELSEC_CACHE_STATE previousQuality, DateTimeOffset observedAt)
        {
            if (_logManager is null)
                return;
            var raw = blockWords.AsSpan(item.Address.Number - blockStart, item.WordCount);
            var id = NormalizeId(item.Data.Id);
            if (_pollLogSamples.TryGetValue(id, out var previous) && previous.Quality == EN_MELSEC_CACHE_STATE.Valid
                && previousQuality == EN_MELSEC_CACHE_STATE.Valid && previous.Words is { } oldWords && raw.SequenceEqual(oldWords))
                return;
            var words = raw.ToArray();
            _pollLogSamples[id] = new(words, EN_MELSEC_CACHE_STATE.Valid, "");
            _pendingMelsecLogs.Enqueue(new(item.Data, item.Address, words, "READ", "POLL", "RECEIVED",
                EN_MELSEC_CACHE_STATE.Valid, observedAt, previousQuality));
        }

        private void QueuePollQualityLog(PollItem item, EN_MELSEC_CACHE_STATE quality, string message)
        {
            if (_logManager is null)
                return;
            var id = NormalizeId(item.Data.Id);
            _pollLogSamples.TryGetValue(id, out var previous);
            if (previous is not null && previous.Quality == quality && previous.Error == message)
                return;
            _pollLogSamples[id] = new(null, quality, message);
            _pendingMelsecLogs.Enqueue(new(item.Data, item.Address, null, "READ", "POLL",
                quality == EN_MELSEC_CACHE_STATE.Pending ? "WAITING_FOR_READ" : "UNAVAILABLE",
                quality, DateTimeOffset.UtcNow, previous?.Quality, message));
        }

        private void FlushMelsecLogs()
        {
            MelsecLogEntry[] entries;
            lock (_pollLock)
            {
                if (_pendingMelsecLogs.Count == 0)
                    return;
                entries = _pendingMelsecLogs.ToArray();
                _pendingMelsecLogs.Clear();
            }
            foreach (var entry in entries)
                WriteMelsecLog(entry);
        }

        private void WriteTransferLog(ST_MELSEC_MAP_DATA data, ST_MELSEC_ADDRESS address, IReadOnlyList<ushort> words,
            string operation, string source, string result)
        {
            if (_logManager is not null)
                WriteMelsecLog(new(data, address, words.ToArray(), operation, source, result, EN_MELSEC_CACHE_STATE.Valid, DateTimeOffset.UtcNow));
        }

        private void WriteMelsecLog(MelsecLogEntry entry)
        {
            if (_logManager is null)
                return;
            try
            {
                var nickname = _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Melsec, entry.Data.DeviceNo)?.NickName
                    ?? "MELSEC_" + entry.Data.DeviceNo.ToString(CultureInfo.InvariantCulture);
                var wordCount = entry.Words?.Length ?? (entry.Data.DataType is EN_MELSEC_DATA_TYPE.Bit or EN_MELSEC_DATA_TYPE.Word ? 1 : entry.Data.Length);
                var array = entry.Source == "ARRAY";
                var header = FormatMelsecLogHeader(entry.Data, entry.Operation, entry.Address, wordCount,
                    entry.Words is { } raw ? BriefMelsecLogValue(entry.Data, entry.Address, raw, array) : "미수신");
                var detail = DescribeMelsecLogResult(entry) + " | 품질="
                    + (entry.Operation == "WRITE" ? "수신판정 아님(쓰기)" : DescribeMelsecLogQuality(entry.Quality));
                if (entry.PreviousQuality is { } previous && previous != entry.Quality)
                    detail += " | 직전 품질=" + DescribeMelsecLogQuality(previous);
                detail += entry.Operation == "WRITE" ? " | 전송시각=" + entry.ObservedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)
                    : entry.Words is not null ? " | 수신시각=" + entry.ObservedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)
                    : " | 수신시각=없음 | 관측시각=" + entry.ObservedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture);
                detail += Environment.NewLine + "  자료형=" + DescribeMelsecLogType(entry.Data.DataType)
                    + " | 맵=" + entry.Data.DataType.ToString().ToUpperInvariant()
                    + " | 크기=" + wordCount.ToString(CultureInfo.InvariantCulture) + "WORD/"
                    + (wordCount * sizeof(short)).ToString(CultureInfo.InvariantCulture) + "byte"
                    + " | 경로=" + DescribeMelsecLogSource(entry.Source)
                    + " | 모드=" + (_interfaceManager.IsSimul(EN_EQP_MODULE.Melsec, entry.Data.DeviceNo) ? "시뮬레이션(SIMULATION)" : "실장비(REAL)");
                if (entry.Operation == "WRITE")
                    detail += " | 쓰기계수=" + entry.Data.WriteScale.ToString("R", CultureInfo.InvariantCulture);
                detail += Environment.NewLine + "  ";
                if (entry.Words is { } words)
                    detail += FormatMelsecLogWords(entry.Data, entry.Address, words, array);
                else
                    detail += "RAW=N/A | 값=미수신";
                var cause = DescribeMelsecLogDetail(entry.Detail);
                if (cause.Length > 0)
                    detail += Environment.NewLine + "  원인=" + EscapeMelsecLogText(cause);
                _logManager.WriteInterfaceCommand(EN_EQP_MODULE.Melsec, nickname, header, detail, "");
            }
            catch (Exception exception)
            {
                // A diagnostic writer must never turn a successful PLC read into a communication failure.
                System.Diagnostics.Debug.WriteLine("MELSEC diagnostic log failed: " + exception.Message);
            }
        }

        private static string BuildMelsecLogHeader(ST_MELSEC_MAP_DATA data, string operation, ST_MELSEC_ADDRESS address, int wordCount)
            => FormatMelsecLogHeader(data, operation, address, wordCount, null);

        private static string FormatMelsecLogHeader(ST_MELSEC_MAP_DATA data, string operation, ST_MELSEC_ADDRESS address, int wordCount, string? value)
            => "[MELSEC][" + (ResolveMelsecLogArea(data) switch
            {
                "PROCESS" => "가공(PROCESS)", "STAGE" => "Stage(물류)", "VISION" => "Vision(비전)",
                "OPTIC" => "광학(OPTIC)", _ => "미분류(UNKNOWN)"
            }) + "][" + (operation switch { "WRITE" => "쓰기", "READBACK" => "쓰기 후 읽기", "READ" => "읽기", _ => operation })
                + "] " + FormatMelsecLogAddress(address) + (value is null ? "" : " | 값=" + value)
                + " | ID=" + data.Id + " | " + wordCount.ToString(CultureInfo.InvariantCulture) + "WORD";

        private static string DescribeMelsecLogType(EN_MELSEC_DATA_TYPE type) => type switch
        {
            EN_MELSEC_DATA_TYPE.DWord or EN_MELSEC_DATA_TYPE.Double => "S32(부호 있는 32비트, 2WORD씩)",
            EN_MELSEC_DATA_TYPE.Word => "U16(부호 없는 16비트, 1WORD)",
            EN_MELSEC_DATA_TYPE.Float => "F32(실수, 2WORD씩)",
            EN_MELSEC_DATA_TYPE.Bit => "BIT(1WORD 안의 지정 비트)", _ => "ASCII 문자열"
        };

        private static string DescribeMelsecLogQuality(EN_MELSEC_CACHE_STATE quality) => quality switch
        {
            EN_MELSEC_CACHE_STATE.Valid => "정상(VALID)", EN_MELSEC_CACHE_STATE.Pending => "읽기 대기(PENDING)",
            EN_MELSEC_CACHE_STATE.Stale => "최근 수신 없음(STALE)", EN_MELSEC_CACHE_STATE.Error => "수신 오류(ERROR)",
            EN_MELSEC_CACHE_STATE.Offline => "통신 불가(OFFLINE)", _ => quality.ToString()
        };

        private static string DescribeMelsecLogSource(string source) => source switch
        {
            "POLL" => "중앙 폴링(POLL)", "DIRECT" => "일반 요청(DIRECT)",
            "WRITE_CONFIRM" => "쓰기 후 확인(WRITE_CONFIRM)", "ARRAY" => "배열 처리(ARRAY)", _ => source
        };

        private static string DescribeMelsecLogResult(MelsecLogEntry entry) => entry.Result switch
        {
            "WRITE_ACCEPTED" => "쓰기 전송 완료(상대 동작 완료 아님)",
            "READBACK_RECEIVED" => "쓰기 후 값 수신(일치 여부는 별도 확인)",
            "RECEIVED" => "값 수신 완료",
            "WAITING_FOR_READ" when entry.Detail == "Waiting for feedback after write." => "쓰기 후 읽기 대기",
            "WAITING_FOR_READ" => "값 수신 대기", "UNAVAILABLE" => "값을 확인할 수 없음", _ => entry.Result
        };

        private static string DescribeMelsecLogDetail(string detail) => detail switch
        {
            "Waiting for feedback after write." or "Waiting for MELSEC feedback." => "",
            "MELSEC communication is stopped." => "MELSEC 통신이 중지되었습니다.",
            "MELSEC communication line is not open." => "MELSEC 통신 채널이 열리지 않았습니다.",
            "MELSEC feedback has not been updated within its polling interval." => "정해진 수신 시간 안에 새 데이터가 들어오지 않았습니다.",
            _ => detail // Keep native error codes and unrecognized diagnostic evidence verbatim.
        };

        private static string ResolveMelsecLogArea(ST_MELSEC_MAP_DATA data)
        {
            // The loaded map has no AREA column. Use the document section named by
            // its description, never an address prefix or the direction alone.
            var section = Regex.Match(data.Description ?? "", @"^Spec\s+[0-9.]+\s+(?:legacy;.*?Spec\s+[0-9.]+\s+)?(?:Config_Map\s+Word\s+Detail\s*\((?<owner>가공|물류|비전|광학|Optic)\)|(?<owner>가공|물류|비전|광학|Optic)(?:\s|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (section.Success)
                return section.Groups["owner"].Value.ToUpperInvariant() switch
                {
                    "가공" => "PROCESS",
                    "물류" => "STAGE",
                    "비전" => "VISION",
                    "광학" or "OPTIC" => "OPTIC",
                    _ => "UNKNOWN"
                };
            // Explicit protocol aliases whose ownership is defined in the driver.
            if (data.Id.StartsWith("HANDSHAKE_PRC_", StringComparison.OrdinalIgnoreCase)
                || data.Id.Equals("SDC_REVIEW_RETRY_REQUEST", StringComparison.OrdinalIgnoreCase))
                return "PROCESS";
            if (data.Id.StartsWith("HANDSHAKE_STG_", StringComparison.OrdinalIgnoreCase)
                || data.Id.Equals(StageAlignBusyWordId, StringComparison.OrdinalIgnoreCase))
                return "STAGE";
            return "UNKNOWN";
        }

        private static string FormatMelsecLogWords(ST_MELSEC_MAP_DATA data, ST_MELSEC_ADDRESS address, ushort[] words, bool array)
        {
            if (words.Length == 0)
                return "RAW=N/A | 값=미수신";
            var scale = data.ReadScale.ToString("R", CultureInfo.InvariantCulture);
            if (data.DataType == EN_MELSEC_DATA_TYPE.String)
            {
                var bytes = new byte[words.Length * 2];
                for (var index = 0; index < words.Length; index++)
                {
                    bytes[index * 2] = (byte)words[index];
                    bytes[index * 2 + 1] = (byte)(words[index] >> 8);
                }
                return "값=\"" + EscapeMelsecLogText(Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' '))
                    + "\" | RAW(U16)=[" + string.Join(",", words.Select(word => word.ToString(CultureInfo.InvariantCulture))) + "] | 인코딩=ASCII";
            }
            if (data.DataType == EN_MELSEC_DATA_TYPE.Bit)
            {
                var bit = address.BitIndex!.Value;
                return "값=" + ((words[0] & (1 << bit)) != 0 ? "1(ON)" : "0(OFF)") + " | RAW="
                    + words[0].ToString(CultureInfo.InvariantCulture) + "(U16) | BIT=" + bit.ToString(CultureInfo.InvariantCulture);
            }
            if (data.DataType == EN_MELSEC_DATA_TYPE.Word)
                return "값=" + FormatMelsecLogNumber(words[0] * data.ReadScale)
                    + FormatMelsecLogSignalState(data, words[0] * data.ReadScale) + " | RAW=" + words[0].ToString(CultureInfo.InvariantCulture)
                    + "(U16) | 읽기계수=" + scale;
            if (words.Length < 2)
                return "RAW=N/A | 값=미수신 | 원인=2WORD 수신값이 완성되지 않음";
            if (data.DataType == EN_MELSEC_DATA_TYPE.Float)
                return "값=" + FormatMelsecLogNumber(WordsToFloat(words) * data.ReadScale)
                    + " | RAW=" + WordsToInt32(words).ToString(CultureInfo.InvariantCulture) + "(S32 비트) | 실수원본="
                    + WordsToFloat(words).ToString("G9", CultureInfo.InvariantCulture) + "(F32) | 읽기계수=" + scale;
            if (!array)
                return "값=" + FormatMelsecLogNumber(WordsToInt32(words) * data.ReadScale)
                    + FormatMelsecLogSignalState(data, WordsToInt32(words) * data.ReadScale) + " | RAW="
                    + WordsToInt32(words).ToString(CultureInfo.InvariantCulture) + "(S32) | 읽기계수=" + scale
                    + (words.Length > 2 ? " | 추가 RAW(U16)=[" + string.Join(",", words.Skip(2).Select(word => word.ToString(CultureInfo.InvariantCulture))) + "]" : "");

            var result = new StringBuilder("개수=").Append((words.Length / 2).ToString(CultureInfo.InvariantCulture))
                .Append(" | 읽기계수=").Append(scale).Append(" | 각 RAW는 S32/2WORD");
            var firstIndex = (address.Number - ParseAddress(data.Address).Number) / 2 + 1;
            for (var offset = 0; offset + 1 < words.Length; offset += 2)
            {
                result.Append(Environment.NewLine).Append("  ");
                var raw = unchecked((int)(words[offset] | ((uint)words[offset + 1] << 16)));
                result.Append('[').Append((firstIndex + offset / 2).ToString(CultureInfo.InvariantCulture)).Append("] ")
                    .Append(FormatMelsecLogAddress(address with { Number = address.Number + offset }))
                    .Append(" | 값=").Append(FormatMelsecLogNumber(raw * data.ReadScale))
                    .Append(" | RAW=").Append(raw.ToString(CultureInfo.InvariantCulture)).Append("(S32)");
            }
            return result.ToString();
        }

        private static string FormatMelsecLogSignalState(ST_MELSEC_MAP_DATA data, double value)
        {
            var onOffSignal = data.Id.StartsWith("HANDSHAKE_PRC_", StringComparison.OrdinalIgnoreCase)
                || data.Id.StartsWith("HANDSHAKE_STG_", StringComparison.OrdinalIgnoreCase)
                || data.Id.Equals("SDC_REVIEW_RETRY_REQUEST", StringComparison.OrdinalIgnoreCase);
            return onOffSignal && value is 0 or 1 ? (value == 1 ? "(ON)" : "(OFF)") : "";
        }

        private static string FormatMelsecLogNumber(double value)
        {
            // Display only: keep wire RAW and the actual decoded cache value untouched.
            // G15 removes binary tails without rounding tiny nonzero values to zero.
            var compact = value.ToString("G15", CultureInfo.InvariantCulture);
            return double.IsFinite(value) && (!double.TryParse(compact, NumberStyles.Float, CultureInfo.InvariantCulture, out var shown) || !double.IsFinite(shown))
                ? value.ToString("R", CultureInfo.InvariantCulture) : compact;
        }

        private static string BriefMelsecLogValue(ST_MELSEC_MAP_DATA data, ST_MELSEC_ADDRESS address, ushort[] words, bool array)
        {
            if (words.Length == 0)
                return "미수신";
            if (data.DataType == EN_MELSEC_DATA_TYPE.String)
            {
                var bytes = new byte[words.Length * 2];
                for (var index = 0; index < words.Length; index++)
                {
                    bytes[index * 2] = (byte)words[index];
                    bytes[index * 2 + 1] = (byte)(words[index] >> 8);
                }
                var text = EscapeMelsecLogText(Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' '));
                return "\"" + (text.Length > 64 ? text[..64] + "…(전체 값은 아래)" : text) + "\"";
            }
            if (data.DataType == EN_MELSEC_DATA_TYPE.Bit)
                return (words[0] & (1 << address.BitIndex!.Value)) != 0 ? "1(ON)" : "0(OFF)";
            if (data.DataType == EN_MELSEC_DATA_TYPE.Word)
                return FormatMelsecLogNumber(words[0] * data.ReadScale) + FormatMelsecLogSignalState(data, words[0] * data.ReadScale);
            if (words.Length < 2)
                return "미수신";
            var first = data.DataType == EN_MELSEC_DATA_TYPE.Float ? WordsToFloat(words) * data.ReadScale : WordsToInt32(words) * data.ReadScale;
            if (!array)
                return FormatMelsecLogNumber(first) + FormatMelsecLogSignalState(data, first);
            var last = unchecked((int)(words[^2] | ((uint)words[^1] << 16))) * data.ReadScale;
            return (words.Length / 2).ToString(CultureInfo.InvariantCulture) + "개(" + FormatMelsecLogNumber(first) + " → " + FormatMelsecLogNumber(last) + ")";
        }

        private static string FormatMelsecLogAddress(ST_MELSEC_ADDRESS address)
        {
            var hexadecimal = address.Device is "W" or "SW" or "B" or "SB" or "X" or "Y";
            return address.Device + address.Number.ToString(hexadecimal ? "X" : "D", CultureInfo.InvariantCulture)
                + (address.BitIndex is { } bit ? "." + bit.ToString("X", CultureInfo.InvariantCulture) : "");
        }

        private static string EscapeMelsecLogText(string text)
        {
            var result = new StringBuilder(text.Length);
            foreach (var value in text)
                result.Append(value switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    '\0' => "\\0",
                    _ when char.IsControl(value) => "\\u" + ((int)value).ToString("X4", CultureInfo.InvariantCulture),
                    _ => value.ToString()
                });
            return result.ToString();
        }
    }
}
