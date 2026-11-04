using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Drilling.UI.Popup;
public partial class CControllerResetSequenceDialog : Window
{
    private readonly IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> _controllerOptions;
    private readonly IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> _targetOptions;
    private readonly Func<CControllerResetSequenceDialog, IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION>, Func<bool>, CancellationToken, Task> _executeSequence;
    private IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_BLOCK> _sequenceBlocks = [];
    private CancellationTokenSource? _runCancellation;
    private bool _isRunning;
    private bool _stopRequested;
    public CControllerResetSequenceDialog(IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> controllerOptions, Func<CControllerResetSequenceDialog, IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION>, Func<bool>, CancellationToken, Task> executeSequence)
    {
        InitializeComponent();
        _executeSequence = executeSequence;
        _controllerOptions = controllerOptions.Count == 0 ? [new ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION("CONTROLLER 1", 0, false)] : controllerOptions;
        _targetOptions = CreateTargetOptions(_controllerOptions);
        TargetComboBox.ItemsSource = _targetOptions;
        TargetComboBox.SelectedIndex = 0;
        SelectedTarget = _targetOptions[0];
        RefreshSequenceBlocks();
    }

    public ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION SelectedTarget { get; private set; }

    public bool IsStopRequested
    {
        get
        {
            return _stopRequested;
        }
    }

    private static IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> CreateTargetOptions(IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> controllerOptions)
    {
        var targets = new List<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION>(controllerOptions.Count + 1);
        targets.AddRange(controllerOptions);
        targets.Add(new ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION("ALL CONTROLLER", -1, true));
        return targets;
    }

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetComboBox.SelectedItem is not ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION target)
        {
            return;
        }

        SelectedTarget = target;
        RefreshSequenceBlocks();
    }

    private void RefreshSequenceBlocks()
    {
        if (_isRunning)
        {
            return;
        }

        _sequenceBlocks = GetSelectedControllers().Select(controller => new ST_CONTROLLER_RESET_SEQUENCE_BLOCK(controller.AutomationNo, controller.DisplayName, CreateSteps())).ToArray();
        SequenceBlocksControl.ItemsSource = _sequenceBlocks;
        StatusText.Text = "Ready";
    }

    private static IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_STEP> CreateSteps()
    {
        return[new(1, "Controller Reset", "WAIT"), new(2, "All Axis Enable", "WAIT"), new(3, "All Axis Home", "WAIT"), new(4, "DIO ON", "WAIT"), new(5, "DOE Z Recipe Position Move", "WAIT")];
    }

    private IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> GetSelectedControllers()
    {
        return SelectedTarget.IsAll ? _controllerOptions : [SelectedTarget];
    }

    public void SetStatusMessage(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatusMessage(message));
            return;
        }

        StatusText.Text = message;
    }

    public void SetStepState(int automationNo, int stepNo, string state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStepState(automationNo, stepNo, state));
            return;
        }

        foreach (var block in _sequenceBlocks)
        {
            if (block.AutomationNo != automationNo)
            {
                continue;
            }

            foreach (var step in block.Steps)
            {
                if (step.No == stepNo)
                {
                    step.State = state;
                    return;
                }
            }
        }
    }

    public void SkipWaitingSteps(int automationNo)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SkipWaitingSteps(automationNo));
            return;
        }

        foreach (var block in _sequenceBlocks)
        {
            if (block.AutomationNo != automationNo)
            {
                continue;
            }

            foreach (var step in block.Steps)
            {
                if (step.State.Equals("WAIT", StringComparison.OrdinalIgnoreCase))
                {
                    step.State = "SKIP";
                }
            }
        }
    }

    public void SkipAllWaitingSteps()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(SkipAllWaitingSteps);
            return;
        }

        foreach (var block in _sequenceBlocks)
        {
            foreach (var step in block.Steps)
            {
                if (step.State.Equals("WAIT", StringComparison.OrdinalIgnoreCase))
                {
                    step.State = "SKIP";
                }
            }
        }
    }

    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        await RunSequence();
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        RequestStop();
    }

    private async Task RunSequence()
    {
        RefreshSequenceBlocks();
        _isRunning = true;
        _stopRequested = false;
        TargetComboBox.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        CloseButton.IsEnabled = false;
        SetStatusMessage("Running");
        _runCancellation = new CancellationTokenSource();
        var controllers = GetSelectedControllers();
        try
        {
            await _executeSequence(this, controllers, () => _stopRequested, _runCancellation.Token);
            SetStatusMessage(_stopRequested ? "Stopped after current step." : "Completed.");
        }
        catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            SetStatusMessage($"Failed. {exception.Message}");
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            _isRunning = false;
            TargetComboBox.IsEnabled = true;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StopButton.Content = "STOP";
            CloseButton.IsEnabled = true;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void RequestStop()
    {
        if (!_isRunning || _stopRequested)
        {
            return;
        }

        _stopRequested = true;
        StopButton.Content = "STOP REQUESTED";
        StopButton.IsEnabled = false;
        SetStatusMessage("Stop requested. The current step will finish, then remaining steps will be skipped.");
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        e.Cancel = true;
        RequestStop();
        SetStatusMessage("Stop requested. Wait for the current step to finish.");
    }
}

public sealed record ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION(string DisplayName, int AutomationNo, bool IsAll);
public sealed class ST_CONTROLLER_RESET_SEQUENCE_BLOCK
{
    public ST_CONTROLLER_RESET_SEQUENCE_BLOCK(int AutomationNo, string Title, IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_STEP> Steps)
    {
        this.AutomationNo = AutomationNo;
        this.Title = Title;
        this.Steps = Steps;
    }

    public int AutomationNo { get; }
    public string Title { get; }
    public IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_STEP> Steps { get; }
}

public sealed class ST_CONTROLLER_RESET_SEQUENCE_STEP : INotifyPropertyChanged
{
    private string _state;
    public ST_CONTROLLER_RESET_SEQUENCE_STEP(int No, string Name, string State)
    {
        this.No = No;
        this.Name = Name;
        _state = State;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int No { get; }
    public string Name { get; }

    public string State
    {
        get
        {
            return _state;
        }

        set
        {
            if (_state.Equals(value, StringComparison.Ordinal))
            {
                return;
            }

            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }
}
