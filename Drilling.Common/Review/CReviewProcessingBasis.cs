using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Drilling.Common.Recipe;
using Drilling.Common.Review.ManualInspection;
using Drilling.Common.Station;

namespace Drilling.Common.Review;

/// <summary>Only completed machining proves the Review contribution used for the physical marks.
/// This record contains no settings dictionary, credentials or inferred current-offset baseline.</summary>
public sealed record ST_REVIEW_PROCESSING_OFFSET(int CellNo,int ShotNo,int HeadNo,ST_MANUAL_OFFSET BaseOffsetUm,
    ST_MANUAL_OFFSET AppliedDesignUm);
public sealed record ST_REVIEW_PROCESSING_BASIS(string BasisId,string ProcessId,string RecipeId,string GlassId,
    DateTimeOffset CompletedAt,string CoordinateSignature,string GeometrySha256,string ReviewCsvPath,
    string ReviewCsvSha256,DateTimeOffset ReviewCsvSavedAt,double ReviewOffsetScale,
    IReadOnlyList<ST_REVIEW_PROCESSING_OFFSET> Offsets,bool IsSimulation,string ExecutionKind)
{
    public string ProofSha256 { get; init; }="";
    public bool IsExecutionModeKnown {get;init;}=true;
    public double ShiftOffsetXmm {get;init;}
    public double ShiftOffsetYmm {get;init;}
}
public sealed record ST_REVIEW_PROCESSING_STATE(string Status,string BasisId,string ProcessId,string RecipeId,string GlassId,
    DateTimeOffset ChangedAt,ST_REVIEW_PROCESSING_BASIS? CompletedBasis)
{ public string PreparedProofSha256 {get;init;}=""; }

