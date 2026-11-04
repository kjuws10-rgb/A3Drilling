using System.Globalization;
using Drilling.Common.Product;
using Drilling.File.Parser;

namespace Drilling.File.Product;
public sealed class CProductFile(string configRoot) : CProductFileBase
{
    private const int DefaultReviewBeamNo = 1;
    private static readonly IReadOnlyList<string> ActiveHeaders = ["TYPE", "NAME", "VALUE"];
    private static readonly IReadOnlyList<string> HistoryHeaders = ["OCCURRED_AT", "ROW_TYPE", "GLASS_ID", "PROCESS_ID", "RECIPE_ID", "ACTION", "STATE", "RESULT", "DETAIL"];
    private readonly string _productRoot = Path.Combine(Directory.GetParent(configRoot)?.FullName ?? configRoot, "Data", "Product");
    public override Task<ST_PRODUCT_DATA?> LoadActive(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activePath = GetActiveProductPath();
        if (!System.IO.File.Exists(activePath))
        {
            return Task.FromResult<ST_PRODUCT_DATA?>(null);
        }

        var rows = CCsvParser.Read(activePath);
        if (rows.Count == 0)
        {
            throw new InvalidDataException("Active Product file is empty.");
        }

        bool MatchesItem(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return !string.IsNullOrWhiteSpace(CCsvParser.Get(row, "TYPE"));
        }

        if (!rows.Any(MatchesItem))
        {
            throw new InvalidDataException("Active Product file is not in the current compact format.");
        }

        var product = LoadCompactActive(rows);
        return product is null ? throw new InvalidDataException("Active Product file does not contain a valid H_GLASSID.") : Task.FromResult<ST_PRODUCT_DATA?>(product);
    }

    private static ST_PRODUCT_DATA? LoadCompactActive(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var productValues = ReadSection(rows, "PRODUCT");
        var glassId = ReadAny(productValues, "H_GLASSID");
        if (string.IsNullOrWhiteSpace(glassId))
        {
            return null;
        }

        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "H_GLASSID",
            "LOTID",
            "PROCESS_ID",
            "PPID",
            "PRODUCT_STATE",
            "JUDGEMENT",
            "CREATED_AT",
            "STARTED_AT",
            "COMPLETED_AT"
        };
        var parameters = ReadSection(rows, "RECIPE");
        bool ShouldIncludeItem(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return !reservedNames.Contains(item.Key);
        }

        foreach (var item in productValues.Where(ShouldIncludeItem))
        {
            parameters.TryAdd(item.Key, item.Value);
        }

        var alignValues = ReadSection(rows, "ALIGN");
        var distortionValues = ReadSection(rows, "DISTORTION");
        var reviewValues = ReadSection(rows, "REVIEW");
        var reviewResultValues = ReadSection(rows, "REVIEW_RESULT");
        var review = ParseCompactReview(reviewValues, reviewResultValues);
        ValidateReview(review);
        string SelectName(string name)
        {
            return name;
        }

        string SelectValue(string name)
        {
            return ReadAny(productValues, name);
        }

