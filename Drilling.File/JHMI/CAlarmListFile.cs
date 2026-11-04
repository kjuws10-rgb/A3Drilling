using Drilling.Common.Alarm;
using Drilling.File.Parser;

namespace Drilling.File.JHMI;

public sealed class CAlarmListFile(string configRoot) : CAlarmListFileBase
{
    public string FilePath { get; } = Path.Combine(configRoot, "JHMI_ALARMLIST.csv");

    public override IReadOnlyList<ST_ALARM_DEFINITION> Load()
    {
        const string table = "JHMI_ALARMLIST";
        string[] headers = ["CODE", "GROUP", "ID", "LEVEL", "CAUSE", "SOLUTION", "CIM_ADRESS"];
        CCsvParser.ValidateRequiredHeaders(FilePath, table, headers.Select(header => new[] { header }));
        var codes = new HashSet<int>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ST_ALARM_DEFINITION>();
        foreach (var (row, index) in CCsvParser.Read(FilePath).Select((row, index) => (row, index)))
        {
            var line = index + 2;
            var code = CCsvParser.ReadRequiredInt(CCsvParser.Get(row, "CODE"), table, line, "CODE");
            var id = CCsvParser.RequireText(row, table, line, "ID");
            var level = CCsvParser.RequireText(row, table, line, "LEVEL").ToUpperInvariant();
            if (!codes.Add(code)) throw new InvalidDataException($"{table}: {line}행 CODE {code}가 중복되었습니다.");
            if (!ids.Add(id)) throw new InvalidDataException($"{table}: {line}행 ID {id}가 중복되었습니다.");
            if (level is not ("HEAVY" or "LIGHT"))
                throw new InvalidDataException($"{table}: {line}행 LEVEL은 HEAVY 또는 LIGHT여야 합니다.");
            result.Add(new(code, CCsvParser.Get(row, "GROUP"), id, level,
                CCsvParser.Get(row, "CAUSE"), CCsvParser.Get(row, "SOLUTION"), CCsvParser.Get(row, "CIM_ADRESS")));
        }
        return result.OrderBy(row => row.Code).ToArray();
    }
}
