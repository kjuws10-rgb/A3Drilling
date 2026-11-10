namespace Drilling.File.ReviewResult
{
    using System.Globalization;
    using System.Text;
    using Drilling.Common.Review;
    using Microsoft.VisualBasic.FileIO;

    public sealed partial class CReviewResultFile
    {
        private static readonly string[] ManualResultTypes = ["ALL_SHOT", "ONE_SHOT", "BEAM_INSPECTION", "ROUGH", "FINE"];

        public override Task<IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM>> ListCorrectionResults(CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM>>(
                () => ReadResultCatalog(cancellationToken),
                cancellationToken);
        }

        private IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM> ReadResultCatalog(CancellationToken cancellationToken)
        {
            var results = new List<ST_REVIEW_RESULT_CATALOG_ITEM>();
            ReadManualCatalog(results, cancellationToken);
            ReadAutoCatalog(results, cancellationToken);
            ReadZeroDefenceCatalog(results, cancellationToken);
            return results
                .OrderByDescending(item => item.SavedAt)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void ReadManualCatalog(List<ST_REVIEW_RESULT_CATALOG_ITEM> results, CancellationToken cancellationToken)
        {
            var root = Path.Combine(_reviewRoot, "Manual");
            if (!Directory.Exists(root))
                return;
            foreach (var recipeDirectory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recipeId = Path.GetFileName(recipeDirectory);
                foreach (var resultType in ManualResultTypes)
                {
                    var typeDirectory = Path.Combine(recipeDirectory, resultType);
                    if (Directory.Exists(typeDirectory))
                        AddFiles(results, typeDirectory, "MANUAL", resultType, recipeId, "", cancellationToken);
                }
            }
        }

        private void ReadAutoCatalog(List<ST_REVIEW_RESULT_CATALOG_ITEM> results, CancellationToken cancellationToken)
        {
            var root = Path.Combine(_reviewRoot, "Auto");
            if (!Directory.Exists(root))
                return;
            foreach (var recipeDirectory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddFiles(results, recipeDirectory, "AUTO", "AUTO", Path.GetFileName(recipeDirectory), "", cancellationToken);
            }
        }

        private void ReadZeroDefenceCatalog(List<ST_REVIEW_RESULT_CATALOG_ITEM> results, CancellationToken cancellationToken)
        {
            var root = Path.Combine(_reviewRoot, "ZeroDefence");
            if (!Directory.Exists(root))
                return;
            foreach (var recipeDirectory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recipeId = Path.GetFileName(recipeDirectory);
                foreach (var glassDirectory in Directory.EnumerateDirectories(recipeDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var glassId = Path.GetFileName(glassDirectory);
                    foreach (var filePath in Directory.EnumerateFiles(glassDirectory, "*.csv", System.IO.SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var resultType = Path.GetFileNameWithoutExtension(filePath).Contains("FOLLOWUP", StringComparison.OrdinalIgnoreCase)
                            ? "FOLLOWUP"
                            : "ZERO_DEFENCE";
                        results.Add(ReadCatalogItem(filePath, "ZERO_DEFENCE", resultType, recipeId, glassId));
                    }
                }
            }
        }

        private static void AddFiles(
            List<ST_REVIEW_RESULT_CATALOG_ITEM> results,
            string directory,
            string source,
            string resultType,
            string recipeId,
            string glassId,
            CancellationToken cancellationToken)
        {
            foreach (var filePath in Directory.EnumerateFiles(directory, "*.csv", System.IO.SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(ReadCatalogItem(filePath, source, resultType, recipeId, glassId));
            }
        }

        private static ST_REVIEW_RESULT_CATALOG_ITEM ReadCatalogItem(
            string filePath,
            string source,
            string resultType,
            string fallbackRecipeId,
            string fallbackGlassId)
        {
            var fullPath = Path.GetFullPath(filePath);
            var fileName = Path.GetFileName(fullPath);
            var fallbackSavedAt = new DateTimeOffset(System.IO.File.GetLastWriteTime(fullPath));
            try
            {
                using var parser = new TextFieldParser(fullPath, Encoding.UTF8, true)
                {
                    TextFieldType = FieldType.Delimited,
                    HasFieldsEnclosedInQuotes = true,
                    TrimWhiteSpace = false
                };
                parser.SetDelimiters(",");
                var headers = parser.ReadFields() ?? [];
                var values = parser.EndOfData ? [] : parser.ReadFields() ?? [];
                var firstRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < headers.Length; index++)
                {
                    var name = headers[index].Trim().TrimStart('\uFEFF');
                    if (name.Length > 0)
                        firstRow[name] = index < values.Length ? values[index].Trim() : "";
                }

                var savedAt = ParseSavedAt(Value(firstRow, "SAVED_AT", "SavedAt"), fallbackSavedAt);
                int? totalCount = int.TryParse(Value(firstRow, "TOTAL_COUNT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                    ? count
                    : null;
                return new ST_REVIEW_RESULT_CATALOG_ITEM(
                    fullPath,
                    fileName,
                    source,
                    resultType,
                    ValueOrFallback(firstRow, fallbackRecipeId, "RECIPE_ID", "Recipe"),
                    savedAt,
                    NormalizeCatalogStatus(Value(firstRow, "RESULT_STATUS", "ResultStatus", "STATE", "State")),
                    ValueOrFallback(firstRow, string.IsNullOrWhiteSpace(fallbackGlassId) ? "UNLINKED" : fallbackGlassId, "GLASS_ID", "GLASS_NAME", "Glass"),
                    ValueOrFallback(firstRow, source, "RUN_TYPE"),
                    ValueOrFallback(firstRow, "-", "METHOD", "INSPECTION_METHOD"),
                    totalCount,
                    Value(firstRow, "RESULT_MESSAGE", "DETAIL", "Detail"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MalformedLineException or InvalidDataException)
            {
                return new ST_REVIEW_RESULT_CATALOG_ITEM(
                    fullPath,
                    fileName,
                    source,
                    resultType,
                    fallbackRecipeId,
                    fallbackSavedAt,
                    "INVALID",
                    string.IsNullOrWhiteSpace(fallbackGlassId) ? "UNLINKED" : fallbackGlassId,
                    source,
                    "-",
                    null,
                    exception.Message);
            }
        }

        private static string Value(IReadOnlyDictionary<string, string> values, params string[] names) => ValueOrFallback(values, "", names);

        private static string ValueOrFallback(IReadOnlyDictionary<string, string> values, string fallback, params string[] names)
        {
            foreach (var name in names)
            {
                if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return fallback;
        }

        private static DateTimeOffset ParseSavedAt(string value, DateTimeOffset fallback)
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var savedAt)
                ? savedAt
                : fallback;
        }

        private static string NormalizeCatalogStatus(string value)
        {
            var status = value.Trim().ToUpperInvariant();
            return status.Length == 0 ? "UNKNOWN" : status;
        }
    }
}
