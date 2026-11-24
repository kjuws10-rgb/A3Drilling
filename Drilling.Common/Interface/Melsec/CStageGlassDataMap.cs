using System.Globalization;

namespace Drilling.Common.Interface;

public sealed record ST_STAGE_GLASS_DATA_MAP_FIELD(
    string ProductField,
    string Id,
    string Name,
    int WordAddress,
    EN_MELSEC_DATA_TYPE DataType,
    int WordLength,
    int ComponentIndex = 0)
{
    public string Address => "W" + WordAddress.ToString("X", CultureInfo.InvariantCulture);
}

public sealed record ST_STAGE_GLASS_DATA_SNAPSHOT(
    IReadOnlyDictionary<string, string> Values,
    DateTimeOffset ReadAt)
{
    public string GlassId => Values.GetValueOrDefault("H_GLASSID", "");
    public string LotId => Values.GetValueOrDefault("LOTID", "");
    public string Ppid => Values.GetValueOrDefault("PPID", "");
}

/// <summary>Spec 0.13 물류PC EQ Stage Edit GlassData(WF600) 공통 정의입니다.</summary>
public static class CStageGlassDataMap
{
    private const int BaseWordAddress = 0xF600;
    private const string MapIdPrefix = "STAGE_GLASS_";

    private sealed record ST_FIELD_SPEC(string Name, EN_MELSEC_DATA_TYPE DataType, int WordLength, bool SplitWords = false);

    private static readonly IReadOnlyList<ST_FIELD_SPEC> Specs =
    [
        new("H_GLASSID", EN_MELSEC_DATA_TYPE.String, 8),
        new("E_GLASSID", EN_MELSEC_DATA_TYPE.String, 8),
        new("LOTID", EN_MELSEC_DATA_TYPE.String, 8),
        new("BATCHID", EN_MELSEC_DATA_TYPE.String, 8),
        new("JOBID", EN_MELSEC_DATA_TYPE.String, 8),
        new("PORTID", EN_MELSEC_DATA_TYPE.String, 2),
        new("SLOTNO", EN_MELSEC_DATA_TYPE.String, 1),
        new("PRODUCT_TYPE", EN_MELSEC_DATA_TYPE.String, 2),
        new("PRODUCT_KIND", EN_MELSEC_DATA_TYPE.String, 2),
        new("PRODUCTID", EN_MELSEC_DATA_TYPE.String, 8),
        new("RUNSPECID", EN_MELSEC_DATA_TYPE.String, 8),
        new("LAYERID", EN_MELSEC_DATA_TYPE.String, 4),
        new("STEPID", EN_MELSEC_DATA_TYPE.String, 4),
        new("PPID", EN_MELSEC_DATA_TYPE.String, 10),
        new("FLOWID", EN_MELSEC_DATA_TYPE.String, 10),
        new("GLASS_SIZE", EN_MELSEC_DATA_TYPE.Word, 2, SplitWords: true),
        new("GLASS_THICKNESS", EN_MELSEC_DATA_TYPE.Word, 1),
        new("GLASS_STATE", EN_MELSEC_DATA_TYPE.Word, 1),
        new("GLASS_ORDER", EN_MELSEC_DATA_TYPE.String, 2),
        new("COMMENT", EN_MELSEC_DATA_TYPE.String, 8),
        new("USE_COUNT", EN_MELSEC_DATA_TYPE.String, 2),
        new("JUDGEMENT", EN_MELSEC_DATA_TYPE.String, 2),
        new("REASON_CODE", EN_MELSEC_DATA_TYPE.String, 2),
        new("INS_FLAG", EN_MELSEC_DATA_TYPE.String, 1),
        new("ENC_FLAG", EN_MELSEC_DATA_TYPE.String, 1),
        new("PRERUN_FLAG", EN_MELSEC_DATA_TYPE.String, 1),
        new("TURN_DIR", EN_MELSEC_DATA_TYPE.String, 1),
        new("FLIP_STATE", EN_MELSEC_DATA_TYPE.String, 1),
        new("WORK_STATE", EN_MELSEC_DATA_TYPE.String, 2),
        new("MULTI_USE", EN_MELSEC_DATA_TYPE.String, 8),
        new("PAIR_GLASSID", EN_MELSEC_DATA_TYPE.String, 8),
        new("PAIR_PPID", EN_MELSEC_DATA_TYPE.String, 10),
        new("OPTIONNAME1", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONVALUE1", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONNAME2", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONVALUE2", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONNAME3", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONVALUE3", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONNAME4", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONVALUE4", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONNAME5", EN_MELSEC_DATA_TYPE.String, 20),
        new("OPTIONVALUE5", EN_MELSEC_DATA_TYPE.String, 20),
        new("ETC", EN_MELSEC_DATA_TYPE.String, 16)
    ];

