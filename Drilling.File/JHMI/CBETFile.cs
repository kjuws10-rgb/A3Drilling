using System.Globalization;
using Drilling.Common.Managers;
using Drilling.Common.Interface;
using Drilling.Common.Motion;
using Drilling.Common.Alarm;
using Drilling.Common.InterLock;
using Drilling.Common.Station;
using Drilling.File.Parser;

namespace Drilling.File.JHMI;
public sealed class CBETFile(string configRoot) : CBETFileBase
{
    private static readonly IReadOnlyList<string> Headers = ["INDEX", "DESCRIPTION", "DIV", "MAG", "SPOTSIZE"];
    public override Task<IReadOnlyList<ST_BET_TABLE_DATA>> Load(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureFiles();
        return Task.FromResult<IReadOnlyList<ST_BET_TABLE_DATA>>(ReadTable(GetFormPath()));
    }

    public override Task Save(IReadOnlyList<ST_BET_TABLE_DATA> table, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteTable(GetFormPath(), table);
        return Task.CompletedTask;
    }

    private void EnsureFiles()
    {
        if (!System.IO.File.Exists(GetFormPath()))
        {
            WriteTable(GetFormPath(), CreateDefaultTable());
        }
    }

    private List<ST_BET_TABLE_DATA> ReadTable(string path)
    {
        bool ShouldIncludeItem(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return !string.IsNullOrWhiteSpace(CCsvParser.Get(row, "INDEX"));
        }

        global::Drilling.Common.Interface.ST_BET_TABLE_DATA BuildBet(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row, int order)
        {
            return new ST_BET_TABLE_DATA(ReadInt(CCsvParser.Get(row, "INDEX"), order), ReadMotorStep(CCsvParser.Get(row, "MAG")), ReadMotorStep(CCsvParser.Get(row, "DIV")), CCsvParser.Get(row, "DESCRIPTION"));
        }

        int SelectIndex(global::Drilling.Common.Interface.ST_BET_TABLE_DATA row)
        {
            return row.Index;
        }

        return CCsvParser.Read(path).Where(ShouldIncludeItem).Select(BuildBet).OrderBy(SelectIndex).ToList();
    }

    private static void WriteTable(string path, IReadOnlyList<ST_BET_TABLE_DATA> table)
    {
        int SelectIndex(global::Drilling.Common.Interface.ST_BET_TABLE_DATA row)
        {
            return row.Index;
        }

        global::System.Collections.Generic.Dictionary<string, string> BuildBetTableRow(global::Drilling.Common.Interface.ST_BET_TABLE_DATA row)
        {
            return new Dictionary<string, string>
            {
                ["INDEX"] = row.Index.ToString(CultureInfo.InvariantCulture),
                ["DESCRIPTION"] = row.Description,
                ["DIV"] = row.Divergence.ToString(CultureInfo.InvariantCulture),
                ["MAG"] = row.Magnification.ToString(CultureInfo.InvariantCulture),
                ["SPOTSIZE"] = row.SpotSize.ToString("F6", CultureInfo.InvariantCulture)
            };
        }

        var rows = table.OrderBy(SelectIndex).Select(BuildBetTableRow);
        CCsvParser.Write(path, Headers, rows);
    }

    private static IReadOnlyList<ST_BET_TABLE_DATA> CreateDefaultTable()
    {
        return[new(0, 1020, 1626, "2times"), new(1, 2351, 1118, "3times"), new(2, 3014, 1278, "4times"), new(3, 3410, 1706, "5times"), new(4, 3673, 2267, "6times")];
    }

    private static int ReadMotorStep(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(parsed), 0, 4500);
    }

    private string GetFormPath()
    {
        return Path.Combine(configRoot, "JHMI_BET.csv");
    }

    private static int ReadInt(string value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private static double ReadDouble(string value, double defaultValue)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }
}
