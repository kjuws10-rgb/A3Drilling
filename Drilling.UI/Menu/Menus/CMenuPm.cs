using System.Globalization;
using System.IO;
using Drilling.Common.Interface;
using Drilling.Common.Log;
using Drilling.Common.Managers;
using Drilling.Common.Recipe;

namespace Drilling.UI.Menu.Menus;
public sealed class CMenuPm(
    CInterfaceManager interfaceManager,
    CRecipeManager recipeManager,
    CSettingManager settingManager,
    CLogManager logManager,
    Func<string> selectedRecipeIdProvider,
    Func<ST_PM_LOCK_STATUS> lockStatusProvider) : CMenuBase
{
    private const string LaserThgHourLimitKey = "LASER_THG_HOUR_LIMIT";

    public override EN_MENU Menu
    {
        get
        {
            return EN_MENU.Pm;
        }
    }

    public bool IsLocked { get; private set; }
    public string StartTime { get; private set; } = "";
    public IReadOnlyList<ST_DISPLAY_ITEM> LockItems { get; private set; } = [];
    public IReadOnlyList<ST_DISPLAY_ITEM> BlockedItems { get; private set; } = [];
    public IReadOnlyList<ST_PM_PART_LIFE_ROW> PartLifeRows { get; private set; } = CreateEmptyPartLifeRows();
    public IReadOnlyList<ST_DISPLAY_ITEM> ProtectWindowItems { get; private set; } = CreateEmptyProtectWindowItems();
    public IReadOnlyList<ST_PM_HISTORY_ROW> HistoryRows { get; private set; } = [];

    public override async Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = lockStatusProvider();
        var partLifeRows = CreatePartLifeRows(cancellationToken);
        var protectWindowItems = await CreateProtectWindowItems(cancellationToken).ConfigureAwait(false);
        var historyRows = CreateHistoryRows(logManager.ReadPmHistory());
        var startTime = current.LockedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Apply(
            current.IsLocked,
            startTime,
            [new("Lock State", current.IsLocked ? "LOCKED" : "UNLOCKED"), new("Locked At", startTime), new("Exit", "Password required")],
            [new("Equipment Operation", "Blocked"), new("Manual Command", "Blocked"), new("Cycle Start", "Blocked")],
            partLifeRows,
            protectWindowItems,
            historyRows);
        return new CScreenViewModel(
            EN_MENU.Pm,
            "PM",
            "Operation lock screen. Password is required to exit PM.",
            [new("Lock", current.IsLocked ? "LOCKED" : "UNLOCKED"), new("Locked At", current.LockedAt?.ToString("HH:mm:ss") ?? "-")],
            [new("Operation", [new("Equipment Control", "Blocked"), new("Exit PM", "Password required")])],
            pm: this
        );
    }

    private void Apply(
        bool isLocked,
        string startTime,
        IReadOnlyList<ST_DISPLAY_ITEM> lockItems,
        IReadOnlyList<ST_DISPLAY_ITEM> blockedItems,
        IReadOnlyList<ST_PM_PART_LIFE_ROW> partLifeRows,
        IReadOnlyList<ST_DISPLAY_ITEM> protectWindowItems,
        IReadOnlyList<ST_PM_HISTORY_ROW> historyRows)
    {
        IsLocked = isLocked;
        StartTime = startTime;
        LockItems = lockItems;
        BlockedItems = blockedItems;
        PartLifeRows = partLifeRows;
        ProtectWindowItems = protectWindowItems;
        HistoryRows = historyRows;
    }

    private IReadOnlyList<ST_PM_PART_LIFE_ROW> CreatePartLifeRows(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = ReadCrystalLimit(cancellationToken);
        var laserNumbers = interfaceManager.GetInterfaceList(EN_EQP_MODULE.TalonLaser)
            .Select(item => item.Number)
            .ToHashSet();

        return Enumerable.Range(1, 8)
            .Select(headNo => CreatePartLifeRow(headNo, limit, laserNumbers))
            .ToArray();
    }

    private ST_PM_PART_LIFE_ROW CreatePartLifeRow(int headNo, double? limit, IReadOnlySet<int> laserNumbers)
    {
        var laserNo = headNo - 1;
        var limitText = limit?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-";
        if (!laserNumbers.Contains(laserNo))
            return new($"H{headNo:00}", "N/C", limitText);

        var snapshot = interfaceManager.OpticSnapshots.GetLaser(laserNo);
        if (snapshot.Value is null)
            return new($"H{headNo:00}", NormalizeSnapshotState(snapshot.State), limitText);

        var status = interfaceManager.GetCachedTalonStatus(laserNo);
        var useText = double.IsFinite(status.ThgHour)
            ? status.ThgHour.ToString("0.0", CultureInfo.InvariantCulture)
            : "ERR";
        return new($"H{headNo:00}", useText, limitText);
    }

    private double? ReadCrystalLimit(CancellationToken cancellationToken)
    {
        var text = settingManager.GetValue(EN_SETTING_TAB.Option, LaserThgHourLimitKey, "", cancellationToken);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            && value >= 0.0
            ? value
            : null;
    }

    private async Task<IReadOnlyList<ST_DISPLAY_ITEM>> CreateProtectWindowItems(CancellationToken cancellationToken)
    {
        var recipeId = selectedRecipeIdProvider().Trim();
        var recipes = await recipeManager.LoadRecipes(cancellationToken).ConfigureAwait(false);
        var recipe = recipes.FirstOrDefault(item => item.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase));
        string ReadRecipeValue(string key) => recipe?.Parameters
            .FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                || item.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

        return
        [
            new("CURRENT COUNT", FormatRecipeCount(ReadRecipeValue(CProtectWindowRecipeData.CurrentCountKey), CProtectWindowRecipeData.CurrentCountKey), "count"),
            new("MAX COUNT", FormatRecipeCount(ReadRecipeValue(CProtectWindowRecipeData.MaximumCountKey), CProtectWindowRecipeData.MaximumCountKey), "count")
        ];
    }

    private static string FormatRecipeCount(string text, string key)
    {
        try
        {
            return CProtectWindowRecipeData.ParseCount(text, key).ToString("N0", CultureInfo.InvariantCulture);
        }
        catch (InvalidDataException)
        {
            return "INVALID";
        }
    }

    private static string NormalizeSnapshotState(string state) => state.ToUpperInvariant() switch
    {
        "OFFLINE" => "OFFLINE",
        "N/C" => "N/C",
        "STALE" => "STALE",
        "ERROR" => "ERR",
        _ => "LOADING"
    };

    private static IReadOnlyList<ST_PM_PART_LIFE_ROW> CreateEmptyPartLifeRows() => Enumerable.Range(1, 8)
        .Select(index => new ST_PM_PART_LIFE_ROW($"H{index:00}", "-", "-"))
        .ToArray();

    private static IReadOnlyList<ST_DISPLAY_ITEM> CreateEmptyProtectWindowItems() =>
    [
        new("CURRENT COUNT", "-", "count"),
        new("MAX COUNT", "-", "count")
    ];

    private static IReadOnlyList<ST_PM_HISTORY_ROW> CreateHistoryRows(IReadOnlyList<ST_PM_HISTORY_ENTRY> entries) => entries
        .Select(entry => new ST_PM_HISTORY_ROW(
            entry.LockedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            entry.UnlockedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-",
            entry.UnlockedAt is { } unlockedAt ? FormatDuration(unlockedAt - entry.LockedAt) : "-",
            entry.Reason))
        .ToArray();

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 100
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}

public sealed record ST_PM_PART_LIFE_ROW(
    string Head,
    string CrystalUse,
    string CrystalLimit);

public sealed record ST_PM_HISTORY_ROW(
    string LockTime,
    string UnlockTime,
    string Duration,
    string Reason);