        var normalizedProductValues = CProductManager.ProductFieldNames.ToDictionary(SelectName, SelectValue, StringComparer.OrdinalIgnoreCase);
        normalizedProductValues["H_GLASSID"] = glassId;
        normalizedProductValues["LOTID"] = ReadAny(productValues, "LOTID");
        normalizedProductValues["PPID"] = ReadAny(productValues, "PPID");
        return new ST_PRODUCT_DATA(
            new ST_PRODUCT_INFO(normalizedProductValues), 
            new ST_PRODUCT_RECIPE_DATA(parameters), 
            new ST_PRODUCT_ALIGN_DATA(
            ParseEnum(ReadAny(alignValues, "STATE"), EN_PRODUCT_PROCESS_STATE.NotStarted), 
            DefaultText(ReadAny(alignValues, "RESULT"), "PENDING"), 
            ParseNullableDouble(ReadAny(alignValues, "OFFSET_X")), 
            ParseNullableDouble(ReadAny(alignValues, "OFFSET_Y")), 
            ParseNullableDouble(ReadAny(alignValues, "ROTATION"))
        ), 
            new ST_PRODUCT_DISTORTION_DATA(ParseEnum(ReadAny(distortionValues, "STATE"), EN_PRODUCT_PROCESS_STATE.NotStarted), DefaultText(ReadAny(distortionValues, "RESULT"), "PENDING"), ParseNullableDouble(ReadAny(distortionValues, "MAX_DA"))), 
            review, 
            new ST_PRODUCT_RUNTIME_DATA([])
        );
    }

    public override Task SaveActive(ST_PRODUCT_DATA product, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var name in CProductManager.ProductFieldNames)
        {
            AddCompactRow(rows, "PRODUCT", name, product.Product.Get(name));
        }

        bool ShouldIncludeItem(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return !IsCompactProductCoreName(item.Key);
        }

        string SelectKey(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return item.Key;
        }

        global::System.Collections.Generic.IReadOnlyDictionary<string, string> BuildRecipeParameterCompactRow(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return CompactRow("RECIPE", item.Key, item.Value);
        }

        rows.AddRange(product.Parameters.Where(ShouldIncludeItem).OrderBy(SelectKey, StringComparer.OrdinalIgnoreCase).Select(BuildRecipeParameterCompactRow));
        AddCompactRow(rows, "ALIGN", "STATE", product.Align.State.ToString().ToUpperInvariant());
        AddCompactRow(rows, "ALIGN", "RESULT", product.Align.Result);
        AddCompactRow(rows, "ALIGN", "OFFSET_X", FormatDouble(product.Align.OffsetX));
        AddCompactRow(rows, "ALIGN", "OFFSET_Y", FormatDouble(product.Align.OffsetY));
        AddCompactRow(rows, "ALIGN", "ROTATION", FormatDouble(product.Align.Rotation));
        AddCompactRow(rows, "DISTORTION", "STATE", product.Distortion.State.ToString().ToUpperInvariant());
        AddCompactRow(rows, "DISTORTION", "RESULT", product.Distortion.Result);
        AddCompactRow(rows, "DISTORTION", "MAX_DA", FormatDouble(product.Distortion.MaxDa));
        AddCompactRow(rows, "REVIEW", "STATE", product.Review.State.ToString().ToUpperInvariant());
        AddCompactRow(rows, "REVIEW", "INSPECTION_METHOD", product.Review.InspectionMethod);
        AddCompactRow(rows, "REVIEW", "RECIPE_ID", product.Review.RecipeId);
        AddCompactRow(rows, "REVIEW", "RULE", product.Review.Rule);
        AddCompactRow(rows, "REVIEW", "RULE_SCOPE", product.Review.RuleScope);
        AddCompactRow(rows, "REVIEW", "BEAM_NO", product.Review.BeamNo.ToString(CultureInfo.InvariantCulture));
        AddCompactRow(rows, "REVIEW", "REFERENCE_CELL_NO", product.Review.ReferenceCellNo?.ToString(CultureInfo.InvariantCulture) ?? "");
        AddCompactRow(rows, "REVIEW", "REFERENCE_SHOT", product.Review.ReferenceShot);
        AddCompactRow(rows, "REVIEW", "ROUGH_REFERENCE", product.Review.RoughReference);
        AddCompactRow(rows, "REVIEW", "FINE_LINE_COUNT", product.Review.FineLineCount.ToString(CultureInfo.InvariantCulture));
        AddCompactRow(rows, "REVIEW", "TOTAL_COUNT", product.Review.TotalCount.ToString(CultureInfo.InvariantCulture));
        if (product.Review.SavedAt.HasValue)
        {
            AddCompactRow(rows, "REVIEW_RESULT", "SAVED_AT", FormatDate(product.Review.SavedAt));
        }

        int SelectCellNo(global::Drilling.Common.Product.ST_PRODUCT_REVIEW_RESULT item)
        {
            return item.CellNo;
        }

        string SelectShotKey(global::Drilling.Common.Product.ST_PRODUCT_REVIEW_RESULT item)
        {
            return item.ShotKey;
        }

        foreach (var result in product.Review.Results.OrderBy(SelectCellNo).ThenBy(SelectShotKey, StringComparer.OrdinalIgnoreCase))
        {
            AddCompactRow(rows, "REVIEW_RESULT", $"{result.ShotKey}.HEAD_NO", result.HeadNo.ToString(CultureInfo.InvariantCulture));
            AddCompactRow(rows, "REVIEW_RESULT", $"{result.ShotKey}.CELL_NO", result.CellNo.ToString(CultureInfo.InvariantCulture));
            AddCompactRow(rows, "REVIEW_RESULT", $"{result.ShotKey}.BEAM_NO", result.BeamNo.ToString(CultureInfo.InvariantCulture));
            AddCompactRow(rows, "REVIEW_RESULT", $"{result.ShotKey}.ERROR_X", result.ErrorX.ToString("G", CultureInfo.InvariantCulture));
            AddCompactRow(rows, "REVIEW_RESULT", $"{result.ShotKey}.ERROR_Y", result.ErrorY.ToString("G", CultureInfo.InvariantCulture));
            AddCompactRow(rows, "REVIEW_RESULT", $"{result.ShotKey}.JUDGE", result.Judge);
        }

        WriteActiveProductAtomically(rows);
        return Task.CompletedTask;
    }

    private void WriteActiveProductAtomically(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var activePath = GetActiveProductPath();
        var temporaryPath = $"{activePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            CCsvParser.Write(temporaryPath, ActiveHeaders, rows);
            if (System.IO.File.Exists(activePath))
            {
                System.IO.File.Replace(temporaryPath, activePath, null);
            }
            else
            {
                System.IO.File.Move(temporaryPath, activePath);
            }
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    public override Task ClearActive(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteIfExists(GetActiveProductPath());
        return Task.CompletedTask;
    }

    public override Task AppendHistory(ST_PRODUCT_HISTORY history, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetHistoryPath(history.OccurredAt);
        var rows = CCsvParser.Read(path)
            .Select(ParseHistory)
            .Where(item => item is not null)
            .Select(item => ToHistoryRow(item!))
            .Append(ToHistoryRow(history))
            .ToArray();
        WriteRowsAtomically(path, HistoryHeaders, rows);
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<ST_PRODUCT_HISTORY>> LoadHistory(int maxRows = 100, int days = 14, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var today = DateTime.Today;
        global::System.DateTime SelectValue(int offset)
        {
            return today.AddDays(-offset);
        }

        string SelectHistoryPath(global::System.DateTime date)
        {
            return GetHistoryPath(new DateTimeOffset(date));
        }

        bool ShouldIncludeItem(global::Drilling.Common.Product.ST_PRODUCT_HISTORY? history)
        {
            return history is not null;
        }

        global::Drilling.Common.Product.ST_PRODUCT_HISTORY SelectItem(global::Drilling.Common.Product.ST_PRODUCT_HISTORY? history)
        {
            return history!;
        }

        global::System.DateTimeOffset SelectOccurredAt(global::Drilling.Common.Product.ST_PRODUCT_HISTORY history)
        {
            return history.OccurredAt;
        }

        var histories = Enumerable.Range(0, Math.Max(1, days)).Select(SelectValue).Select(SelectHistoryPath).Where(System.IO.File.Exists).SelectMany(CCsvParser.Read).Select(ParseHistory).Where(ShouldIncludeItem).Select(SelectItem).OrderByDescending(SelectOccurredAt).Take(Math.Max(1, maxRows)).ToArray();
        return Task.FromResult<IReadOnlyList<ST_PRODUCT_HISTORY>>(histories);
    }

    private string GetActiveProductPath()
    {
        return Path.Combine(_productRoot, "ActiveProduct.csv");
    }

    private string GetHistoryPath(DateTimeOffset timestamp)
    {
        return Path.Combine(_productRoot, "History", timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture), $"ProductHistory_{timestamp:yyyyMMdd}.csv");
    }

    private static void ValidateReview(ST_PRODUCT_REVIEW_DATA review)
    {
        if (review.State == EN_PRODUCT_PROCESS_STATE.Completed && review.Results.Count == 0)
        {
            throw new InvalidDataException("Active Product validation failed. REVIEW is COMPLETED but REVIEW_RESULT is empty.");
        }

        if (review.State == EN_PRODUCT_PROCESS_STATE.NotStarted && review.Results.Count > 0)
        {
            throw new InvalidDataException("Active Product validation failed. REVIEW_RESULT exists while REVIEW is NOT_STARTED.");
        }
    }

    private static ST_PRODUCT_REVIEW_DATA ParseCompactReview(IReadOnlyDictionary<string, string> reviewValues, IReadOnlyDictionary<string, string> resultValues)
    {
        bool ShouldIncludeItem(global::System.Collections.Generic.KeyValuePair<string, string> item)
        {
            return !item.Key.Equals("SAVED_AT", StringComparison.OrdinalIgnoreCase);
        }

        int SelectCellNo(global::Drilling.Common.Product.ST_PRODUCT_REVIEW_RESULT result)
        {
            return result.CellNo;
        }

        string SelectShotKey(global::Drilling.Common.Product.ST_PRODUCT_REVIEW_RESULT result)
        {
            return result.ShotKey;
        }

        var groupedResults = resultValues.Where(ShouldIncludeItem).Select(item =>
        {
            var separator = item.Key.LastIndexOf('.');
            return separator > 0 ? new
            {
                ShotKey = item.Key[..separator],
                Field = item.Key[(separator + 1)..],
                item.Value
            }

            : null;
        }).Where(item => item is not null).GroupBy(item => item!.ShotKey, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var values = group.ToDictionary(item => item!.Field, item => item!.Value, StringComparer.OrdinalIgnoreCase);
            return new ST_PRODUCT_REVIEW_RESULT(
                group.Key, 
                ParseInt(ReadAny(values, "HEAD_NO")), 
                ParseInt(ReadAny(values, "CELL_NO")), 
                ParseInt(ReadAny(values, "BEAM_NO"), DefaultReviewBeamNo), 
                ParseNullableDouble(ReadAny(values, "ERROR_X")) ?? 0.0, 
                ParseNullableDouble(ReadAny(values, "ERROR_Y")) ?? 0.0, 
                ReadAny(values, "JUDGE")
            );
        }).OrderBy(SelectCellNo).ThenBy(SelectShotKey, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ST_PRODUCT_REVIEW_DATA(
            ParseEnum(ReadAny(reviewValues, "STATE"), EN_PRODUCT_PROCESS_STATE.NotStarted),
            ParseDate(ReadAny(resultValues, "SAVED_AT")),
            groupedResults)
        {
            InspectionMethod = ReadAny(reviewValues, "INSPECTION_METHOD"),
            RecipeId = ReadAny(reviewValues, "RECIPE_ID"),
            Rule = ReadAny(reviewValues, "RULE"),
            RuleScope = ReadAny(reviewValues, "RULE_SCOPE"),
            BeamNo = ParseInt(ReadAny(reviewValues, "BEAM_NO"), DefaultReviewBeamNo),
            ReferenceCellNo = ParseNullableInt(ReadAny(reviewValues, "REFERENCE_CELL_NO")),
            ReferenceShot = ReadAny(reviewValues, "REFERENCE_SHOT"),
            RoughReference = ReadAny(reviewValues, "ROUGH_REFERENCE"),
            FineLineCount = ParseInt(ReadAny(reviewValues, "FINE_LINE_COUNT")),
            TotalCount = ParseInt(ReadAny(reviewValues, "TOTAL_COUNT"))
        };
    }

    private static Dictionary<string, string> ReadSection(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string type)
    {
        bool MatchesSectionType(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return CCsvParser.Get(row, "TYPE").Equals(type, StringComparison.OrdinalIgnoreCase);
        }

        bool HasSectionName(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return !string.IsNullOrWhiteSpace(CCsvParser.Get(row, "NAME"));
        }

        string SelectGroupKey(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return CCsvParser.Get(row, "NAME");
        }

        string SelectKey(global::System.Linq.IGrouping<string, global::System.Collections.Generic.IReadOnlyDictionary<string, string>> group)
        {
            return group.Key;
        }

        string SelectValue(global::System.Linq.IGrouping<string, global::System.Collections.Generic.IReadOnlyDictionary<string, string>> group)
        {
            return CCsvParser.Get(group.Last(), "VALUE");
        }

        return rows.Where(MatchesSectionType).Where(HasSectionName).GroupBy(SelectGroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadAny(IReadOnlyDictionary<string, string> values, params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return "";
    }

    private static void AddCompactRow(ICollection<IReadOnlyDictionary<string, string>> rows, string type, string name, string value)
    {
        rows.Add(CompactRow(type, name, value));
    }

    private static IReadOnlyDictionary<string, string> CompactRow(string type, string name, string value)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TYPE"] = type,
            ["NAME"] = name,
            ["VALUE"] = value
        };
    }

    private static bool IsCompactProductCoreName(string name)
    {
        return name.Trim().ToUpperInvariant() switch
        {
            "H_GLASSID" or "LOTID" or "PROCESS_ID" or "PPID" or "PRODUCT_STATE" or "JUDGEMENT" or "CREATED_AT" or "STARTED_AT" or "COMPLETED_AT" => true,
            _ => false
        };
    }

    private static string DefaultText(string value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static int ParseInt(string value, int defaultValue = 0)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    private static int? ParseNullableInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static double? ParseNullableDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string FormatDouble(double? value)
    {
        return value?.ToString("G", CultureInfo.InvariantCulture) ?? "";
    }

    private static IReadOnlyDictionary<string, string> ToHistoryRow(ST_PRODUCT_HISTORY history)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OCCURRED_AT"] = FormatDate(history.OccurredAt),
            ["ROW_TYPE"] = "EVENT",
            ["GLASS_ID"] = history.GlassId,
            ["PROCESS_ID"] = history.ProcessId,
            ["RECIPE_ID"] = history.RecipeId,
            ["ACTION"] = history.Action,
            ["STATE"] = history.State,
            ["RESULT"] = history.Result,
            ["DETAIL"] = history.Detail
        };
    }

    private static ST_PRODUCT_HISTORY? ParseHistory(IReadOnlyDictionary<string, string> row)
    {
        var occurredAt = ParseDate(CCsvParser.Get(row, "OCCURRED_AT"));
        if (!occurredAt.HasValue)
        {
            return null;
        }

        return new ST_PRODUCT_HISTORY(
            occurredAt.Value, 
            CCsvParser.Get(row, "GLASS_ID"), 
            CCsvParser.Get(row, "PROCESS_ID"), 
            CCsvParser.Get(row, "RECIPE_ID"), 
            CCsvParser.Get(row, "ACTION"), 
            CCsvParser.Get(row, "STATE"), 
            CCsvParser.Get(row, "RESULT"), 
            CCsvParser.Get(row, "DETAIL")
        );
    }

    private static void WriteRowsAtomically(string path, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            CCsvParser.Write(temporaryPath, headers, rows);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Replace(temporaryPath, path, null);
            }
            else
            {
                System.IO.File.Move(temporaryPath, path);
            }
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static T ParseEnum<T>(string value, T defaultValue)
        where T : struct, Enum
    {
        return Enum.TryParse<T>(value, true, out var result) ? result : defaultValue;
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.ToString("O", CultureInfo.InvariantCulture) ?? "";
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : null;
    }

    private static void DeleteIfExists(string path)
    {
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }
}
