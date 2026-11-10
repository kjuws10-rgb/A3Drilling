using System.Globalization;
using Drilling.Common.Correction;
using Drilling.Common.Recipe;
using Drilling.File.Parser;

namespace Drilling.File.Correction;

public sealed class CDefaultOffsetLogFile(string configRoot)
{
    public Task<string> Save(ST_DEFAULT_OFFSET_LOG data, CancellationToken token = default) => Task.Run(() =>
    {
        var safeName = string.Concat(data.Recipe.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safeName is "" or "." or "..") throw new IOException("Default 결과의 레시피명을 확인해 주세요.");
        var directory = Path.Combine(Directory.GetParent(Path.GetFullPath(configRoot))!.FullName, "Data", "DefaultOffset", safeName);
        var path = Path.Combine(directory, $"{data.Time:yyMMdd_HHmmss_fff}_{data.Action}_{Guid.NewGuid():N}.csv");
        var temporary = path + ".tmp";
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        try
        {
            string[] headers = ["SESSION", "RECIPE", "ACTION", "TIME", "MODE", "HEAD", "TEST_ID", "Q", "R", "AUTOMATION_SIMULATION", "STAGE_SIMULATION", "VISION_SIMULATION", "BEAM", "MARK_X", "MARK_Y", "RECV_TARGET_HOLE_X", "RECV_TARGET_HOLE_Y", "VISION_FLIP_X", "VISION_FLIP_Y", "ENCODER_X", "ENCODER_Y", "CAPTURE_STARTED_AT", "JUDGE", "CENTER_ERROR_X", "CENTER_ERROR_Y", "RESIDUAL", "PITCH_X", "PITCH_Y", "BEFORE_X", "BEFORE_Y", "AFTER_X", "AFTER_Y", "BOARD_CENTER_X", "TEST_STAGE_Y", "TOLERANCE", "FIELD_HALF_X", "FIELD_HALF_Y", "DOE_ANGLE", "MODEL", "VISION_REFERENCE_CELL", "TARGET_REVIEW_X", "TARGET_STAGE_Y", "CENTER_REVIEW_X", "CENTER_STAGE_Y", "FIT_CENTER_X", "FIT_CENTER_Y", "MACHINING_MODE", "CROSS_FULL_SIZE_MM", "VISION_METHOD"];
            CCsvParser.Write(temporary, headers, Rows().Select(row => (IReadOnlyDictionary<string, string>)headers.Zip(row).ToDictionary(p => p.First, p => p.Second)));
            token.ThrowIfCancellationRequested();
            System.IO.File.Move(temporary, path);
            return path;
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
        IEnumerable<IReadOnlyList<string>> Rows()
        {
            foreach (var result in data.Results.OrderBy(r => r.Head))
            foreach (var mark in result.Marks)
            {
                token.ThrowIfCancellationRequested();
                var prefix = $"H{result.Head:00}_DEFAULT_OFFSET_";
                yield return new[] { data.SessionId.ToString(), data.Recipe, data.Action, data.Time.ToString("O"), "SETUP_CENTER", result.Head.ToString(), result.TestId.ToString(), F(result.Q), F(result.R), result.AutomationSimulation.ToString(), result.StageSimulation.ToString(), result.VisionSimulation.ToString(), mark.Beam.ToString(), F(mark.X), F(mark.Y), F(mark.RawX), F(mark.RawY), F(mark.CorrectionX), F(mark.CorrectionY), F(mark.EncoderX), F(mark.EncoderY), mark.CapturedAt.ToString("O"), mark.Judge, F(result.CenterErrorX), F(result.CenterErrorY), F(result.Fit.Residual), F(result.Fit.PitchX), F(result.Fit.PitchY), data.Before.GetValueOrDefault(prefix + "X", ""), data.Before.GetValueOrDefault(prefix + "Y", ""), data.After.GetValueOrDefault(prefix + "X", ""), data.After.GetValueOrDefault(prefix + "Y", ""), F(data.Options.BoardCenterX), F(data.Options.TestStageY), F(data.Options.Tolerance), result.Geometry is { } fieldX ? F(fieldX.FieldHalfX) : "", result.Geometry is { } fieldY ? F(fieldY.FieldHalfY) : "", result.Geometry is { } doe ? F(doe.DoeAngle) : "", result.Model.ToString(CultureInfo.InvariantCulture), result.ReferenceCell.ToString(CultureInfo.InvariantCulture), F(result.TargetReviewX), F(result.TargetStageY), F(result.CenterReviewX), F(result.CenterStageY), F(result.Fit.X), F(result.Fit.Y), data.Options.MachiningMode.ToString(), F(data.Options.CrossSize), data.Options.VisionMethod.ToString() };
            }
        }
    }, token);
}

public sealed class CAlignOffsetLogFile(string configRoot) : CAlignOffsetLogFileBase
{
    private static readonly string[] Headers =
    [

        "RECIPE_NAME", 
        "ACTION", 
        "ALGORITHM", 
        "APPLIED_AT", 
        "LAST_LOAD_TIME", 
        "SOURCE", 
        "ROW_TYPE", 
        "KEY_NAME", 
        "INCLUDED",

        "CELL_NO", 
        "SHOT_NAME", 
        "DESIGN_X_MM", 
        "DESIGN_Y_MM", 
        "BASE_X_MM", 
        "BASE_Y_MM", 
        "LAST_LOAD_X_MM", 
        "LAST_LOAD_Y_MM",

        "BASE_LOAD_X_MM", 
        "BASE_LOAD_Y_MM", 
        "OFFSET_X_MM", 
        "OFFSET_Y_MM", 
        "TOLERANCE_MM", 
        "APPLY_STATUS",

        "CALCULATED_OFFSET_X_MM", 
        "CALCULATED_OFFSET_Y_MM"

    ];

