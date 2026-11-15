namespace Drilling.UI.Popup
{
    using System.Windows;
    using System.ComponentModel;
    using System.Globalization;
    using Drilling.Common.Alarm;
    using Drilling.UI.Menu;
    using System.Windows.Media;

    public partial class CAlarmPopupDialog : Window
    {
        public CAlarmPopupPanel Panel { get; }

        public CAlarmPopupDialog(CAlarmPopupPanel panel)
        {
            InitializeComponent();
            Panel = panel;
            DataContext = panel;
            panel.Refresh();
            ApplySeverityTheme(panel);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        }

        private void ApplySeverityTheme(CAlarmPopupPanel panel)
        {
            var lightOnly = panel.Rows.Any(row => row.Level == "LIGHT")
                && panel.Rows.All(row => row.Level != "HEAVY");
            if (!lightOnly)
                return;

            Resources["AlarmSurfaceBrush"] = Brush(0xFF, 0xF7, 0xED);
            Resources["GridAltBackgroundBrush"] = Brush(0xFF, 0xFB, 0xF5);
            Resources["GridHeaderBackgroundBrush"] = Brush(0xB4, 0x53, 0x09);
            Resources["GridLineBrush"] = Brush(0xF0, 0xC8, 0x9D);
            Resources["MenuItemSelectedBackgroundBrush"] = Brush(0xB4, 0x53, 0x09);
            Resources["AccentBrush"] = Brush(0xD9, 0x77, 0x06);
            Resources["AccentDarkBrush"] = Brush(0xB4, 0x53, 0x09);
        }

        private static SolidColorBrush Brush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    }

    public sealed class CAlarmPopupPanel(CAlarmManager alarms, Func<Task> reset) : INotifyPropertyChanged
    {
        private long _revision = -1;
        private bool _resetting;
        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<CAlarmPopupRow> Rows { get; private set; } = [];
        public string Summary => $"현재 알람 {Rows.Count}건 · HEAVY {Rows.Count(row => row.Level == "HEAVY")} · LIGHT {Rows.Count(row => row.Level == "LIGHT")}";
        public string RecoveryStatus { get; private set; } = "";
        public CButtonCommand ResetCommand => _resetCommand ??= new(async _ =>
        {
            if (_resetting)
                return;
            _resetting = true;
            _resetCommand?.NotifyCanExecuteChanged();
            try
            {
                await reset();
                Refresh();
            }
            catch (Exception error)
            {
                SetRecoveryStatus("ALARM RESET 실패: " + error.Message);
                Refresh();
            }
            finally
            {
                _resetting = false;
                _resetCommand?.NotifyCanExecuteChanged();
            }
        }, _ => !_resetting && Rows.Count > 0);

        private CButtonCommand? _resetCommand;
        public void SetRecoveryStatus(string text)
        {
            if (RecoveryStatus == text)
                return;
            RecoveryStatus = text;
            PropertyChanged?.Invoke(this, new(nameof(RecoveryStatus)));
        }

        public void Refresh()
        {
            var snapshot = alarms.GetCurrentSnapshot();
            if (snapshot.Revision == _revision)
                return;
            _revision = snapshot.Revision;
            Rows = snapshot.Rows.Select((row, index) => new CAlarmPopupRow(
                index + 1, 
                row.Code, 
                row.Severity == EN_ALARM_LEVEL.Critical ? "HEAVY" : row.Severity == EN_ALARM_LEVEL.Warning ? "LIGHT" : "INFO", 
                row.Group, 
                row.Message, 
                row.RecoveryAction, 
                row.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            )).ToArray();
            PropertyChanged?.Invoke(this, new(null));
            _resetCommand?.NotifyCanExecuteChanged();
        }
    }

    public sealed record CAlarmPopupRow(int No, int Code, string Level, string Group, string Cause, string Solution, string Time);
}
