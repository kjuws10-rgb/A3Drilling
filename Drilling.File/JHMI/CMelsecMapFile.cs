namespace Drilling.File.JHMI
{
    using System.Globalization;
    using Drilling.Common.Interface;
    using Drilling.File.Parser;

    public sealed partial class CMelsecMapFile(string configRoot) : CMelsecMapFileBase
    {
#region 공유 상태·초기값
        private const string TableName = "JHMI_MELSEC_MAP";
        private static readonly IReadOnlyList<string> Headers = ["ID", "USE", "GROUP", "NAME", "DEVICE NO", "ADDRESS", "DATA TYPE", "DIRECTION", "ACCESS", "READ_SCALE", "WRITE_SCALE", "LENGTH", "POLL_MS", "DESCRIPTION"];
        // Startup structure checks and runtime loading must use the same map schema.
        internal static readonly IReadOnlyList<IReadOnlyList<string>> RequiredHeaderGroups = [
            ["ID"], 
            ["USE"], 
            ["GROUP"], 
            ["NAME"], 
            ["DEVICE NO", "DEV NO", "NUMBER"], 
            ["ADDRESS"], 
            ["DATA TYPE", "DATATYPE", "TYPE"], 
            ["DIRECTION", "DIR"], 
            ["ACCESS"], 
            ["READ_SCALE", "SCALE"], 
            ["WRITE_SCALE", "SCALE"], 
            ["LENGTH", "SIZE"], 
            ["POLL_MS", "POLL MS", "POLL"]
        ];
#endregion
#region 기본 처리
        public override IReadOnlyList<ST_MELSEC_MAP_DATA> LoadAll(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureFile();
            CCsvParser.ValidateRequiredHeaders(GetMapPath(), TableName, RequiredHeaderGroups);
            global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA SelectValue(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row, int index)
            {
                return Parse(row, index + 2);
            }

            bool HasId(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                return !string.IsNullOrWhiteSpace(data.Id);
            }

            string SelectGroup(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                return data.Group;
            }

            int SelectDeviceNo(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                return data.DeviceNo;
            }

            string SelectId(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                return data.Id;
            }

            var rows = CCsvParser.Read(GetMapPath()).Select(SelectValue).Where(HasId).OrderBy(SelectGroup, StringComparer.OrdinalIgnoreCase).ThenBy(SelectDeviceNo).ThenBy(SelectId, StringComparer.OrdinalIgnoreCase).ToArray();
            Validate(rows);
            return rows;
        }

        private ST_MELSEC_MAP_DATA Parse(IReadOnlyDictionary<string, string> row, int rowNo)
        {
            double readScale, writeScale;
            if (row.ContainsKey("READ_SCALE") || row.ContainsKey("WRITE_SCALE"))
            {
                readScale = ReadDouble(RequireText(row, rowNo, "READ_SCALE"), rowNo, "READ_SCALE", 1);
                writeScale = ReadDouble(RequireText(row, rowNo, "WRITE_SCALE"), rowNo, "WRITE_SCALE", 1);
            }
            else
            {
                // Legacy maps divided by SCALE when writing. Keep their behavior without rewriting the file.
                readScale = ReadDouble(ReadFirst(row, "SCALE"), rowNo, "SCALE", 1);
                if (Math.Abs(readScale) < double.Epsilon)
                    readScale = 1;
                writeScale = 1 / readScale;
            }

            if (!double.IsFinite(readScale) || readScale == 0 || !double.IsFinite(writeScale) || writeScale == 0)
                throw new InvalidDataException($"{TableName} validation failed. Row {rowNo} / READ_SCALE and WRITE_SCALE must be finite and nonzero.");
            return new ST_MELSEC_MAP_DATA(
                NormalizeId(RequireText(row, rowNo, "ID")), 
                ReadBool(ReadFirst(row, "USE"), true), 
                NormalizeGroup(RequireText(row, rowNo, "GROUP")), 
                ReadFirst(row, "NAME"), 
                ReadInt(ReadFirst(row, "DEVICE NO"), rowNo, "DEVICE NO", 0), 
                NormalizeAddress(RequireText(row, rowNo, "ADDRESS")), 
                ReadDataType(RequireText(row, rowNo, "DATA TYPE", "DATATYPE", "TYPE"), rowNo), 
                ReadDirection(RequireText(row, rowNo, "DIRECTION", "DIR"), rowNo), 
                ReadAccess(RequireText(row, rowNo, "ACCESS"), rowNo), 
                readScale, 
                writeScale, 
                ReadInt(ReadFirst(row, "LENGTH"), rowNo, "LENGTH", 1), 
                ReadInt(ReadFirst(row, "POLL_MS"), rowNo, "POLL_MS", 0), 
                ReadFirst(row, "DESCRIPTION")
            );
        }

        private void EnsureFile()
        {
            var path = GetMapPath();
            if (System.IO.File.Exists(path))
            {
                return;
            }

            CCsvParser.Write(path, Headers, CreateHandshakeRows().Concat(CreateMotorStatusRows()).Concat(CreateLatestScenarioRows()).Concat(CreateVisionSpecRows()).Concat(CreateUnassignedLegacyRows()).ToArray());
        }

        private string GetMapPath()
        {
            return Path.Combine(configRoot, "JHMI_MELSEC_MAP.csv");
        }

        private static void Validate(IReadOnlyList<ST_MELSEC_MAP_DATA> rows)
        {
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool IsUse(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA row)
            {
                return row.Use;
            }

            foreach (var row in rows.Where(IsUse))
            {
                if (string.IsNullOrWhiteSpace(row.Id))
                {
                    throw new InvalidDataException($"{TableName} validation failed. ID cannot be empty.");
                }

                if (!usedIds.Add(row.Id))
                {
                    throw new InvalidDataException($"{TableName} validation failed. Duplicated ID: {row.Id}");
                }

                if (row.DeviceNo < 0)
                {
                    throw new InvalidDataException($"{TableName} validation failed. DEVICE NO cannot be negative: {row.Id}");
                }

                if (string.IsNullOrWhiteSpace(row.Address))
                {
                    throw new InvalidDataException($"{TableName} validation failed. ADDRESS cannot be empty: {row.Id}");
                }

                if (row.Length <= 0)
                {
                    throw new InvalidDataException($"{TableName} validation failed. LENGTH must be positive: {row.Id}");
                }

                if (row.PollMs < 0)
                {
                    throw new InvalidDataException($"{TableName} validation failed. POLL_MS cannot be negative: {row.Id}");
                }

                if (row.DataType == EN_MELSEC_DATA_TYPE.Bit && row.Length != 1)
                {
                    throw new InvalidDataException($"{TableName} validation failed. BIT LENGTH must be 1: {row.Id}");
                }
            }
        }

        private static IReadOnlyDictionary<string, string> Row(
            string id, 
            string group, 
            string name, 
            int deviceNo, 
            string address, 
            string dataType, 
            string direction, 
            string access, 
            string readScale, 
            string writeScale, 
            string length, 
            string pollMs, 
            string description, 
            bool use = true
        )
        {
            return new Dictionary<string, string>
            {
                ["ID"] = id,
                ["USE"] = use ? "1" : "0",
                ["GROUP"] = group,
                ["NAME"] = name,
                ["DEVICE NO"] = deviceNo.ToString(CultureInfo.InvariantCulture),
                ["ADDRESS"] = address,
                ["DATA TYPE"] = dataType,
                ["DIRECTION"] = direction,
                ["ACCESS"] = access,
                ["READ_SCALE"] = readScale,
                ["WRITE_SCALE"] = writeScale,
                ["LENGTH"] = length,
                ["POLL_MS"] = pollMs,
                ["DESCRIPTION"] = description
            };
        }

        private static EN_MELSEC_DATA_TYPE ReadDataType(string value, int rowNo)
        {
            return NormalizeText(value) switch
            {
                "BIT" => EN_MELSEC_DATA_TYPE.Bit,
                "WORD" => EN_MELSEC_DATA_TYPE.Word,
                "DWORD" or "D_WORD" or "DOUBLEWORD" => EN_MELSEC_DATA_TYPE.DWord,
                "DOUBLE" or "REAL64" => EN_MELSEC_DATA_TYPE.Double,
                "FLOAT" or "REAL32" => EN_MELSEC_DATA_TYPE.Float,
                "STRING" or "TEXT" => EN_MELSEC_DATA_TYPE.String,
                _ => throw new InvalidDataException($"{TableName} validation failed. Row {rowNo} / DATA TYPE is invalid: {value}")};
        }

        private static EN_MELSEC_DIRECTION ReadDirection(string value, int rowNo)
        {
            return NormalizeText(value) switch
            {
                "IN" or "INPUT" => EN_MELSEC_DIRECTION.In,
                "OUT" or "OUTPUT" => EN_MELSEC_DIRECTION.Out,
                "INOUT" or "IN_OUT" or "BOTH" => EN_MELSEC_DIRECTION.InOut,
                _ => throw new InvalidDataException($"{TableName} validation failed. Row {rowNo} / DIRECTION is invalid: {value}")};
        }

        private static EN_MELSEC_ACCESS ReadAccess(string value, int rowNo)
        {
            return NormalizeText(value) switch
            {
                "R" or "READ" => EN_MELSEC_ACCESS.Read,
                "W" or "WRITE" => EN_MELSEC_ACCESS.Write,
                "RW" or "R/W" or "READWRITE" or "READ_WRITE" => EN_MELSEC_ACCESS.ReadWrite,
                _ => throw new InvalidDataException($"{TableName} validation failed. Row {rowNo} / ACCESS is invalid: {value}")};
        }

        private static string RequireText(IReadOnlyDictionary<string, string> row, int rowNo, params string[] names)
        {
            return CCsvParser.RequireText(row, TableName, rowNo, names);
        }

        private static string ReadFirst(IReadOnlyDictionary<string, string> row, params string[] names)
        {
            return CCsvParser.GetFirst(row, names);
        }

        private static bool ReadBool(string value, bool defaultValue)
        {
            return CCsvParser.ReadBool(value, defaultValue);
        }

        private static int ReadInt(string value, int rowNo, string fieldName, int defaultValue)
        {
            return CCsvParser.ReadInt(value, TableName, rowNo, fieldName, defaultValue);
        }

        private static double ReadDouble(string value, int rowNo, string fieldName, double defaultValue)
        {
            return CCsvParser.ReadDouble(value, TableName, rowNo, fieldName, defaultValue);
        }

        private static string NormalizeId(string value)
        {
            return value.Trim().ToUpperInvariant();
        }

        private static string NormalizeGroup(string value)
        {
            return value.Trim().ToUpperInvariant();
        }

        private static string NormalizeAddress(string value)
        {
            return value.Trim().ToUpperInvariant();
        }

        private static string NormalizeText(string value)
        {
            return value.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.OrdinalIgnoreCase).Replace("-", "_", StringComparison.OrdinalIgnoreCase);
        }
#endregion

#region Defaults
        // Keep legacy IDs available for future configuration, but never seed obsolete 250703 addresses.
        private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreateUnassignedLegacyRows() => [
            Unassigned("FUNCTION_PAUSE_POSSIBLE_READ", "FUNCTION", "PAUSE Possible Read", "BIT", "IN", "R", "1", "1", "1"), 
            Unassigned("FUNCTION_PAUSE_ACK_READ", "FUNCTION", "PAUSE ACK Read", "BIT", "IN", "R", "1", "1", "1"), 
            Unassigned("FUNCTION_PAUSE_ACK_WRITE", "FUNCTION", "PAUSE ACK Write", "BIT", "OUT", "W", "1", "1", "1"), 
            Unassigned("FUNCTION_PM_POSSIBLE_READ", "FUNCTION", "PM Possible Read", "BIT", "IN", "R", "1", "1", "1"), 
            Unassigned("FUNCTION_NORMAL_POSSIBLE_READ", "FUNCTION", "NORMAL Possible Read", "BIT", "IN", "R", "1", "1", "1"), 
            Unassigned("FUNCTION_RESUME_POSSIBLE_READ", "FUNCTION", "RESUME Possible Read", "BIT", "IN", "R", "1", "1", "1"), 
            Unassigned("FUNCTION_COMMUNICATION_CHECK_COMMCHECK_READ", "FUNCTION", "Communication Check Read", "WORD", "IN", "R", "1", "1", "1"), 
            Unassigned("FUNCTION_COMMUNICATION_CHECK_COMMCHECK_WRITE", "FUNCTION", "Communication Check Write", "WORD", "OUT", "W", "1", "1", "1"), 
            Unassigned("FUNCTION_RECIPE_SET_POSSIBLE_READ", "FUNCTION", "Recipe Set Possible Read", "BIT", "IN", "R", "1", "1", "1"), 
            Unassigned("MNT_LD01_EQUIPMENT_STATUS_LD", "MNT", "LD01 Equipment Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("MNT_LD01_PROCESS_STATUS_LD", "MNT", "LD01 Process Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("MNT_LD01_CHAMBER_STATUS_LD", "MNT", "LD01 Chamber Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("MNT_LD01_CURRENT_STEP", "MNT", "LD01 Current Step", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("MNT_LD01_AUTO_MODE_CHNG_STATUS", "MNT", "LD01 Auto Mode Change Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("MNT_LD01_ALARM_WARNING_STATUS", "MNT", "LD01 Alarm Warning Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("MNT_LD01_PPID", "MNT", "LD01 PPID", "STRING", "IN", "R", "1", "1", "10"), 
            Unassigned("SV_LD01_LD_CH_STATUS", "SV", "LD01 LD Channel Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("SV_LD01_LD_CURRENT_STEP", "SV", "LD01 Current Step", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("SV_LD01_AUTO_MODE_CHNG_STATUS", "SV", "LD01 Auto Mode Change Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("SV_LD01_ALARM_STATUS", "SV", "LD01 Alarm Status", "DWORD", "IN", "R", "1", "1", "2"), 
            Row("SV_SDC_ALIVE_ON_DEVICE1", "SV", "SDC Alive On Device1", 0, "WEFD6", "DWORD", "IN", "R", "1", "1", "2", "0", "Spec 0.10 물류; SDC_ALIVE_ON_DEVICE1; 2 Word; 주소 정의만 등록, 자동 감시 미연결; USE=0 유지", use: false), 
            Row("SV_SDC_ALIVE_ON_DEVICE2", "SV", "SDC Alive On Device2", 0, "WF010", "DWORD", "IN", "R", "1", "1", "2", "0", "Spec 0.10 물류; SDC_ALIVE_ON_DEVICE2; 2 Word; 주소 정의만 등록, 자동 감시 미연결; USE=0 유지", use: false), 
            Unassigned("PPID_ONLINE_PPID_NAME_READ", "PPID", "Online PPID Name Read", "STRING", "IN", "R", "1", "1", "8"), 
            Unassigned("PPID_ONLINE_PPID_NAME_WRITE", "PPID", "Online PPID Name Write", "STRING", "OUT", "W", "1", "1", "8"), 
            Unassigned("PPID_STAGE_SPEED_READ", "PPID", "Stage Speed Read", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("PPID_STAGE_SPEED_WRITE", "PPID", "Stage Speed Write", "DWORD", "OUT", "W", "1", "1", "2"), 
            Unassigned("PPID_LASER_POWER_READ", "PPID", "Laser Power Read", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("PPID_LASER_POWER_WRITE", "PPID", "Laser Power Write", "DWORD", "OUT", "W", "1", "1", "2"), 
            Unassigned("PPID_LASER_FREQUENCY_READ", "PPID", "Laser Frequency Read", "DWORD", "IN", "R", "1", "1", "2"), 
            Unassigned("PPID_LASER_FREQUENCY_WRITE", "PPID", "Laser Frequency Write", "DWORD", "OUT", "W", "1", "1", "2"), 
            Unassigned("EC_SET_ALIGN_STAGE_X1_POS_READ", "EC", "Set Align Stage X1 Position Read", "DOUBLE", "IN", "R", "0.0001", "10000", "2"), 
            Unassigned("EC_SET_ALIGN_STAGE_X1_POS_WRITE", "EC", "Set Align Stage X1 Position Write", "DOUBLE", "OUT", "W", "0.0001", "10000", "2"), 
            Unassigned("EC_SET_ALIGN_STAGE_Y1_POS_READ", "EC", "Set Align Stage Y1 Position Read", "DOUBLE", "IN", "R", "0.0001", "10000", "2"), 
            Unassigned("EC_SET_ALIGN_STAGE_Y1_POS_WRITE", "EC", "Set Align Stage Y1 Position Write", "DOUBLE", "OUT", "W", "0.0001", "10000", "2"), 
            Unassigned("DC_TOP_POWERMETER_1_MEASURE_VALUE", "DC", "Top Powermeter 1 Measure Value", "DOUBLE", "IN", "R", "0.001", "1000", "2"), 
            Unassigned("ALARM_F0000", "ALARM", "F0000 PLC Alarm", "BIT", "IN", "R", "1", "1", "1"), 
            Unassigned("WARNING_F2000", "WARNING", "F2000 PLC Battery Low Warning", "BIT", "IN", "R", "1", "1", "1"), 
        ];
        private static IReadOnlyDictionary<string, string> Unassigned(string id, string group, string name, string dataType, string direction, string access, string readScale, string writeScale, string length) => Row(id, group, name, 0, "UNASSIGNED", dataType, direction, access, readScale, writeScale, length, "0", "미사용: Spec 0.10 대응 신호 미확인; 주소와 자료형 확정 후 설정", use: false);
        // Position/speed feedback uses signed Int32 words scaled by 0.0001, enabled per the configured Spec 0.10 map.
        // Raw Busy and Servo code reads use separate, unscaled two-word entries.
        // DOUBLE currently means scaled signed Int32, not an IEEE floating-point value.
        private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreateMotorStatusRows() => [
            Row(
            "MOTOR_STAGE_Y_CUR_POS", 
            "MOTOR", 
            "Stage Y1 Current Position", 
            0, 
            "WC182", 
            "DOUBLE", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 물류 D349:G349 Stage_Y#1 Current Position; READ_SCALE=0.0001; WRITE_SCALE=10000"
        ), 
            Row(
            "MOTOR_STAGE_Y_CUR_SPEED", 
            "MOTOR", 
            "Stage Y1 Current Speed", 
            0, 
            "WC188", 
            "DOUBLE", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 물류 D352:G352 Stage_Y#1 Current Speed; READ_SCALE=0.0001; WRITE_SCALE=10000"
        ), 
            Row("MOTOR_STAGE_Y_CUR_ACC", "MOTOR", "STAGE_Y Current ACC", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "1", "1", "2", "0", "미사용: 현재 가속도 표시 제외; Spec 0.10 대응 신호 미확인", use: false), 
            Row("MOTOR_STAGE_Y_BUSY", "MOTOR", "Stage Y1 Busy", 0, "WC180", "DWORD", "IN", "R", "1", "1", "2", "1000", "Spec 0.10 물류 D348:G348 Stage_Y#1 Busy; 0=OFF 1=ON"), 
            Row("MOTOR_STAGE_Y_SERVO_ALARM_CODE", "MOTOR", "Stage Y1 Servo Alarm Code", 0, "WC18E", "DWORD", "IN", "R", "1", "1", "2", "1000", "Spec 0.10 물류 D355:G355 Stage_Y#1 Servo Alarm Code; raw code"), 
            Row("MOTOR_STAGE_Y_SERVO_WARNING_CODE", "MOTOR", "Stage Y1 Servo Warning Code", 0, "WC190", "DWORD", "IN", "R", "1", "1", "2", "1000", "Spec 0.10 물류 D356:G356 Stage_Y#1 Servo Warning Code; raw code"), 
            Row(
            "MOTOR_CAMERA_X_CUR_POS", 
            "MOTOR", 
            "Review X1 Current Position", 
            0, 
            "WC212", 
            "DOUBLE", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 물류 D421:G421 Review Axis Current Position; READ_SCALE=0.0001; WRITE_SCALE=10000"
        ), 
            Row(
            "MOTOR_CAMERA_X_CUR_SPEED", 
            "MOTOR", 
            "Review X1 Current Speed", 
            0, 
            "WC218", 
            "DOUBLE", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 물류 D424:G424 Review Axis Current Speed; READ_SCALE=0.0001; WRITE_SCALE=10000"
        ), 
            Row("MOTOR_CAMERA_X_BUSY", "MOTOR", "Review X1 Busy", 0, "WC210", "DWORD", "IN", "R", "1", "1", "2", "1000", "Spec 0.10 물류 D420:G420 Review Axis Busy; raw 0=OFF nonzero=BUSY"), 
            Row("MOTOR_CAMERA_X_CUR_ACC", "MOTOR", "REVIEW_X Current ACC", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "1", "1", "2", "0", "미사용: 현재 가속도 표시 제외; Spec 0.10 대응 신호 미확인", use: false), 
            Row(
            "MOTOR_WINDOW_Y_CUR_POS", 
            "MOTOR", 
            "Window Moving Y1 Current Position", 
            0, 
            "W1B02", 
            "DOUBLE", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PLC Axis27 M302:N302 + 공통 Current Position offset 2; READ_SCALE=0.0001; WRITE_SCALE=10000"
        ), 
            Row(
            "MOTOR_WINDOW_Y_CUR_SPEED", 
            "MOTOR", 
            "Window Moving Y1 Current Speed", 
            0, 
            "W1B08", 
            "DOUBLE", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PLC Axis27 M302:N302 + 공통 Current Speed offset 8; READ_SCALE=0.0001; WRITE_SCALE=10000"
        ), 
            Row("MOTOR_WINDOW_Y_CUR_ACC", "MOTOR", "WINDOW_Y Current ACC", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "1", "1", "2", "0", "미사용: 현재 가속도 표시 제외; Spec 0.10 대응 신호 미확인", use: false), 
        ];
#endregion
#region Handshakes
        // Spec 0.10: all handshakes use raw 0/10000 and READ_SCALE=0.0001 / WRITE_SCALE=10000.
        // Latest TPM is W804A/W804C. W8218 belongs to the zero-defence follow-up Review request.
        private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreateHandshakeRows() => [
            Row(
            "PROCESS_DO_ALIGN", 
            "IF_ALIGN", 
            "Process DO ALIGN", 
            0, 
            "W8111", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "100", 
            "Spec 0.10 PROCESS_DO_ALIGN; Stage Align request; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "SDC_DO_ALIGN_STATUS", 
            "IF_ALIGN", 
            "Stage Align Available", 
            0, 
            "WC300", 
            "WORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "1", 
            "100", 
            "Spec 0.10 SDC_DO_Align_Status; raw 10000 x READ_SCALE 0.0001 = 1 means Available; all other scaled values=Unavailable"
        ), 
            Row(
            "SDC_DO_ALIGN_COMPLETE", 
            "IF_ALIGN", 
            "Stage Align Complete", 
            0, 
            "WC302", 
            "WORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "1", 
            "100", 
            "Spec 0.10 SDC_DO_Align_Complete; raw 10000 x READ_SCALE 0.0001 = 1 means Complete; all other scaled values=Incomplete"
        ), 
            Row(
            "HANDSHAKE_STG_DOREADY_ABLE", 
            "IF_DOREADY", 
            "Stg DoReady ABLE", 
            0, 
            "WC102", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_DoReady_Able; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_DOREADY_START", 
            "IF_DOREADY", 
            "Stg DoReady START", 
            0, 
            "WC104", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_DoReady_Start; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_DOREADY_COMPLETE", 
            "IF_DOREADY", 
            "Stg DoReady COMPLETE", 
            0, 
            "WC106", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_DoReady_Complete; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_DOREADY_ABLE", 
            "IF_DOREADY", 
            "Prc DoReady ABLE", 
            0, 
            "W8200", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_READY_ABLE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_DOREADY_START", 
            "IF_DOREADY", 
            "Prc DoReady START", 
            0, 
            "W8202", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_READY_START; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_DOREADY_COMPLETE", 
            "IF_DOREADY", 
            "Prc DoReady COMPLETE", 
            0, 
            "W8204", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_READY_COMPLETE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_PROCESS_ABLE", 
            "IF_PROCESS", 
            "Stg Process ABLE", 
            0, 
            "WC10A", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_Process_Able; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_PROCESS_START", 
            "IF_PROCESS", 
            "Stg Process START", 
            0, 
            "WC10C", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_Process_Start; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_PROCESS_COMPLETE", 
            "IF_PROCESS", 
            "Stg Process COMPLETE", 
            0, 
            "WC10E", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_Process_Complete; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_PROCESS_ABLE", 
            "IF_PROCESS", 
            "Prc Process ABLE", 
            0, 
            "W8208", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_PROCESS_ABLE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_PROCESS_START", 
            "IF_PROCESS", 
            "Prc Process START", 
            0, 
            "W820A", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_PROCESS_START; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_PROCESS_COMPLETE", 
            "IF_PROCESS", 
            "Prc Process COMPLETE", 
            0, 
            "W820C", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_PROCESS_COMPLETE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_REVIEW_ABLE", 
            "IF_REVIEW", 
            "Stg Review ABLE", 
            0, 
            "WC112", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_Review_Able; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_REVIEW_START", 
            "IF_REVIEW", 
            "Stg Review START", 
            0, 
            "WC114", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_Review_Start; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_REVIEW_COMPLETE", 
            "IF_REVIEW", 
            "Stg Review COMPLETE", 
            0, 
            "WC116", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_Review_Complete; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_REVIEW_ABLE", 
            "IF_REVIEW", 
            "Prc Review ABLE", 
            0, 
            "W8210", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_REVIEW_ABLE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_REVIEW_START", 
            "IF_REVIEW", 
            "Prc Review START", 
            0, 
            "W8212", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_REVIEW_START; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_REVIEW_COMPLETE", 
            "IF_REVIEW", 
            "Prc Review COMPLETE", 
            0, 
            "W8214", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_REVIEW_COMPLETE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_REVIEW_ABLE_SDC", 
            "IF_PROCESS_MOVE", 
            "Stg_ReviewAbleSDC", 
            0, 
            "WC124", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_ReviewAbleSDC; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_REVIEW_COMPLETE_SDC", 
            "IF_PROCESS_MOVE", 
            "Stg_ReviewCompleteSDC", 
            0, 
            "WC128", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_ReviewCompleteSDC; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_REVIEW_GENERAL_RUNNING", 
            "IF_PROCESS_MOVE", 
            "Prc_ReviewGeneralRunning", 
            0, 
            "W8021", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_REVIEW_GENERAL_RUNNING; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_REVIEW_MOVE_START", 
            "IF_PROCESS_MOVE", 
            "Prc_Review_Move_Start", 
            0, 
            "W8029", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_REVIEW_MOVE_START; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_IOF_COMPLETE", 
            "IF_IOF", 
            "Stg_IOF_Complete", 
            0, 
            "WC13C", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_IOF_Complete; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_IOF_START_REQUEST", 
            "IF_IOF", 
            "Prc_IOF_StartRequest", 
            0, 
            "W8238", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IOF_START_REQUEST; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_IOF_COMPLETE_RESPONSE", 
            "IF_IOF", 
            "Prc_IOF_CompleteReponse", 
            0, 
            "W823A", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IOF_COMPLETE_RESPONSE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_TOP_POWERMETER_ABLE", 
            "IF_TOP_POWERMETER", 
            "Stg TPM ABLE", 
            0, 
            "WC118", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_TPM_Able; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_STG_TOP_POWERMETER_COMPLETE", 
            "IF_TOP_POWERMETER", 
            "Stg TPM COMPLETE", 
            0, 
            "WC11A", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 IPS_TPM_Complete; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_TOP_POWERMETER_ABLE", 
            "IF_TOP_POWERMETER", 
            "Prc TPM ABLE", 
            0, 
            "W804A", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_TPD_ABLE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        ), 
            Row(
            "HANDSHAKE_PRC_TOP_POWERMETER_COMPLETE", 
            "IF_TOP_POWERMETER", 
            "Prc TPM COMPLETE", 
            0, 
            "W804C", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 PROCESS_SEQUENCE_TPD_COMPLETE; Int32 raw 0=OFF 10000=ON; logical 0/1 after READ_SCALE"
        )
        ];
#endregion
#region Spec008
        // Spec 0.10: D/E/G/H/I are authoritative; stale J-column aliases are not separate routes.
        private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreateLatestScenarioRows() => [
            Row(
            "PROCESS_LASER_ON", 
            "IF_PROCESS_DATA", 
            "Process Actual Laser Emission", 
            0, 
            "W8005", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D175:G175 PROCESS_LASER_ON; 주소 정의만 등록; 시퀀스 연결 전 USE=0; raw 0/10000; 실제 출사 중에만 ON; 자동 출력 연결은 후속 작업; Map 0.10: 레이저 출사 시 항상 On상태 유지", 
            use: false
        ), 
            Row(
            "PROCESS_REVIEW_COMMAND_POSITION_X", 
            "IF_PROCESS_MOVE", 
            "Review COMMAND POSITION X", 
            0, 
            "W8011", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D181:G181 PROCESS_REVIEW_COMMAND_POSITION_X; 공통 Process Move/IOF 시퀀스에 연결; Review X 목표 mm; Map 0.10: mm 단위", 
            use: true
        ), 
            Row(
            "PROCESS_REVIEW_COMMAND_POSITION_Y", 
            "IF_PROCESS_MOVE", 
            "Review COMMAND POSITION Y", 
            0, 
            "W8013", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D182:G182 PROCESS_REVIEW_COMMAND_POSITION_Y; 공통 Process Move/IOF 시퀀스에 연결; Stage Y 목표 mm; Map 0.10: mm 단위", 
            use: true
        ), 
            Row(
            "PROCESS_REVIEW_COMMAND_SPEED_X", 
            "IF_PROCESS_MOVE", 
            "Review COMMAND SPEED X", 
            0, 
            "W8015", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D183:G183 PROCESS_REVIEW_COMMAND_SPEED_X; 공통 Process Move/IOF 시퀀스에 연결; Review X 속도 mm/sec; Map 0.10: mm/s 단위", 
            use: true
        ), 
            Row(
            "PROCESS_REVIEW_COMMAND_SPEED_Y", 
            "IF_PROCESS_MOVE", 
            "Review COMMAND SPEED Y", 
            0, 
            "W8017", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D184:G184 PROCESS_REVIEW_COMMAND_SPEED_Y; 공통 Process Move/IOF 시퀀스에 연결; Stage Y 속도 mm/sec; Map 0.10: mm/s 단위", 
            use: true
        ), 
            Row(
            "PROCESS_REVIEW_CONTROL_MODE", 
            "IF_PROCESS_MOVE", 
            "Review CONTROL MODE", 
            0, 
            "W801B", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D186:G186 PROCESS_REVIEW_CONTROL_MODE; 공통 Process Move/IOF 시퀀스에 연결; 논리값 0=XY; 1=Review X만; 2=Main Y만; raw 0/10000/20000; Map 0.10: 0 : X, Y 동시구동 / 10000 : Review X만 구동 / 20000 : Main Y만 구동", 
            use: true
        ), 
            Row(
            "PROCESS_REVIEW_STOP_SIGNAL", 
            "IF_PROCESS_MOVE", 
            "Review STOP SIGNAL", 
            0, 
            "W802F", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D196:G196 PROCESS_REVIEW_STOP_SIGNAL; 공통 Process Move/IOF 시퀀스에 연결; raw 10000=정지 요청; 관련 이동 I/F 복귀 필요; Map 0.10: 모션 즉정지 + 구동조건 관련 신호 리셋 (구동관련 I/F신호 리셋해줘야함)", 
            use: true
        ), 
            Row(
            "PROCESS_STAGE_SPEED", 
            "IF_PROCESS_DATA", 
            "Process Stage Speed", 
            0, 
            "W8032", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 가공 D198:I198 PROCESS_STAGE_SPEED; Recipe STAGE_SPEED mm/sec; DoReady 준비/Process 준비/START/COMPLETE 전에 WRITE_SCALE 적용 및 공유 캐시 확인", 
            use: true
        ), 
            Row(
            "PROCESS_PROCESS_COMPLETE", 
            "IF_PROCESS_DATA", 
            "Actual Machining Complete", 
            0, 
            "W8034", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 가공 D199:I199 PROCESS_PROCESS_COMPLETE; 실제 전체 Task/큐 정상 완료시 1 게시; Process COMPLETE와 구분; 다음 DoReady에서 0; raw 0/10000; 실패/STOP을 완료로 보고하지 않음", 
            use: true
        ), 
            Row(
            "IPS_ZERO_DEFENCE_OPT", 
            "IF_ZERO_DEFENCE", 
            "Stage Zero Defence Option", 
            0, 
            "WC11C", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 물류 D292:I292 IPS_ZeroDefenceOpt; Stage 0선방어 사용 여부; READ_SCALE 적용 논리값 0/1; Main 0선방어 옵션과 우선순위는 별도 계약; 자동 연결 전 USE=0", 
            use: false
        ), 
            Row(
            "IPS_REVIEW_MODE", 
            "IF_ZERO_DEFENCE", 
            "Stage Review Mode", 
            0, 
            "WC11E", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 물류 D293:I293 IPS_Review_Mode; raw 0=1줄 / 10000=3줄; READ_SCALE 적용 후 0=1줄 / 1=3줄; Main 1/3/5/7줄 옵션과 우선순위는 별도 계약; 자동 연결 전 USE=0", 
            use: false
        ), 
            Row(
            "IPS_REVIEW_CONTROL_MODE_Y", 
            "IF_PROCESS_MOVE", 
            "Stage Review Control Mode Y", 
            0, 
            "WC120", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 물류 D294:I294 IPS_ReviewControlModeY; 해당 축의 Process 구동 허가; SDC 공통 허가 WC124와 별개; READ_SCALE 적용 논리값 0/1; 공통 시퀀스에 연결", 
            use: true
        ), 
            Row(
            "IPS_REVIEW_CONTROL_MODE_X", 
            "IF_PROCESS_MOVE", 
            "Stage Review Control Mode X", 
            0, 
            "WC122", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 물류 D295:I295 IPS_ReviewControlModeX; 해당 축의 Process 구동 허가; SDC 공통 허가 WC124와 별개; READ_SCALE 적용 논리값 0/1; 공통 시퀀스에 연결", 
            use: true
        ), 
            Row(
            "IPS_REVIEW_POSITION_X_MOVING_SDC", 
            "IF_PROCESS_MOVE_DATA", 
            "Stage Review X Moving SDC", 
            0, 
            "WC12C", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D300:G300 IPS_ReviewPositionXMovingSDC; 주소 정의만 등록; 시퀀스 연결 전 USE=0; READ_SCALE 적용 논리값으로 사용; 모드의 상세 enum 계약은 사용 시 확인; Map 0.10: X Moving 동작 중 : Motor Busy로 대체 가능", 
            use: false
        ), 
            Row(
            "IPS_REVIEW_POSITION_Y_MOVING_SDC", 
            "IF_PROCESS_MOVE_DATA", 
            "Stage Review Y Moving SDC", 
            0, 
            "WC12E", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D301:G301 IPS_ReviewPositionYMovingSDC; 주소 정의만 등록; 시퀀스 연결 전 USE=0; READ_SCALE 적용 논리값으로 사용; 모드의 상세 enum 계약은 사용 시 확인; Map 0.10: Y Moving 동작 중 : Motor Busy로 대체 가능", 
            use: false
        ), 
            Row(
            "SDC_REVIEW_RETRY_REQUEST", 
            "IF_ZERO_DEFENCE", 
            "Zero Defence Follow-up Review Request", 
            0, 
            "W8218", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 W8218; 0선 NG & 후속 옵션 ON이면 logical 1/raw10000; Process 결과 종결 후 요청 게시; 후속 1줄 Review 실행은 별도 시퀀스", 
            use: true
        ), 
            Row(
            "IOF_SETTING", 
            "IF_IOF", 
            "IOF Setting", 
            0, 
            "W8230", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D454:G454 IOF_SETTING; 공통 Process Move/IOF 시퀀스에 연결; 논리값 0=일반 X; 1=0선 Y; 3=일반 Y; raw 0/10000/30000; Map 0.10: 10000 : 0선 Y방향 / 30000 : 일반 Y방향 / 0 : 일반 X방향", 
            use: true
        ), 
            Row(
            "IOF_TRIGGER_POINT_COUNT", 
            "IF_IOF", 
            "General IOF Point Count", 
            0, 
            "W8234", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D456:G456 IOF_TRIGGER_POINT_COUNT; 일반 IOF Review 이번 라인 Shot 수 M; 논리 개수 x WRITE_SCALE; W8300부터 검사 순서대로 X M개; 역방향은 역순; W8238 요청-WC13C 완료-W823A 응답; 실제/Simulation 공통 처리", 
            use: true
        ), 
            Row(
            "IOF_TRIGGER_ZERO_POINT_COUNT", 
            "IF_IOF", 
            "Zero Defence IOF Point Count", 
            0, 
            "W8236", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D457:G457 IOF_TRIGGER_ZERO_POINT_COUNT; 공통 Process Move/IOF 시퀀스에 연결; 0선 총 개수 N; 논리 4개는 raw 40000; Map 0.10: 0선 측정 시 계측 개수", 
            use: true
        ), 
            .. CreateIofPositionRows(),
            Row(
            "IPS_IOF_READ_COMPLETE", 
            "IF_IOF", 
            "Stage IOF Read Complete", 
            0, 
            "WC138", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(물류) D306:G306 IPS_IOF_ReadComplete; 공통 Process Move/IOF 시퀀스에 연결; raw 0/10000; Stage 소유 수신 신호", 
            use: true
        ), 
            Row(
            "IPS_IOF_BUFFER_RUN_COMPLETE", 
            "IF_IOF", 
            "Stage IOF Buffer Run Complete", 
            0, 
            "WC13A", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(물류) D307:G307 IPS_IOF_BufferRunComplete; 공통 Process Move/IOF 시퀀스에 연결; raw 0/10000; Stage 소유 수신 신호", 
            use: true
        ), 
            Row(
            "PROCESS_SEQUENCE_TPD_PTW_EXCHANGE_FLAG", 
            "IF_PTW", 
            "Protect Window Exchange Request", 
            0, 
            "W804E", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D212:G212 PR0CESS_SEQUENCE_TPD_PTW_EXCHANGE_FLAG; 현재매수 >= Max이면 요청(raw 10000); Max=0 즉시 요청; 교체완료 WF150 확인 후 Stage Tray Count를 현재매수로 저장 및 보고한 뒤 요청 OFF; TPM은 별도; ATT/Model Job 조건은 후속 작업; Map 0.10: 가공진행완료 시점에 보호윈도우 교체(이동) 요청 Flag", 
            use: true
        ), 
            Row(
            "PROCESS_SEQUENCE_TPD_PTW_MEASURE_FLAG", 
            "IF_PTW", 
            "Protect Window TPM Remeasure Request", 
            0, 
            "W8050", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D213:G213 PROCESS_SEQUENCE_TPD_PTW_MEASURE_FLAG; 주소 정의만 등록; 시퀀스 연결 전 USE=0; raw 10000=기본 TPM 후 재측정; 0이어도 Shift/교체 후 최초 TPM 1회 수행; Map 0.10: 보호윈도우 교체(이동) 후 파워보정 진행 여부 (Option상태보고 업데이트)", 
            use: false
        ), 
            Row(
            "PROCESS_SEQUENCE_PTW_PROCESS_COUNT", 
            "IF_PTW", 
            "Protect Window Process Count", 
            0, 
            "W8052", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(가공) D214:G214 PROCESS_SEQUENCE_PTW_PROCESS_COUNT; 레시피 CURRENT_PROCESS_COUNT를 AUTO START/정상 완료 직후/완료 인계 전에 게시; 가공 중 매수 판정 반복 없음; 교체완료시 Stage Tray Count 인계값 저장 및 게시; WRITE_SCALE 적용; Map 0.10: 보호윈도우 교체 후 Stage에서 관리하는 Count를 받아서 가공SW이어서 업데이트", 
            use: true
        ), 
            Row(
            "PROCESS_SEQUENCE_PTW_COMPLETE_FLAG", 
            "IF_PTW", 
            "Protect Window Process Complete Flag", 
            0, 
            "W8054", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D215:I215; 문서상 미사용; USE=0 유지", 
            use: false
        ), 
            Row(
            "IPS_PTW_CHANGED", 
            "IF_PTW", 
            "Stage Protect Window Auto Manual Mode", 
            0, 
            "WC12A", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 물류 D299:I299 IPS_PTW_Changed; PTW 동작 모드 raw 0=Auto / 10000=Manual; 물리 교체완료 edge로 사용 금지; USE=0 유지", 
            use: false
        ), 
            Row(
            "IPS_CASSETTE_MOVE_BUSY", 
            "IF_PTW", 
            "Stage Cassette Move Busy", 
            0, 
            "WC140", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D310:G310 IPS_CassetteMoveBusy; 주소 정의만 등록; 시퀀스 연결 전 USE=0; 교체 준비 시 Busy 인터락; logical 0/1", 
            use: false
        ), 
            Row(
            "IPS_PTW_EXCHANGE_BUSY", 
            "IF_PTW", 
            "Stage Protect Window Exchange Busy", 
            0, 
            "WC142", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D311:G311 IPS_PTWExchangeBusy; 주소 정의만 등록; 시퀀스 연결 전 USE=0; 교체 준비 시 Busy 인터락; logical 0/1", 
            use: false
        ), 
            Row(
            "IPS_PTW_MAPPING_BUSY", 
            "IF_PTW", 
            "Stage Protect Window Mapping Busy", 
            0, 
            "WC144", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D312:G312 IPS_PTWMappingBusy; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Cassette Mapping Busy; 평탄도 완료와 구분", 
            use: false
        ), 
            Row(
            "PTW_MOVING_FLAG", 
            "IF_PTW", 
            "Stage Protect Window Moving Flag", 
            0, 
            "WC2A0", 
            "WORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "1", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D488:G488 PTW_MOVING_FLAG; 주소 정의만 등록; 시퀀스 연결 전 USE=0; raw 10000=Moving; 기존 PLC 요청과 SDC 통지 수신 경로 확인 필요; Map 0.10: Stage > PLC_Req (Auto Ready Function)", 
            use: false
        ), 
            Row(
            "PTW_TRAY_COUNT", 
            "IF_PTW", 
            "Stage Protect Window Tray Count", 
            0, 
            "WC2B0", 
            "WORD", 
            "IN", 
            "R", 
            "1", 
            "1", 
            "1", 
            "1000", 
            "Spec 0.10 Config_Map Word Detail(물류) D504:G504 PTW_TRAY_COUNT; Stage Tray Count; 1 Word 원시 Count; WF150 완료 확인 시 현재 가공매수로 저장하고 W8052 보고", 
            use: true
        ), 
            Row(
            "PTW_EXCHANGE_SEQ_COMPLETE", 
            "IF_PTW", 
            "Stage Protect Window Section Complete", 
            0, 
            "WF150", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "1000", 
            "Spec 0.08 legacy; Spec 0.10에서는 해당 주소가 Spare; 대체 완료 주소 미확정으로 기존 경로 유지; Stage 확인 필요; Spec 0.10 Config_Map Word Detail(물류) D2065:G2065 PTW_EXCHANGE_SEQ_COMPLETE; 교체요청 중 완료(logical 1) 수신시 Stage Tray Count를 현재 가공매수로 저장 및 보고 후 요청 OFF; TPM 완료와 별개; 이전 완료 ON으로 새 요청을 즉시 해제하지 않음", 
            use: true
        ), 
            Row(
            "PTW_EXCHANGE_COMPLETE", 
            "IF_PTW", 
            "Stage Protect Window Exchange Result", 
            0, 
            "WF152", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.08 legacy; Spec 0.10에서는 해당 주소가 Spare; 대체 완료 주소 미확정으로 기존 경로 유지; Stage 확인 필요; Spec 0.10 Config_Map Word Detail(물류) D2066:G2066 PTW_EXCHANGE_COMPLETE; 주소 정의만 등록; 시퀀스 연결 전 USE=0; 최신 시나리오 raw 20000=인터락 NG; 정상 교체 완료로 처리 금지", 
            use: false
        ), 
            Row(
            "PTW_EXCHANGE_FLATNESS_COMPLETE", 
            "IF_PTW", 
            "Stage Protect Window Flatness Complete", 
            0, 
            "WF154", 
            "DWORD", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.08 legacy; Spec 0.10에서는 해당 주소가 Spare; 대체 완료 주소 미확정으로 기존 경로 유지; Stage 확인 필요; Spec 0.10 Config_Map Word Detail(물류) D2067:G2067 PTW_EXCHANGE_FLATNESS_COMPLETE; 주소 정의만 등록; 시퀀스 연결 전 USE=0; 실교체 후 평탄도 정상 직후 보고; 가공 위치 이동 전; 수신 경로 확인 필요", 
            use: false
        ), 
            Row(
            "EQ_STAGE_HOME_FLAG", 
            "IF_STAGE_STATE", 
            "Stage Home Flag", 
            0, 
            "WC012", 
            "WORD", 
            "IN", 
            "R", 
            "1", 
            "1", 
            "1", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D26:G26 EQ_Stage_HomeFlag; 주소 정의만 등록; 시퀀스 연결 전 USE=0; 명시된 raw 1=ON; 0=OFF; 초기 Home 완료 조건; Map 0.10: ON - 1, OFF - 0", 
            use: false
        ), 
            Row(
            "STAGE_DRYRUN_USE", 
            "IF_STAGE_STATE", 
            "Stage Dry Run Use", 
            0, 
            "WC288", 
            "WORD", 
            "IN", 
            "R", 
            "1", 
            "1", 
            "1", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D464:G464 STAGE_DRYRUN_USE; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Word 상태; 값 enum/배율은 활성화 전 Stage 계약 확인", 
            use: false
        ), 
            Row(
            "SDC_DRYRUN_USE", 
            "IF_STAGE_STATE", 
            "SDC Dry Run Use", 
            0, 
            "WC289", 
            "WORD", 
            "IN", 
            "R", 
            "1", 
            "1", 
            "1", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D465:G465 SDC_DRYRUN_USE; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Word 상태; 값 enum/배율은 활성화 전 Stage 계약 확인", 
            use: false
        ), 
            Row(
            "MOTOR_STAGE_Y_ENCODER_POS", 
            "MOTOR", 
            "Stage Y1 Encoder Position", 
            0, 
            "WC236", 
            "DOUBLE", 
            "IN", 
            "R", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(물류) D439:G439 StageY1_EncoderPosition; 주소 정의만 등록; 시퀀스 연결 전 USE=0; StageY1_EncoderPosition; 단위와 배율은 사용 전 Stage 계약 확인", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_X1", 
            "ALIGN_BASE", 
            "Recipe Align Base X1", 
            0, 
            "W8056", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D216:G216 PROCESS_RECIPE_ALIGNBASE_X1; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_X2", 
            "ALIGN_BASE", 
            "Recipe Align Base X2", 
            0, 
            "W8058", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D217:G217 PROCESS_RECIPE_ALIGNBASE_X2; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_X3", 
            "ALIGN_BASE", 
            "Recipe Align Base X3", 
            0, 
            "W805A", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D218:G218 PROCESS_RECIPE_ALIGNBASE_X3; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_X4", 
            "ALIGN_BASE", 
            "Recipe Align Base X4", 
            0, 
            "W805C", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D219:G219 PROCESS_RECIPE_ALIGNBASE_X4; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_Y1", 
            "ALIGN_BASE", 
            "Recipe Align Base Y1", 
            0, 
            "W805E", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D220:G220 PROCESS_RECIPE_ALIGNBASE_Y1; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_Y2", 
            "ALIGN_BASE", 
            "Recipe Align Base Y2", 
            0, 
            "W8060", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D221:G221 PROCESS_RECIPE_ALIGNBASE_Y2; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_Y3", 
            "ALIGN_BASE", 
            "Recipe Align Base Y3", 
            0, 
            "W8062", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D222:G222 PROCESS_RECIPE_ALIGNBASE_Y3; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_Y4", 
            "ALIGN_BASE", 
            "Recipe Align Base Y4", 
            0, 
            "W8064", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D223:G223 PROCESS_RECIPE_ALIGNBASE_Y4; 주소 정의만 등록; 시퀀스 연결 전 USE=0; Recipe 기준값의 가공 PC 보고 주소; Vision LastLoad 수신 주소가 아님; 실제 전송은 후속 연결; Map 0.10: Distortion Bench Mark", 
            use: false
        ), 
            Row(
            "PROCESS_LDPRO_ALARM", 
            "IF_PROCESS_DATA", 
            "Process Alarm Status", 
            0, 
            "W8003", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D174:I174 PROCESS_LDPRO_ALARM; ; raw 0/10000; logical 0/1; 자동 송신 연결은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_LDPRO_ALIVE", 
            "IF_PROCESS_DATA", 
            "Process Alive", 
            0, 
            "W8007", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D176:I176 PROCESS_LDPRO_ALIVE; 1초마다 On Off 점멸; raw 0/10000; logical 0/1; 자동 송신 연결은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_LDPRO_AUTO", 
            "IF_PROCESS_DATA", 
            "Process Auto Mode", 
            0, 
            "W8009", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D177:I177 PROCESS_LDPRO_AUTO; Auto / Manual 전환 여부; raw 0/10000; logical 0/1; 자동 송신 연결은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_LDPRO_MANUAL", 
            "IF_PROCESS_DATA", 
            "Process Manual Mode", 
            0, 
            "W800B", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D178:I178 PROCESS_LDPRO_MANUAL; ; raw 0/10000; logical 0/1; 자동 송신 연결은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_ICR_FLAG", 
            "IF_PROCESS_DATA", 
            "Process APC ICR Glass", 
            0, 
            "W8036", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D200:I200 PROCESS_ICR_FLAG; Glass Data정보에 ICR Flag 확인 시 APC 보정용 기판 표시; raw 0/10000; logical 0/1; 자동 송신 연결은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_SEQUENCE_TPM_LASER_ON", 
            "IF_PROCESS_DATA", 
            "TPM Actual Laser Emission", 
            0, 
            "W8042", 
            "DWORD", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D206:I206 PROCESS_SEQUENCE_TPM_LASER_ON; Auto 중 Top Power Meter 측정 시 Laser On일 경우; raw 0/10000; logical 0/1; 자동 송신 연결은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_X1", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit X1", 
            0, 
            "W8070", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D229:I229 PROCESS_RECIPE_ALIGNBASE_RAGNE_X1; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_X2", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit X2", 
            0, 
            "W8072", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D230:I230 PROCESS_RECIPE_ALIGNBASE_RAGNE_X2; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_X3", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit X3", 
            0, 
            "W8074", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D231:I231 PROCESS_RECIPE_ALIGNBASE_RAGNE_X3; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_X4", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit X4", 
            0, 
            "W8076", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D232:I232 PROCESS_RECIPE_ALIGNBASE_RAGNE_X4; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_Y1", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit Y1", 
            0, 
            "W8078", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D233:I233 PROCESS_RECIPE_ALIGNBASE_RAGNE_Y1; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_Y2", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit Y2", 
            0, 
            "W807A", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D234:I234 PROCESS_RECIPE_ALIGNBASE_RAGNE_Y2; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_Y3", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit Y3", 
            0, 
            "W807C", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D235:I235 PROCESS_RECIPE_ALIGNBASE_RAGNE_Y3; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "PROCESS_RECIPE_ALIGNBASE_RAGNE_Y4", 
            "ALIGN_BASE_LIMIT", 
            "Bench Mark Limit Y4", 
            0, 
            "W807E", 
            "DOUBLE", 
            "OUT", 
            "RW", 
            "0.0001", 
            "10000", 
            "2", 
            "0", 
            "Spec 0.10 Config_Map Word Detail(가공) D236:I236 PROCESS_RECIPE_ALIGNBASE_RAGNE_Y4; Distortion Bench Mark Limit Range; 문서의 RAGNE 표기를 유지; 단위는 Stage 계약 확인; Align Offset Tolerance와 다른 Bench Mark 허용범위; 값 선택/자동 송신은 후속 작업; USE=0", 
            use: false
        ), 
            Row(
            "STAGE_MCC_BUSY_FLAGS", 
            "IF_ALIGN", 
            "MCC Busy Word (bit1 Align)", 
            0, 
            "WFAB3",
            "WORD", 
            "IN", 
            "R", 
            "1", 
            "1", 
            "1", 
            "100", 
            "FAB3 ALIGN_BUSY 원시 Word; bit 1 (mask 2)=MCC_ALIGN_BUSY; AUTO 및 ReviewAble ON일 때 첫 Review X 선이동 조건",
            use: true
        ), 
        ];
        // Raw Vision/Stage names follow Spec 0.10. AK/DK-to-camera mapping is
        // intentionally not guessed; these routes stay disabled until connected.
        private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreateVisionSpecRows() => [
            Row("DISTORTION_INSP_BUSY", "VISION_DISTORTION", "DISTORTION_INSP_BUSY", 0, "W10098", "WORD", "IN", "R", "1", "1", "1", "0", "Spec 0.10 비전 D71:G71; 1 Word 원시 상태; 자동 연결 전 USE=0", use: false),
            Row("DISTORTION_INSP_RESULT_OK", "VISION_DISTORTION", "DISTORTION_INSP_RESULT_OK", 0, "W10099", "WORD", "IN", "R", "1", "1", "1", "0", "Spec 0.10 비전 D72:G72; 1 Word 원시 상태; 자동 연결 전 USE=0", use: false),
            Row("DISTORTION_INSP_RESULT_NG", "VISION_DISTORTION", "DISTORTION_INSP_RESULT_NG", 0, "W1009A", "WORD", "IN", "R", "1", "1", "1", "0", "Spec 0.10 비전 D73:G73; 1 Word 원시 상태; 자동 연결 전 USE=0", use: false),
            Row("DISTORTION_POS0_CAM1X", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM1X", 0, "W10100", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D169:H169; Distortion Pos0 (Align 완료위치) Cam1 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS0_CAM1Y", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM1Y", 0, "W10102", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D170:H170; Distortion Pos0 (Align 완료위치) Cam1 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS0_CAM2X", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM2X", 0, "W10104", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D171:H171; Distortion Pos0 (Align 완료위치) Cam2 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS0_CAM2Y", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM2Y", 0, "W10106", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D172:H172; Distortion Pos0 (Align 완료위치) Cam2 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS0_CAM3X", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM3X", 0, "W10108", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D173:H173; Distortion Pos0 (Align 완료위치) Cam3 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS0_CAM3Y", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM3Y", 0, "W1010A", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D174:H174; Distortion Pos0 (Align 완료위치) Cam3 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS0_CAM4X", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM4X", 0, "W1010C", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D175:H175; Distortion Pos0 (Align 완료위치) Cam4 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS0_CAM4Y", "VISION_DISTORTION_DATA", "DISTORTION_POS0_CAM4Y", 0, "W1010E", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D176:H176; Distortion Pos0 (Align 완료위치) Cam4 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS1_CAM2X", "VISION_DISTORTION_DATA", "DISTORTION_POS1_CAM2X", 0, "W10114", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D179:H179; Distortion Pos1 Cam2 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS1_CAM2Y", "VISION_DISTORTION_DATA", "DISTORTION_POS1_CAM2Y", 0, "W10116", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D180:H180; Distortion Pos1 Cam2 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS1_CAM4X", "VISION_DISTORTION_DATA", "DISTORTION_POS1_CAM4X", 0, "W10118", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D181:H181; Distortion Pos1 Cam4 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS1_CAM4Y", "VISION_DISTORTION_DATA", "DISTORTION_POS1_CAM4Y", 0, "W1011A", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D182:H182; Distortion Pos1 Cam4 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS2_CAM2X", "VISION_DISTORTION_DATA", "DISTORTION_POS2_CAM2X", 0, "W10120", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D185:H185; Distortion Pos2 Cam2 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS2_CAM2Y", "VISION_DISTORTION_DATA", "DISTORTION_POS2_CAM2Y", 0, "W10122", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D186:H186; Distortion Pos2 Cam2 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS2_CAM4X", "VISION_DISTORTION_DATA", "DISTORTION_POS2_CAM4X", 0, "W10124", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D187:H187; Distortion Pos2 Cam4 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS2_CAM4Y", "VISION_DISTORTION_DATA", "DISTORTION_POS2_CAM4Y", 0, "W10126", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D188:H188; Distortion Pos2 Cam4 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS3_CAM2X", "VISION_DISTORTION_DATA", "DISTORTION_POS3_CAM2X", 0, "W1012C", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D191:H191; Distortion Pos3 Cam2 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS3_CAM2Y", "VISION_DISTORTION_DATA", "DISTORTION_POS3_CAM2Y", 0, "W1012E", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D192:H192; Distortion Pos3 Cam2 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS3_CAM4X", "VISION_DISTORTION_DATA", "DISTORTION_POS3_CAM4X", 0, "W10130", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D193:H193; Distortion Pos3 Cam4 X 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("DISTORTION_POS3_CAM4Y", "VISION_DISTORTION_DATA", "DISTORTION_POS3_CAM4Y", 0, "W10132", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Spec 0.10 비전 D194:H194; Distortion Pos3 Cam4 Y 위치 (mm); READ_SCALE=0.0001 WRITE_SCALE=10000; Load용 원시 수신 주소 정의; AK/DK와 CAM/Pos 매칭은 미확정; 자동 폴링/보정 연결 안 함; USE=0", use: false),
            Row("STAGE_DISTORTION_INSP_REQ", "VISION_DISTORTION", "DISTORTION_INSP_REQ", 0, "WC0CB", "WORD", "IN", "R", "1", "1", "1", "0", "Spec 0.10 물류 D219:H219; On  1 ,  Off  0; 1 Word 원시값; 자동 연결 전 USE=0", use: false),
            Row("STAGE_DISTORTION_INSP_STEP", "VISION_DISTORTION", "DISTORTION_INSP_STEP", 0, "WC0CC", "WORD", "IN", "R", "1", "1", "1", "0", "Spec 0.10 물류 D220:H220; 1, 2, 3 , 4  position; 1 Word 원시값; 자동 연결 전 USE=0", use: false),
            Row("STAGE_DISTORTION_INSP_DIR", "VISION_DISTORTION", "DISTORTION_INSP_DIR", 0, "WC0CD", "WORD", "IN", "R", "1", "1", "1", "0", "Spec 0.10 물류 D221:H221; Moveprocess  1 , review  2; 1 Word 원시값; 자동 연결 전 USE=0", use: false),
            Row("IOF_POS_ARRAY_X_1", "IF_IOF_DATA", "IOF X Array (Unused)", 0, "W8900", "DOUBLE", "OUT", "RW", "0.0001", "10000", "1000", "0", "Spec 0.10 가공 D1326:G1825; W8900~W8CE7; 좌표 500개/1000 Word; 문서에서 사용안함으로 명시; USE=0 유지; 일반 X용으로 자동 선택하지 않음", use: false),
            Row("ALIGN_LASTLOAD_AK1_X", "ALIGN_LASTLOAD", "AK1 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK1 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_AK1_Y", "ALIGN_LASTLOAD", "AK1 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK1 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_AK2_X", "ALIGN_LASTLOAD", "AK2 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK2 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_AK2_Y", "ALIGN_LASTLOAD", "AK2 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK2 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_AK3_X", "ALIGN_LASTLOAD", "AK3 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK3 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_AK3_Y", "ALIGN_LASTLOAD", "AK3 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK3 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_AK4_X", "ALIGN_LASTLOAD", "AK4 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK4 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_AK4_Y", "ALIGN_LASTLOAD", "AK4 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision AK4 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK1_X", "ALIGN_LASTLOAD", "DK1 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK1 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK1_Y", "ALIGN_LASTLOAD", "DK1 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK1 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK2_X", "ALIGN_LASTLOAD", "DK2 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK2 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK2_Y", "ALIGN_LASTLOAD", "DK2 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK2 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK3_X", "ALIGN_LASTLOAD", "DK3 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK3 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK3_Y", "ALIGN_LASTLOAD", "DK3 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK3 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK4_X", "ALIGN_LASTLOAD", "DK4 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK4 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK4_Y", "ALIGN_LASTLOAD", "DK4 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK4 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK5_X", "ALIGN_LASTLOAD", "DK5 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK5 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK5_Y", "ALIGN_LASTLOAD", "DK5 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK5 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK6_X", "ALIGN_LASTLOAD", "DK6 LastLoad X", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK6 LastLoad X (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
            Row("ALIGN_LASTLOAD_DK6_Y", "ALIGN_LASTLOAD", "DK6 LastLoad Y", 0, "UNASSIGNED", "DOUBLE", "IN", "R", "0.0001", "10000", "2", "0", "Vision DK6 LastLoad Y (mm); 수신 주소 미정; Load 일회 읽기 대상; 실제 주소 및 스케일 확인 후 활성화", use: false),
        ];
        private static IEnumerable<IReadOnlyDictionary<string, string>> CreateIofPositionRows()
        {
            for (var index = 1; index <= 500; index++)
                yield return Row("IOF_POS_ARRAY_" + index.ToString(CultureInfo.InvariantCulture), "IF_IOF_DATA", "IOF Position " + index.ToString(CultureInfo.InvariantCulture),
                    0, "W" + (0x8300 + (index - 1) * 2).ToString("X", CultureInfo.InvariantCulture), "DOUBLE", "OUT", "RW", "0.0001", "10000", "2", "0",
                    $"Spec 0.10 가공 D{557 + index}:G{557 + index}; 촬영 순서 {index}; signed Int32 2 Word; 입력 mm; WRITE_SCALE=10000 / READ_SCALE=0.0001; 0선은 Y 2/4개, 일반 IOF는 해당 라인의 X를 실제 Count만 전송/읽기 확인; 역방향도 실제 촬영 순서; Count 밖 영역은 미사용/변경 안 함; 자동 폴링 없음");
        }
#endregion
    }
}
