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
                var header = BuildMelsecLogHeader(entry.Data, entry.Operation, entry.Address, wordCount);
                var detail = "SOURCE=" + entry.Source + " | MODE="
                    + (_interfaceManager.IsSimul(EN_EQP_MODULE.Melsec, entry.Data.DeviceNo) ? "SIMULATION" : "REAL")
                    + " | QUALITY=" + (entry.Operation == "WRITE" ? "N/A" : entry.Quality.ToString().ToUpperInvariant());
                detail += entry.Operation == "WRITE" ? " | WRITE_AT=" + entry.ObservedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)
                    : entry.Words is not null ? " | SAMPLE_AT=" + entry.ObservedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)
                    : " | SAMPLE_AT=N/A | OBSERVED_AT=" + entry.ObservedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture);
                if (entry.PreviousQuality is { } previous && previous != entry.Quality)
                    detail += " | PREVIOUS_QUALITY=" + previous.ToString().ToUpperInvariant();
                detail += " | RESULT=" + entry.Result;
                if (entry.Operation == "WRITE")
                    detail += " | WRITE_SCALE=" + entry.Data.WriteScale.ToString("R", CultureInfo.InvariantCulture);
                if (entry.Words is { } words)
                    detail += " | " + FormatMelsecLogWords(entry.Data, entry.Address, words, entry.Source == "ARRAY");
                else
                    detail += " | RAW=N/A | VALUE=N/A";
                if (entry.Detail.Length > 0)
                    detail += " | DETAIL=" + EscapeMelsecLogText(entry.Detail);
                _logManager.WriteInterfaceCommand(EN_EQP_MODULE.Melsec, nickname, header, detail, "");
            }
            catch (Exception exception)
            {
                // A diagnostic writer must never turn a successful PLC read into a communication failure.
                System.Diagnostics.Debug.WriteLine("MELSEC diagnostic log failed: " + exception.Message);
            }
        }

        private static string BuildMelsecLogHeader(ST_MELSEC_MAP_DATA data, string operation, ST_MELSEC_ADDRESS address, int wordCount)
            => "[MELSEC] AREA=" + ResolveMelsecLogArea(data) + " | OP=" + operation
                + " | ID=" + data.Id + " | ADDRESS=" + FormatMelsecLogAddress(address)
                + " | TYPE=" + (data.DataType switch
                {
                    EN_MELSEC_DATA_TYPE.DWord or EN_MELSEC_DATA_TYPE.Double => "INT32",
                    EN_MELSEC_DATA_TYPE.Word => "UINT16",
                    EN_MELSEC_DATA_TYPE.Float => "FLOAT32",
                    _ => data.DataType.ToString().ToUpperInvariant()
                }) + " | MAP_TYPE=" + data.DataType.ToString().ToUpperInvariant()
                + " | SIZE=" + wordCount.ToString(CultureInfo.InvariantCulture) + "W"
                + " | BYTES=" + (wordCount * sizeof(short)).ToString(CultureInfo.InvariantCulture);

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
                return "RAW=N/A | VALUE=N/A";
            var scale = data.ReadScale.ToString("R", CultureInfo.InvariantCulture);
            if (data.DataType == EN_MELSEC_DATA_TYPE.String)
            {
                var bytes = new byte[words.Length * 2];
                for (var index = 0; index < words.Length; index++)
                {
                    bytes[index * 2] = (byte)words[index];
                    bytes[index * 2 + 1] = (byte)(words[index] >> 8);
                }
                return "RAW_UINT16=[" + string.Join(",", words.Select(word => word.ToString(CultureInfo.InvariantCulture)))
                    + "] | VALUE=\"" + EscapeMelsecLogText(Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ')) + "\" | ENCODING=ASCII";
            }
            if (data.DataType == EN_MELSEC_DATA_TYPE.Bit)
            {
                var bit = address.BitIndex!.Value;
                return "RAW=" + words[0].ToString(CultureInfo.InvariantCulture) + " | RAW_TYPE=UINT16 | BIT="
                    + bit.ToString(CultureInfo.InvariantCulture) + " | VALUE=" + ((words[0] & (1 << bit)) != 0 ? "1" : "0");
            }
            if (data.DataType == EN_MELSEC_DATA_TYPE.Word)
                return "RAW=" + words[0].ToString(CultureInfo.InvariantCulture) + " | RAW_TYPE=UINT16 | VALUE="
                    + (words[0] * data.ReadScale).ToString("R", CultureInfo.InvariantCulture)
                    + FormatMelsecLogSignalState(data, words[0] * data.ReadScale) + " | READ_SCALE=" + scale;
            if (words.Length < 2)
                return "RAW=N/A | VALUE=N/A | DETAIL=Incomplete 2W sample";
            if (data.DataType == EN_MELSEC_DATA_TYPE.Float)
                return "RAW=" + WordsToInt32(words).ToString(CultureInfo.InvariantCulture) + " | RAW_TYPE=INT32_BITS | FLOAT_VALUE="
                    + WordsToFloat(words).ToString("R", CultureInfo.InvariantCulture) + " | VALUE="
                    + (WordsToFloat(words) * data.ReadScale).ToString("R", CultureInfo.InvariantCulture) + " | READ_SCALE=" + scale;
            if (!array)
                return "RAW=" + WordsToInt32(words).ToString(CultureInfo.InvariantCulture) + " | RAW_TYPE=INT32 | VALUE="
                    + (WordsToInt32(words) * data.ReadScale).ToString("R", CultureInfo.InvariantCulture)
                    + FormatMelsecLogSignalState(data, WordsToInt32(words) * data.ReadScale) + " | READ_SCALE=" + scale
                    + (words.Length > 2 ? " | EXTRA_RAW_UINT16=[" + string.Join(",", words.Skip(2).Select(word => word.ToString(CultureInfo.InvariantCulture))) + "]" : "");

            var result = new StringBuilder("RAW_TYPE=INT32 | READ_SCALE=").Append(scale)
                .Append(" | COUNT=").Append((words.Length / 2).ToString(CultureInfo.InvariantCulture));
            var firstIndex = (address.Number - ParseAddress(data.Address).Number) / 2 + 1;
            for (var offset = 0; offset + 1 < words.Length; offset += 2)
            {
                result.Append(Environment.NewLine).Append("  ");
                var raw = unchecked((int)(words[offset] | ((uint)words[offset + 1] << 16)));
                result.Append("INDEX=").Append((firstIndex + offset / 2).ToString(CultureInfo.InvariantCulture))
                    .Append(" | ADDRESS=").Append(FormatMelsecLogAddress(address with { Number = address.Number + offset }))
                    .Append(" | RAW=").Append(raw.ToString(CultureInfo.InvariantCulture))
                    .Append(" | VALUE=").Append((raw * data.ReadScale).ToString("R", CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        private static string FormatMelsecLogSignalState(ST_MELSEC_MAP_DATA data, double value)
        {
            var onOffSignal = data.Id.StartsWith("HANDSHAKE_PRC_", StringComparison.OrdinalIgnoreCase)
                || data.Id.StartsWith("HANDSHAKE_STG_", StringComparison.OrdinalIgnoreCase)
                || data.Id.Equals("SDC_REVIEW_RETRY_REQUEST", StringComparison.OrdinalIgnoreCase);
            return onOffSignal && value is 0 or 1 ? " | STATE=" + (value == 1 ? "ON" : "OFF") : "";
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