public static class CReviewProcessingBasis
{
    /// <summary>Exact .8 static signature algorithm. Keep old sessions comparable. Review keys,
    /// shift (appended separately), process counters and credential fields are not serialised.</summary>
    public static string StaticSignature(IReadOnlyDictionary<string,string> values) => Hash(string.Join("\n",
        values.Where(p=>!CReviewOffsetData.IsParameterKey(p.Key)&&p.Key is not ("CURRENT_PROCESS_COUNT" or "SHIFT_OFFSET_X" or "SHIFT_OFFSET_Y")
            &&!p.Key.Contains("PASSWORD",StringComparison.OrdinalIgnoreCase)&&!p.Key.Contains("SECRET",StringComparison.OrdinalIgnoreCase)
            &&!p.Key.Contains("TOKEN",StringComparison.OrdinalIgnoreCase))
        .OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+"="+p.Value)));
    public static string CoordinateSignature(IReadOnlyDictionary<string,string> parameters,double shiftX,double shiftY)
    {
        // Root attaches Product identities only to execution requests. They are checked explicitly
        // on the completed basis; they must not make the same static recipe fail its frame match.
        var frame=parameters.Where(p=>!p.Key.Equals("GlassId",StringComparison.OrdinalIgnoreCase)&&!p.Key.Equals("LotId",StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        frame.TryAdd("DIAMETER","0.350");frame.TryAdd("HEAD_COUNT","8");
        return StaticSignature(frame)+FormattableString.Invariant($"|SHIFT:{shiftX:R},{shiftY:R}");
    }
    public static string CoordinateSignature(IReadOnlyDictionary<string,string> parameters)
        => CoordinateSignature(parameters,Number(parameters,"SHIFT_OFFSET_X",0),Number(parameters,"SHIFT_OFFSET_Y",0));

    /// <summary>Called with the frozen model that is actually sent to A1. Head selection is
    /// represented by the explicit consumed Shot/Head list, not by changing the static signature.</summary>
    public static ST_REVIEW_PROCESSING_BASIS Capture(ST_PROCESS_MODEL model,string staticCoordinateSignature,
        string csvPath,string csvSha,DateTimeOffset csvSavedAt,bool simulation,string kind,string? basisId=null)
    {
        var scale=Number(model.Parameters,"REVIEW_OFFSET_SCALE",100)/100;
        if(!double.IsFinite(scale)||scale<0)throw new InvalidDataException("Review Scale 기준을 기록할 수 없습니다.");
        var masking=CRecipeShotMaskingPlan.Build(model.Parameters);
        var shots=model.HeadPlans.SelectMany(h=>h.Shots).Where(s=>!masking.EvaluateShot(s).ShouldSkipLaser).ToArray();
        var values=new List<ST_REVIEW_PROCESSING_OFFSET>();
        foreach(var shot in shots)
        {
            var design=CScannerAxisTransform.ScannerOffsetToDesign(model.Parameters,shot.HeadNo,shot.ReviewOffsetX,shot.ReviewOffsetY);
            var applied=new ST_MANUAL_OFFSET(design.X*1000,design.Y*1000);
            // Capture the actual frozen command contribution; do not assume CSV/Align precision.
            var baseline=scale>0?new ST_MANUAL_OFFSET(applied.XUm/scale,applied.YUm/scale):ST_MANUAL_OFFSET.Zero;
            values.Add(new(shot.CellNo,shot.CellShotNo,shot.HeadNo,baseline,applied));
        }
        if(values.Count==0||values.GroupBy(v=>(v.CellNo,v.ShotNo)).Any(g=>g.Count()!=1))
            throw new InvalidDataException("실제가공 Shot 기준이 없거나 중복됐습니다.");
        var geometry=Hash(JsonSerializer.Serialize(shots.OrderBy(s=>s.CellNo).ThenBy(s=>s.CellShotNo)
            .Select(s=>new{s.CellNo,s.CellShotNo,s.HeadNo,s.DesignX,s.DesignY,s.ScannerGx,s.ScannerGy,s.StageX,s.StageY})));
        var basis=new ST_REVIEW_PROCESSING_BASIS(basisId??Guid.NewGuid().ToString("N"),model.ProcessId,model.RecipeId,model.GlassId,
            default,staticCoordinateSignature,geometry,csvPath,csvSha,csvSavedAt,scale,Array.AsReadOnly(values.ToArray()),simulation,kind);
        basis=basis with{ShiftOffsetXmm=Number(model.Parameters,"SHIFT_OFFSET_X",0),ShiftOffsetYmm=Number(model.Parameters,"SHIFT_OFFSET_Y",0)};
        return basis with{ProofSha256=Proof(basis)};
    }
    public static ST_REVIEW_PROCESSING_BASIS Complete(ST_REVIEW_PROCESSING_BASIS basis,DateTimeOffset at)
    { Validate(basis,false);if(!basis.IsExecutionModeKnown)throw new InvalidDataException("실사용 가공 경로의 모드가 확인되지 않았습니다.");if(at==default)throw new InvalidDataException("가공 완료 시각이 없습니다.");var completed=basis with{CompletedAt=at};return completed with{ProofSha256=Proof(completed)}; }
    public static ST_REVIEW_PROCESSING_BASIS Freeze(ST_REVIEW_PROCESSING_BASIS basis)
    { Validate(basis,basis.CompletedAt!=default);return basis with{Offsets=Array.AsReadOnly(basis.Offsets.Select(v=>v with{}).ToArray())}; }
    public static void Validate(ST_REVIEW_PROCESSING_BASIS basis,bool completed=true)
    {
        if(basis is null||string.IsNullOrWhiteSpace(basis.BasisId)||string.IsNullOrWhiteSpace(basis.ProcessId)
            ||string.IsNullOrWhiteSpace(basis.RecipeId)||completed&&string.IsNullOrWhiteSpace(basis.GlassId)||string.IsNullOrWhiteSpace(basis.CoordinateSignature)
            ||!IsHash(basis.GeometrySha256)||!IsHash(basis.ReviewCsvSha256)||basis.ReviewCsvPath is null||string.IsNullOrWhiteSpace(basis.ExecutionKind)
            ||basis.ReviewCsvPath.Length>0&&basis.ReviewCsvSha256.Length!=64
            ||completed&&(basis.CompletedAt==default||!basis.IsExecutionModeKnown)||!double.IsFinite(basis.ReviewOffsetScale)||basis.ReviewOffsetScale<0
            ||!double.IsFinite(basis.ShiftOffsetXmm)||!double.IsFinite(basis.ShiftOffsetYmm)
            ||basis.Offsets is null||basis.Offsets.Count==0||basis.Offsets.Any(v=>v is null||v.CellNo<=0||v.ShotNo<=0||v.HeadNo is <1 or >8
                ||!v.BaseOffsetUm.IsFinite||!v.AppliedDesignUm.IsFinite)
            ||basis.Offsets.GroupBy(v=>(v.CellNo,v.ShotNo)).Any(g=>g.Count()!=1)||basis.ProofSha256!=Proof(basis))
            throw new InvalidDataException("가공 기준 기록의 출처·완료·수치 검증에 실패했습니다.");
    }
    /// <summary>Only routes used by this machining run contribute to its mode. Unknown routes
    /// cannot prove a real process; unused simulated devices/heads do not contaminate it.</summary>
    public static bool IsSimulationOrUnknown(IReadOnlyCollection<int> usedHeads,IReadOnlyDictionary<int,bool?> headModes,
        IReadOnlyCollection<int> usedStageDevices,IReadOnlyDictionary<int,bool?> stageModes)
        =>GetExecutionSimulationMode(usedHeads,headModes,usedStageDevices,stageModes)!=false;
    public static bool? GetExecutionSimulationMode(IReadOnlyCollection<int> usedHeads,IReadOnlyDictionary<int,bool?> headModes,
        IReadOnlyCollection<int> usedStageDevices,IReadOnlyDictionary<int,bool?> stageModes)
    {
        if(usedHeads.Count==0||usedStageDevices.Count==0||usedHeads.Any(id=>!headModes.TryGetValue(id,out var mode)||!mode.HasValue)
            ||usedStageDevices.Any(id=>!stageModes.TryGetValue(id,out var mode)||!mode.HasValue))return null;
        return usedHeads.Any(id=>headModes[id]==true)||usedStageDevices.Any(id=>stageModes[id]==true);
    }

    public static string Proof(ST_REVIEW_PROCESSING_BASIS b)=>Hash(JsonSerializer.Serialize(new{b.BasisId,b.ProcessId,b.RecipeId,b.GlassId,b.CompletedAt,
        b.CoordinateSignature,b.GeometrySha256,b.ReviewCsvPath,b.ReviewCsvSha256,b.ReviewCsvSavedAt,b.ReviewOffsetScale,
        Offsets=b.Offsets?.OrderBy(v=>v.CellNo).ThenBy(v=>v.ShotNo),b.IsSimulation,b.IsExecutionModeKnown,b.ExecutionKind,b.ShiftOffsetXmm,b.ShiftOffsetYmm}));
    private static bool IsHash(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
    public static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static double Number(IReadOnlyDictionary<string,string> p,string key,double fallback)
        =>p.TryGetValue(key,out var raw)&&double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:fallback;
}

/// <summary>Atomic restartable lifecycle. A new RUN persists PENDING before any equipment command;
/// incomplete/invalid records never fall back to a previous completed basis.</summary>
public sealed class CReviewProcessingBasisStore(string path)
{
    public string FilePath {get;}=Path.GetFullPath(path);
    public string ReadIdentity()
    {
        if(!File.Exists(FilePath))return "";
        var state=JsonSerializer.Deserialize<ST_REVIEW_PROCESSING_STATE>(File.ReadAllText(FilePath))??throw new InvalidDataException("가공 기준 상태가 없습니다.");
        if(state.Status is not ("PENDING" or "COMPLETED")||string.IsNullOrWhiteSpace(state.BasisId))throw new InvalidDataException("가공 기준 상태가 잘못됐습니다.");
        return state.Status+"|"+state.BasisId;
    }
    public ST_REVIEW_PROCESSING_BASIS? ReadCompleted()
    {
        if(!File.Exists(FilePath))return null;
        var state=JsonSerializer.Deserialize<ST_REVIEW_PROCESSING_STATE>(File.ReadAllText(FilePath))??throw new InvalidDataException("가공 기준 상태가 없습니다.");
        if(state.Status!="COMPLETED")return null;
        var b=state.CompletedBasis??throw new InvalidDataException("가공 완료 기준이 없습니다.");CReviewProcessingBasis.Validate(b);
        if(state.BasisId!=b.BasisId||state.ProcessId!=b.ProcessId||state.RecipeId!=b.RecipeId||state.GlassId!=b.GlassId)
            throw new InvalidDataException("가공 기준 상태와 완료 기록이 다릅니다.");
        return CReviewProcessingBasis.Freeze(b);
    }
    public void Pending(ST_REVIEW_PROCESSING_BASIS b)
    {CReviewProcessingBasis.Validate(b,false);if(b.CompletedAt!=default)throw new InvalidDataException("완료 기록을 새 RUN 기준으로 재사용할 수 없습니다.");
        Write(new("PENDING",b.BasisId,b.ProcessId,b.RecipeId,b.GlassId,DateTimeOffset.UtcNow,null){PreparedProofSha256=b.ProofSha256});}
    public ST_REVIEW_PROCESSING_BASIS Completed(ST_REVIEW_PROCESSING_BASIS b,DateTimeOffset at)
    {
        var current=File.Exists(FilePath)?JsonSerializer.Deserialize<ST_REVIEW_PROCESSING_STATE>(File.ReadAllText(FilePath)):null;
        if(current?.Status!="PENDING"||current.BasisId!=b.BasisId||current.ProcessId!=b.ProcessId||current.RecipeId!=b.RecipeId
            ||current.GlassId!=b.GlassId||current.PreparedProofSha256!=b.ProofSha256||at<current.ChangedAt)throw new InvalidDataException("현재 진행 중인 가공과 완료 기준이 다릅니다.");
        var result=CReviewProcessingBasis.Complete(b,at);Write(new("COMPLETED",result.BasisId,result.ProcessId,result.RecipeId,result.GlassId,at,result));return result;
    }
    private void Write(ST_REVIEW_PROCESSING_STATE state)
    {
        var directory=Path.GetDirectoryName(FilePath)!;Directory.CreateDirectory(directory);var temp=Path.Combine(directory,"."+Path.GetFileName(FilePath)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try{using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state));stream.Write(bytes);stream.Flush(true);}File.Move(temp,FilePath,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}

/// <summary>Shared native save exception boundary. An unreadable receipt is unknown, never proof
/// that a CSV did not commit; retain its reservations for explicit recovery.</summary>
public static class CReviewSaveReservationTransaction
{
    public static async Task<T> Run<T>(IReadOnlyList<string> reservations,Func<Task<T>> work,Func<Task<bool>> committed)
    {
        try{return await work().ConfigureAwait(false);}
        catch
        {
            bool? saved=null;try{saved=await committed().ConfigureAwait(false);}catch{ }
            if(saved==false)
                foreach(var path in reservations)
                    try{if(File.Exists(path))File.Delete(path);}catch(Exception error)when(error is IOException or UnauthorizedAccessException)
                    {System.Diagnostics.Trace.TraceError("Reservation cleanup retained: "+error.Message);}
            throw;
        }
    }
}
