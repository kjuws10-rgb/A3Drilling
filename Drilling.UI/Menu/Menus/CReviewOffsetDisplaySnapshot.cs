namespace Drilling.UI.Menu.Menus;

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Drilling.Common.Recipe;
using Drilling.Common.Review;

/// <summary>
/// Immutable, Recipe-scoped saved Review data for a coordinate preview. Applying it always creates
/// a temporary map; editor rows and Recipe save input never become a second Review offset store.
/// </summary>
public sealed class CReviewOffsetDisplaySnapshot
{
    private readonly ST_REVIEW_OFFSET_FILE_DATA _file;
    private CReviewOffsetDisplaySnapshot(string recipeId, string revision, ST_REVIEW_OFFSET_FILE_DATA file)
    {
        RecipeId = recipeId;
        RevisionIdentity = revision;
        _file = file;
    }
    public string RecipeId { get; }
    public string RevisionIdentity { get; }

    public static string GetRevisionIdentity(string recipeId, ST_REVIEW_OFFSET_FILE_DATA file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentNullException.ThrowIfNull(file);
        if (!recipeId.Trim().Equals(file.RecipeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("표시할 Recipe와 저장된 Review Offset의 모델이 다릅니다.");
        var content = string.IsNullOrWhiteSpace(file.ContentSha256)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file.Rows))))
            : file.ContentSha256;
        return recipeId.Trim().ToUpperInvariant() + "|" + file.FilePath + "|" + file.FileName + "|" + file.SavedAt.ToString("O") + "|" + content;
    }

    public static CReviewOffsetDisplaySnapshot Create(string recipeId, ST_REVIEW_OFFSET_FILE_DATA file)
    {
        var revision = GetRevisionIdentity(recipeId, file);
        var rows = CReviewOffsetData.ValidateRows(file.Rows, string.IsNullOrEmpty(file.FilePath) ? file.FileName : file.FilePath);
        return new(recipeId.Trim(), revision, file with { Rows = Array.AsReadOnly(rows) });
    }

    public Dictionary<string, string> ApplyToCopy(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var copy = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        CReviewOffsetData.ApplyToParameters(copy, _file);
        return copy;
    }
}
