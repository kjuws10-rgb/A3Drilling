using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;
using Drilling.UI.Menu;
using System.Windows.Media;

namespace Drilling.UI.Menu.Menus;
public abstract class CMenuBase : CBindingBase
{
    public Func<string, bool>? OperationAllowed { get; set; }

    protected bool CheckOperationAlarm(string operation) => OperationAllowed?.Invoke(operation) ?? true;
    public virtual Task StopForHeavyAlarm() => Task.CompletedTask;
    public abstract EN_MENU Menu { get; }

    public abstract Task<CScreenViewModel> Build(CancellationToken cancellationToken = default);
    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
    {
    }
}

public enum EN_MENU
{
    Main,
    Manual,
    Recipe,
    Setting,
    Alarm,
    Monitor,
    Review,
    Correction,
    Pm,
    Exit
}

public sealed record ST_DISPLAY_ITEM(string Name, string Value, string Detail = "")
{
    public Brush ValueBrush
    {
        get
        {
            return CStatusBrush.ForDisplayState(string.IsNullOrWhiteSpace(Detail) ? Value : Detail);
        }
    }

    public Brush StateBrush
    {
        get
        {
            return CStatusBrush.ForDisplayState(Value);
        }
    }
}

internal static class CStatusBrush
{
    private static readonly Brush LightPrimaryText = Frozen(0x05, 0x05, 0x05);
    private static readonly Brush LightRecipe = Frozen(0x05, 0x05, 0x05);
    private static readonly Brush LightOnline = Frozen(0x2E, 0x8B, 0x57);
    private static readonly Brush LightSimul = Frozen(0xB8, 0x7A, 0x00);
    private static readonly Brush LightOffline = Frozen(0xE6, 0x21, 0x21);
    private static readonly Brush LightWait = Frozen(0xB8, 0x7A, 0x00);
    private static readonly Brush LightActive = Frozen(0x46, 0x82, 0xB4);
    private static readonly Brush LightMuted = Frozen(0x1F, 0x1F, 0x1F);
    private static readonly Brush LightCommandBlue = Frozen(0x46, 0x82, 0xB4);
    private static readonly Brush LightCommandBlueBorder = Frozen(0x2D, 0x5F, 0x8A);
    private static readonly Brush LightCommandGreen = Frozen(0x2E, 0x8B, 0x57);
    private static readonly Brush LightCommandGreenBorder = Frozen(0x1F, 0x6E, 0x42);
    private static readonly Brush LightCommandRed = Frozen(0xE6, 0x21, 0x21);
    private static readonly Brush LightCommandRedBorder = Frozen(0xB0, 0x00, 0x00);
    private static readonly Brush LightCommandDark = Frozen(0x70, 0x80, 0x90);
    private static readonly Brush LightCommandDarkBorder = Frozen(0x5B, 0x6F, 0x84);
    private static readonly Brush LightCardBackground = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush LightSoftSelectedBackground = Frozen(0xF3, 0xF9, 0xFF);
    private static readonly Brush LightMaskBackground = Frozen(0xF7, 0xE5, 0xE5);
    private static readonly Brush LightChessBackground = Frozen(0xF0, 0xE7, 0xFF);
    private static readonly Brush DarkPrimaryText = Frozen(0xF3, 0xF6, 0xFA);
    private static readonly Brush DarkRecipe = Frozen(0xB8, 0xC7, 0xD6);
    private static readonly Brush DarkOnline = Frozen(0x5F, 0x9B, 0x75);
    private static readonly Brush DarkSimul = Frozen(0xC4, 0x9A, 0x35);
    private static readonly Brush DarkOffline = Frozen(0xBF, 0x62, 0x62);
    private static readonly Brush DarkWait = Frozen(0xC4, 0x9A, 0x35);
    private static readonly Brush DarkActive = Frozen(0x86, 0xA8, 0xC1);
    private static readonly Brush DarkMuted = Frozen(0x87, 0x92, 0x9E);
    private static readonly Brush DarkCommandBlue = Frozen(0x3F, 0x66, 0x88);
    private static readonly Brush DarkCommandBlueBorder = Frozen(0x55, 0x7C, 0x9B);
    private static readonly Brush DarkCommandGreen = Frozen(0x3A, 0x76, 0x56);
    private static readonly Brush DarkCommandGreenBorder = Frozen(0x50, 0x91, 0x6C);
    private static readonly Brush DarkCommandRed = Frozen(0x93, 0x40, 0x40);
    private static readonly Brush DarkCommandRedBorder = Frozen(0xAF, 0x58, 0x58);
    private static readonly Brush DarkCommandDark = Frozen(0x2A, 0x32, 0x3B);
    private static readonly Brush DarkCommandDarkBorder = Frozen(0x4B, 0x59, 0x67);
    private static readonly Brush DarkCardBackground = Frozen(0x20, 0x26, 0x2D);
    private static readonly Brush DarkSoftSelectedBackground = Frozen(0x24, 0x3B, 0x4D);
    private static readonly Brush DarkMaskBackground = Frozen(0x3A, 0x22, 0x24);
    private static readonly Brush DarkChessBackground = Frozen(0x32, 0x26, 0x4A);
    public static Brush PrimaryText
    {
        get
        {
            return Pick(LightPrimaryText, DarkPrimaryText);
        }
    }