    public static IReadOnlyList<string> ProductFieldNames { get; } = Array.AsReadOnly(Specs.Select(field => field.Name).ToArray());
    public static IReadOnlyList<ST_STAGE_GLASS_DATA_MAP_FIELD> Fields { get; } = CreateFields();
    public static IReadOnlyList<string> MapIds { get; } = Array.AsReadOnly(Fields.Select(field => field.Id).ToArray());

    public static bool IsMapRow(ST_MELSEC_MAP_DATA row) => row.Id.StartsWith(MapIdPrefix, StringComparison.OrdinalIgnoreCase);

    public static async Task<ST_STAGE_GLASS_DATA_SNAPSHOT> ReadAsync(CMelsec melsec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(melsec);
        cancellationToken.ThrowIfCancellationRequested();

        // 전 항목이 같은 연속 Poll Block이므로 첫 항목 새로고침 한 번으로 WF600~WF767을 원자적으로 갱신합니다.
        await melsec.RefreshCachedValueAsync(Fields[0].Id, cancellationToken).ConfigureAwait(false);
        var samples = melsec.GetCachedSnapshot(MapIds, out _);
        var errors = new List<string>();
        foreach (var field in Fields)
        {
            if (!samples.TryGetValue(field.Id, out var sample) || !sample.IsValid || sample.Value is null)
            {
                var reason = sample?.ErrorMessage;
                errors.Add(field.Name + (string.IsNullOrWhiteSpace(reason) ? " 데이터 없음" : $" · {reason}"));
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("STAGE PC Glass Data를 읽지 못했습니다.\n" + string.Join("\n", errors.Take(5)));

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in Specs)
        {
            var fields = Fields.Where(field => field.ProductField.Equals(spec.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(field => field.ComponentIndex).ToArray();
            values[spec.Name] = string.Join(",", fields.Select(field => FormatValue(samples[field.Id].Value)));
        }

        values["H_GLASSID"] = values["H_GLASSID"].Trim();
        if (string.IsNullOrWhiteSpace(values["H_GLASSID"]))
            throw new InvalidOperationException("STAGE PC Glass Data의 H_GLASSID가 비어 있습니다.");

        var readAt = Fields.Select(field => samples[field.Id].UpdatedAt).Where(value => value.HasValue)
            .Select(value => value!.Value).DefaultIfEmpty(DateTimeOffset.UtcNow).Max();
        return new(values, readAt);
    }

    private static IReadOnlyList<ST_STAGE_GLASS_DATA_MAP_FIELD> CreateFields()
    {
        var result = new List<ST_STAGE_GLASS_DATA_MAP_FIELD>();
        var address = BaseWordAddress;
        foreach (var spec in Specs)
        {
            if (spec.SplitWords)
            {
                for (var index = 0; index < spec.WordLength; index++)
                {
                    result.Add(new(spec.Name, $"{MapIdPrefix}{spec.Name}_{index + 1}", $"{spec.Name}[{index + 1}]",
                        address + index, EN_MELSEC_DATA_TYPE.Word, 1, index));
                }
            }
            else
            {
                result.Add(new(spec.Name, MapIdPrefix + spec.Name, spec.Name, address, spec.DataType, spec.WordLength));
            }

            address += spec.WordLength;
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        string text => text.Trim(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