    public string RootPath { get; } = Path.Combine(Directory.GetParent(Path.GetFullPath(configRoot))!.FullName, "Data", "AlignOffset");

    public override Task<string> Save(ST_ALIGN_OFFSET_LOG entry, CancellationToken cancellationToken = default)
        => Task.Run(() => SaveCore(entry, cancellationToken), cancellationToken);

    private string SaveCore(ST_ALIGN_OFFSET_LOG entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(RootPath, SafeRecipeName(entry.RecipeName));
        var name = entry.ChangedAt.ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, name + ".csv");
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        var calculated = (entry.CalculatedOffsets.Count > 0 ? entry.CalculatedOffsets : entry.Shots)
            .ToDictionary(shot => (shot.CellNo, shot.ShotName));
        var excluded = entry.ExcludedOffsets.Select(shot => (shot.CellNo, shot.ShotName)).ToHashSet();
        try
        {
            CCsvParser.Write(temporaryPath, Headers, Rows());
            cancellationToken.ThrowIfCancellationRequested();
            for (var duplicate = 1; ; duplicate++)
            {
                try { System.IO.File.Move(temporaryPath, path); return path; }
                catch (IOException) when (System.IO.File.Exists(path))
                {
                    // Keep every operation when two saves occur within the same second.
                    path = Path.Combine(directory, $"{name}_{duplicate}.csv");
                }
            }
        }
        finally
        {
            if (System.IO.File.Exists(temporaryPath)) System.IO.File.Delete(temporaryPath);
        }

        IEnumerable<IReadOnlyDictionary<string, string>> Rows()
        {
            foreach (var key in entry.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = CommonRow("KEY", key.DesignX, key.DesignY, key.OffsetX, key.OffsetY);
                row["KEY_NAME"] = key.Name;
                row["INCLUDED"] = key.Included ? "1" : "0";
                row["BASE_X_MM"] = Number(key.BaseX);
                row["BASE_Y_MM"] = Number(key.BaseY);
                row["LAST_LOAD_X_MM"] = Number(key.LastLoadX);
                row["LAST_LOAD_Y_MM"] = Number(key.LastLoadY);
                row["BASE_LOAD_X_MM"] = Number(key.BaseX - key.LastLoadX);
                row["BASE_LOAD_Y_MM"] = Number(key.BaseY - key.LastLoadY);
                yield return row;
            }
            foreach (var shot in entry.Shots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = CommonRow("SHOT", shot.DesignX, shot.DesignY, shot.X, shot.Y);
                row["CELL_NO"] = shot.CellNo.ToString(CultureInfo.InvariantCulture);
                row["SHOT_NAME"] = shot.ShotName;
                row["APPLY_STATUS"] = entry.IsInit ? "INIT" : excluded.Contains((shot.CellNo, shot.ShotName)) ? "EXCLUDED_KEEP" : "APPLIED";
                if (calculated.TryGetValue((shot.CellNo, shot.ShotName), out var result))
                {
                    row["CALCULATED_OFFSET_X_MM"] = Number(result.X);
                    row["CALCULATED_OFFSET_Y_MM"] = Number(result.Y);
                }
                yield return row;
            }
        }

        Dictionary<string, string> CommonRow(string type, double x, double y, double? offsetX, double? offsetY) => new()
        {
            ["RECIPE_NAME"] = entry.RecipeName,
            ["ACTION"] = entry.IsInit ? "Init" : "Apply",
            ["ALGORITHM"] = entry.IsInit ? "" : CAlignInterpolation.AlgorithmName,
            ["APPLIED_AT"] = entry.ChangedAt.ToString("O", CultureInfo.InvariantCulture),
            ["LAST_LOAD_TIME"] = entry.LastLoadedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
            ["SOURCE"] = entry.LastLoadedAt is null ? "NOT_LOADED" : entry.IsSimulation ? "SIMULATION" : "MELSEC",
            ["TOLERANCE_MM"] = Number(entry.Tolerance),
            ["ROW_TYPE"] = type,
            ["DESIGN_X_MM"] = Number(x), ["DESIGN_Y_MM"] = Number(y),
            // These are the persisted design-coordinate offsets, without head-direction conversion.
            ["OFFSET_X_MM"] = Number(offsetX), ["OFFSET_Y_MM"] = Number(offsetY)
        };
    }

    private static string Number(double? value) => value is { } number ? CAlignShotOffsetData.FormatValue(number) : "";

    private static string SafeRecipeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Align Offset 로그의 레시피명이 없습니다.");
        if (result.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            result = "_" + result;
        return result;
    }
}
