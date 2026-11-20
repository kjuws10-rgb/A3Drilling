using System.IO;
using System.Security.Cryptography;
using System.Text;
using Drilling.Common.Review.ManualInspection;
using Drilling.Common.Review;

// Source-derived production seam only. No equipment manager/driver/SDK class is constructed.
namespace Drilling.UI.Menu.Menus
{
    public interface IManualInspectionBackend
    {
        bool IsSimulation { get; }
        string StateDirectory { get; }
        IReadOnlyList<string> WatchRoots => [];
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> LastSavedValues => new Dictionary<string, ST_MANUAL_OFFSET>();
        IReadOnlyList<KeyValuePair<string, string>> ConsumedMeasurements => [];
        Task<CManualInspectionSource> LoadAsync(string? resultPath, CancellationToken token, IReadOnlyList<string>? displayKeys = null);
        Task EnsureCurrentAsync(CManualInspectionSource source, CancellationToken token);
        void GuardOperation();
        Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource source, string key,
            EN_MANUAL_OPERATION_KIND kind, Action<string> progress, CancellationToken token);
        Task ExecuteFlyingAsync(CManualInspectionSource source, IReadOnlyList<string> keys,
            Action<CManualInspectionMeasurement> measured, Action<string> progress, CancellationToken token);
        Task<string> SaveAsync(CManualInspectionSource source, IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values,
            string sourceIdentity, IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> measuredRows, CancellationToken token,
            IReadOnlyDictionary<string, string>? consumedMeasurements = null);
        void Stop();
    }
    public sealed record CManualInspectionSource(ST_MANUAL_CONTEXT Context, IReadOnlyList<ST_MANUAL_POINT> Points,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> Values, ST_MANUAL_LIMITS Limits,
        ManualInspectionFieldEvaluator FieldEvaluator, double ToleranceXUm, double ToleranceYUm,
        string SourcePath, string Description)
    {
        public bool ReadOnly { get; init; }
        public IReadOnlySet<string> AppliedOffsetKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string,CManualInspectionImportedResult> ImportedResults { get; init; } = new Dictionary<string,CManualInspectionImportedResult>(StringComparer.Ordinal);
        public IReadOnlyList<ST_REVIEW_RESULT_CATALOG_ITEM> RecentResults { get; init; } = [];
        public string ObservationFingerprint { get; init; } = "";
    }
    public sealed record CManualInspectionImportedResult(string Path, DateTimeOffset SavedAt, string Origin);
    public sealed record CManualInspectionMeasurement(string Key, ST_MANUAL_MEASUREMENT Measurement, double ReviewXmm, double StageYmm);
    public static class CManualInspectionBackend
    {
        public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        public static void AtomicText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { File.WriteAllText(tmp,text,new UTF8Encoding(false)); File.Move(tmp,path,true); }
            finally { if(File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
namespace Drilling.Common.Review
{
    public sealed record ST_REVIEW_RESULT_CATALOG_ITEM(string FilePath,string FileName,string Source,string ResultType,
        string RecipeId,DateTimeOffset SavedAt,string ResultStatus,string GlassId,string RunType,string Method,int? TotalCount,string ResultMessage);
}
namespace Drilling.UI.Popup
{
    public static class CRecipeConfirmDialog
    { public static void ShowWarning(string title,string text) => throw new InvalidOperationException("Unexpected real UI popup in isolated test: "+title); }
}