    public static Brush Recipe
    {
        get
        {
            return Pick(LightRecipe, DarkRecipe);
        }
    }

    public static Brush Online
    {
        get
        {
            return Pick(LightOnline, DarkOnline);
        }
    }

    public static Brush Simul
    {
        get
        {
            return Pick(LightSimul, DarkSimul);
        }
    }

    public static Brush Offline
    {
        get
        {
            return Pick(LightOffline, DarkOffline);
        }
    }

    public static Brush Wait
    {
        get
        {
            return Pick(LightWait, DarkWait);
        }
    }

    public static Brush Active
    {
        get
        {
            return Pick(LightActive, DarkActive);
        }
    }

    public static Brush Muted
    {
        get
        {
            return Pick(LightMuted, DarkMuted);
        }
    }

    public static Brush CommandBlue
    {
        get
        {
            return Pick(LightCommandBlue, DarkCommandBlue);
        }
    }

    public static Brush CommandBlueBorder
    {
        get
        {
            return Pick(LightCommandBlueBorder, DarkCommandBlueBorder);
        }
    }

    public static Brush CommandGreen
    {
        get
        {
            return Pick(LightCommandGreen, DarkCommandGreen);
        }
    }

    public static Brush CommandGreenBorder
    {
        get
        {
            return Pick(LightCommandGreenBorder, DarkCommandGreenBorder);
        }
    }

    public static Brush CommandRed
    {
        get
        {
            return Pick(LightCommandRed, DarkCommandRed);
        }
    }

    public static Brush CommandRedBorder
    {
        get
        {
            return Pick(LightCommandRedBorder, DarkCommandRedBorder);
        }
    }

    public static Brush CommandDark
    {
        get
        {
            return Pick(LightCommandDark, DarkCommandDark);
        }
    }

    public static Brush CommandDarkBorder
    {
        get
        {
            return Pick(LightCommandDarkBorder, DarkCommandDarkBorder);
        }
    }

    public static Brush CardBackground
    {
        get
        {
            return Pick(LightCardBackground, DarkCardBackground);
        }
    }

    public static Brush SoftSelectedBackground
    {
        get
        {
            return Pick(LightSoftSelectedBackground, DarkSoftSelectedBackground);
        }
    }

    public static Brush MaskBackground
    {
        get
        {
            return Pick(LightMaskBackground, DarkMaskBackground);
        }
    }

    public static Brush ChessBackground
    {
        get
        {
            return Pick(LightChessBackground, DarkChessBackground);
        }
    }

    public static Brush SoftSelectedText
    {
        get
        {
            return IsLightTheme ? PrimaryText : Brushes.White;
        }
    }

    public static bool IsLightTheme
    {
        get
        {
            return Drilling.UI.CThemeManager.CurrentTheme == Drilling.UI.EN_UI_THEME.Light;
        }
    }

    public static Brush ForHeaderState(string state)
    {
        return Normalize(state) switch
        {
            "RECIPE" => Recipe,
            "ONLINE" or "CLEAR" or "READY" or "OK" or "RUN" or "SAFE" or "DONE" => Online,
            "SIM" or "SIMUL" or "SIMULATION" => Simul,
            "OFFLINE" or "OCCUR" or "ERROR" or "NG" or "FAILED" or "FAIL" or "ALARM" or "STOP" or "STOPPED" => Offline,
            "WAIT" or "WAITING" or "WARN" or "WARNING" or "PENDING" => Wait,
            "ACTIVE" or "RUNNING" => Active,
            _ => Muted
        };
    }

    public static Brush ForDisplayState(string state)
    {
        return Normalize(state) switch
        {
            "OK" or "DONE" or "COMPLETE" or "COMPLETED" or "CLEAR" or "READY" => Online,
            "RUN" or "RUNNING" or "ACTIVE" or "CURRENT" => Active,
            "WAIT" or "WAITING" or "PENDING" or "WARN" or "WARNING" => Wait,
            "SKIP" or "SKIPPED" or "DISABLE" or "DISABLED" or "NOT USE" => Muted,
            "ERROR" or "NG" or "FAILED" or "FAIL" or "ALARM" or "STOP" or "STOPPED" => Offline,
            "SIM" or "SIMUL" or "SIMULATION" => Simul,
            _ => Muted
        };
    }

