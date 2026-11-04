namespace Drilling.UI.Popup
{
    using System.Windows;
    using Drilling.UI.Menu.Menus;
    using System.Diagnostics;
    using System.Windows.Threading;
    using Drilling.Common.Interface;

    public partial class CProtectWindowRequestDialog : Window
    {
        public CProtectWindowRequestDialog(CMainProtectWindowPanel status)
        {
            InitializeComponent();
            DataContext = status;
            Width = Math.Min(Width, SystemParameters.WorkArea.Width * 0.95);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.9);
        }

        private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>Menu-independent, modeless notice. Copies shared snapshots only; never reads a device or recipe file.</summary>
    public sealed class CProtectWindowRequestNotice : IDisposable
    {
        private readonly CInterfaceManager _interfaces;
        private readonly Window _owner;
        private readonly Func<CMainProtectWindowPanel, CProtectWindowRequestDialog> _createDialog;
        private readonly DispatcherTimer _timer;
        private CProtectWindowRequestDialog? _dialog;
        private bool _pending;
        private bool _disposed;
        private string _lastError = "";
        public CMainProtectWindowPanel Status { get; } = new();
        public CProtectWindowRequestDialog? CurrentDialog => _dialog;
        public TimeSpan RefreshInterval => _timer.Interval;

        public CProtectWindowRequestNotice(CInterfaceManager interfaces, Window owner, Func<CMainProtectWindowPanel, CProtectWindowRequestDialog>? createDialog = null)
        {
            _interfaces = interfaces;
            _owner = owner;
            _createDialog = createDialog ?? (status => new(status));
            _timer = new(DispatcherPriority.Background, owner.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Tick;
            _owner.Closed += OwnerClosed;
        }

        public void Start()
        {
            _owner.Dispatcher.VerifyAccess();
            if (_disposed)
                return;
            Refresh();
            _timer.Start();
        }

        public void Refresh()
        {
            _owner.Dispatcher.VerifyAccess();
            if (_disposed)
                return;
            var values = _interfaces.Melsec.GetCachedSnapshot([CMainProtectWindowPanel.CurrentCountId, CMainProtectWindowPanel.RequestId, CMainProtectWindowPanel.CompleteId, CMainProtectWindowPanel.TrayCountId], out var map);
            var connections = _interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).ToDictionary(device => device.Number, device => device.IsSimulation ? EN_COMM_STATE.Simulation : device.ConnectionState);
            Status.RefreshSignals(values, map, connections);
            // Preserve a last-confirmed ON across stale/offline feedback. Only a valid OFF dismisses it.
            if (Status.Request.IsActive is { } active)
                _pending = active;
            if (!_pending)
            {
                _dialog?.Close();
                return;
            }

            if (!_owner.IsVisible)
                return;
            if (_dialog is null)
            {
                var dialog = _createDialog(Status);
                dialog.Owner = _owner;
                dialog.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_dialog, dialog))
                        _dialog = null;
                };
                _dialog = dialog;
                dialog.Show(); // Modeless: does not block operation or the sequence.
            }
            else if (!_dialog.IsVisible)
                _dialog.Show();
        }

        private void Tick(object? sender, EventArgs e)
        {
            try
            {
                Refresh();
                _lastError = "";
            }
            catch (Exception error)
            {
                if (_lastError != error.Message)
                    Trace.TraceError($"Protect Window notice refresh failed: {error}");
                _lastError = error.Message;
            }
        }

        private void OwnerClosed(object? sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= Tick;
            _owner.Closed -= OwnerClosed;
            _dialog?.Close();
            _dialog = null;
        }
    }
}