    public static Brush ForHeadStatus(string status)
    {
        return Normalize(status) switch
        {
            "RUN" or "RUNNING" or "ACTIVE" => Active,
            "WAIT" or "WAITING" or "IDLE" => Wait,
            "SKIP" or "SKIPPED" or "DISABLE" or "DISABLED" or "NOT USE" => Muted,
            "ERROR" or "ALARM" => Offline,
            _ => Online
        };
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static Brush Pick(Brush lightBrush, Brush darkBrush)
    {
        return IsLightTheme ? lightBrush : darkBrush;
    }

    public static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed record ST_HEADER_STATUS_ITEM(
    string Name, 
    string Value, 
    string State, 
    bool CanNavigate = false, 
    string PageText = "", 
    CButtonCommand? PreviousCommand = null, 
    CButtonCommand? NextCommand = null, 
    CButtonCommand? OpenCommand = null
)
{
    // Keep the full status in Value while fitting the Vision peer detail under
    // ONLINE in the existing fixed-width communication card.
    public string DisplayValue => Name.Equals("VISION", StringComparison.OrdinalIgnoreCase)
        ? Value.Replace(" (", "\n(", StringComparison.Ordinal) : Value;
    public Brush CardBrush
    {
        get
        {
            return Name.Equals("RECIPE", StringComparison.OrdinalIgnoreCase) ? CStatusBrush.Frozen(0xFF, 0xFF, 0xFF) : CStatusBrush.ForHeaderState(State);
        }
    }

    public Brush CardTextBrush
    {
        get
        {
            return Name.Equals("RECIPE", StringComparison.OrdinalIgnoreCase) ? CStatusBrush.PrimaryText : Brushes.White;
        }
    }

    public Brush AccentBrush
    {
        get
        {
            return CStatusBrush.ForHeaderState(State);
        }
    }

    public Brush ValueBrush
    {
        get
        {
            return CStatusBrush.ForHeaderState(State);
        }
    }

    public CButtonCommand Previous
    {
        get
        {
            return PreviousCommand ?? CButtonCommand.NoOp;
        }
    }

    public CButtonCommand Next
    {
        get
        {
            return NextCommand ?? CButtonCommand.NoOp;
        }
    }

    public CButtonCommand Open
    {
        get
        {
            return OpenCommand ?? CButtonCommand.NoOp;
        }
    }
}

public sealed record ST_SCREEN_SECTION(string Title, IReadOnlyList<ST_DISPLAY_ITEM> Items);
public sealed class CScreenViewModel(
    EN_MENU menu, 
    string title, 
    string subtitle, 
    IReadOnlyList<ST_DISPLAY_ITEM> metrics, 
    IReadOnlyList<ST_SCREEN_SECTION> sections, 
    bool showCycleControls = false, 
    CMenuMain? mainOperating = null, 
    CMenuManual? manual = null, 
    CMenuRecipe? recipe = null, 
    CMenuSetting? setting = null, 
    CMenuAlarm? alarm = null, 
    CMenuMonitor? monitor = null, 
    CMenuReview? review = null, 
    CMenuCorrection? correction = null, 
    CMenuPm? pm = null
)
{
    public EN_MENU Menu { get; } = menu;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public IReadOnlyList<ST_DISPLAY_ITEM> Metrics { get; } = metrics;
    public IReadOnlyList<ST_SCREEN_SECTION> Sections { get; } = sections;
    public bool ShowCycleControls { get; } = showCycleControls;
    public CMenuMain? MainOperating { get; } = mainOperating;
    public CMenuManual? Manual { get; } = manual;
    public CMenuRecipe? Recipe { get; } = recipe;
    public CMenuSetting? Setting { get; } = setting;
    public CMenuAlarm? Alarm { get; } = alarm;
    public CMenuMonitor? Monitor { get; } = monitor;
    public CMenuReview? Review { get; } = review ?? manual?.Review;
    public CMenuCorrection? Correction { get; } = correction;
    public CMenuPm? Pm { get; } = pm;

    public bool IsMainLayout
    {
        get
        {
            return Menu == EN_MENU.Main;
        }
    }

    public bool IsManualLayout
    {
        get
        {
            return Menu == EN_MENU.Manual;
        }
    }

    public bool IsRecipeLayout
    {
        get
        {
            return Menu == EN_MENU.Recipe;
        }
    }

    public bool IsSettingLayout
    {
        get
        {
            return Menu == EN_MENU.Setting;
        }
    }

    public bool IsAlarmLayout
    {
        get
        {
            return Menu == EN_MENU.Alarm;
        }
    }

    public bool IsMonitorLayout
    {
        get
        {
            return Menu == EN_MENU.Monitor;
        }
    }

    public bool IsReviewLayout
    {
        get
        {
            return Menu == EN_MENU.Review;
        }
    }

    public bool IsCorrectionLayout
    {
        get
        {
            return Menu == EN_MENU.Correction;
        }
    }

    public bool IsPmLayout
    {
        get
        {
            return Menu == EN_MENU.Pm;
        }
    }

    public bool IsGenericLayout
    {
        get
        {
            return !IsMainLayout && !IsManualLayout && !IsRecipeLayout && !IsSettingLayout && !IsAlarmLayout && !IsMonitorLayout && !IsReviewLayout && !IsCorrectionLayout && !IsPmLayout;
        }
    }
}

public sealed class CMenuItem(EN_MENU menu, string name) : CBindingBase
{
    private bool _isEnabled = true;
    public EN_MENU Menu { get; } = menu;
    public string Name { get; } = name;

    public bool IsEnabled
    {
        get
        {
            return _isEnabled;
        }

        set
        {
            SetProperty(ref _isEnabled, value);
        }
    }

    public Geometry IconGeometry
    {
        get
        {
            return CMenuIcon.Get(Menu);
        }
    }

    public string Description
    {
        get
        {
            return Menu switch
            {
                EN_MENU.Main => "Auto",
                EN_MENU.Manual => "Manual",
                EN_MENU.Recipe => "Recipe",
                EN_MENU.Setting => "Setting",
                EN_MENU.Alarm => "Alarm",
                EN_MENU.Monitor => "Monitor",
                EN_MENU.Review => "Review",
                EN_MENU.Correction => "Correction",
                EN_MENU.Pm => "PM Lock",
                EN_MENU.Exit => "Exit",
                _ => string.Empty
            };
        }
    }
}

internal static class CMenuIcon
{
    private static readonly IReadOnlyDictionary<EN_MENU, Geometry> Icons = new Dictionary<EN_MENU, Geometry>
    {
        [EN_MENU.Main] = Icon("M3,11 L12,4 L21,11 M5,10 V21 H10 V15 H14 V21 H19 V10"),
        [EN_MENU.Manual] = Icon("M7,12 V6 M10,12 V4 M13,12 V5 M16,13 V7 M7,12 L5,10 C4,9 3,10 4,12 L6,17 C7,20 9,22 12,22 H14 C17,22 19,19 19,16 V10"),
        [EN_MENU.Recipe] = Icon("M6,4 H18 V20 H6 Z M9,8 H15 M9,12 H15 M9,16 H13"),
        [EN_MENU.Setting] = Icon("M12,8 C14.2,8 16,9.8 16,12 C16,14.2 14.2,16 12,16 C9.8,16 8,14.2 8,12 C8,9.8 9.8,8 12,8 M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M19.1,4.9 L17,7 M7,17 L4.9,19.1"),
        [EN_MENU.Alarm] = Icon("M8,18 H16 M10,20 H14 M6,17 H18 L16,15 V10 C16,7.8 14.2,6 12,6 C9.8,6 8,7.8 8,10 V15 Z M10,6 C10,4.9 10.9,4 12,4 C13.1,4 14,4.9 14,6"),
        [EN_MENU.Monitor] = Icon("M4,5 H20 V16 H4 Z M9,20 H15 M12,16 V20 M7,11 H10 L11.5,8 L14,14 L15,11 H17"),
        [EN_MENU.Review] = Icon("M11,5 C14.3,5 17,7.7 17,11 C17,14.3 14.3,17 11,17 C7.7,17 5,14.3 5,11 C5,7.7 7.7,5 11,5 M15.5,15.5 L20,20 M11,7 V15 M7,11 H15"),
        [EN_MENU.Correction] = Icon("M12,4 V8 M12,16 V20 M4,12 H8 M16,12 H20 M8.5,8.5 L6,6 M15.5,15.5 L18,18 M15.5,8.5 L18,6 M8.5,15.5 L6,18 M10,10 H14 V14 H10 Z"),
        [EN_MENU.Pm] = Icon("M15,4 C17,3 19,4 20,5 L17,8 L19,10 L16,13 L14,11 L7,18 C6,19 5,19 4,18 C3,17 3,16 4,15 L11,8 L9,6 L12,3 Z"),
        [EN_MENU.Exit] = Icon("M10,5 H5 V19 H10 M13,8 L18,12 L13,16 M18,12 H8")
    };
    public static Geometry Get(EN_MENU menu)
    {
        return Icons.TryGetValue(menu, out var geometry) ? geometry : Geometry.Empty;
    }

    private static Geometry Icon(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
