using System.Globalization;
using Drilling.Common.Automation;
using Drilling.Common.Managers;

namespace Drilling.UI.Menu.Menus;
// Presentation adapter only: no timer, thread, or device reads.
internal sealed class CScannerStatusSnapshotReader(CAutomationManager automationManager)
{
    private readonly object _snapshotLock = new();
    private IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> _snapshot = [];
    private IReadOnlyList<ST_SCANNER_AXIS_SETTING> _axisDefinitions = [];
    private int _selectedHeadNo = 1;
    public void SelectHead(int headNo)
    {
        var normalizedHeadNo = NormalizeHeadNo(headNo);
        lock (_snapshotLock)
        {
            if (_selectedHeadNo == normalizedHeadNo)
            {
                return;
            }

            _selectedHeadNo = normalizedHeadNo;
            _axisDefinitions = [];
            _snapshot = [];
        }
    }

    public void SelectHead(int headNo, IReadOnlyDictionary<string, string> parameters)
    {
        var normalizedHeadNo = NormalizeHeadNo(headNo);
        var axisDefinitions = BuildAxisDefinitions(parameters, normalizedHeadNo);
        lock (_snapshotLock)
        {
            if (_selectedHeadNo == normalizedHeadNo && IsSameAxisDefinitions(_axisDefinitions, axisDefinitions))
            {
                return;
            }

            _selectedHeadNo = normalizedHeadNo;
            _axisDefinitions = axisDefinitions;
            _snapshot = [];
        }
    }

    public IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> GetSnapshot()
    {
        RefreshSnapshot(CancellationToken.None);
        lock (_snapshotLock) return _snapshot;
    }

    private void RefreshSnapshot(CancellationToken cancellationToken)
    {
        var context = ReadPollingContext();
        try
        {
            var items = new List<ST_SCANNER_AXIS_STATUS_ITEM>(context.AxisDefinitions.Count);
            foreach (var axis in context.AxisDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(BuildScannerAxisStatusItem(axis, cancellationToken));
            }

            SetSnapshot(context.HeadNo, items);
        }
        catch (Exception exception)when (exception is not OperationCanceledException)
        {
            SetSnapshot(context.HeadNo, CreatePollingErrorSnapshot(exception.Message));
        }
    }

    private ST_SCANNER_AXIS_STATUS_ITEM BuildScannerAxisStatusItem(ST_SCANNER_AXIS_SETTING axis, CancellationToken cancellationToken)
    {
        var axisReference = axis.AxisReference.Trim();
        var displayAxisReference = string.IsNullOrWhiteSpace(axisReference) ? "-" : axisReference;
        var automationLabel = $"#{axis.AutomationNo}";
        if (!axis.IsUsed)
        {
            return new ST_SCANNER_AXIS_STATUS_ITEM($"H{axis.HeadNo:00}", automationLabel, axis.AxisName, "Z NOT USE", "NOT USE", "-", "-", "-", "-", "-", "-", "-", "NOT USE", "Scanner Z axis is not used.");
        }

        if (!string.IsNullOrWhiteSpace(axis.ConfigurationError))
        {
            return new ST_SCANNER_AXIS_STATUS_ITEM($"H{axis.HeadNo:00}", automationLabel, axis.AxisName, displayAxisReference, "NOT CONFIGURED", "-", "-", "-", "-", "-", "NO", "-", "NOT CONFIGURED", axis.ConfigurationError);
        }

        try
        {
            if (!automationManager.IsSimul(axis.AutomationNo) && !automationManager.IsConnect(axis.AutomationNo))
            {
                return new ST_SCANNER_AXIS_STATUS_ITEM($"H{axis.HeadNo:00}", automationLabel, axis.AxisName, displayAxisReference, "OFFLINE", "-", "-", "-", "-", "-", "-", "-", "OFFLINE", "Automation not connected.");
            }

            var snapshot = automationManager.StatusSnapshots.GetAxis(axisReference, axis.AutomationNo, axis.AioNo, axis.ReadExtendedStatus);
            if (snapshot.Value is not { } status)
                return new ST_SCANNER_AXIS_STATUS_ITEM($"H{axis.HeadNo:00}", automationLabel, axis.AxisName, displayAxisReference, snapshot.State, "-", "-", "-", "-", "-", "-", "-", snapshot.State, snapshot.Detail);
            return new ST_SCANNER_AXIS_STATUS_ITEM(
                $"H{axis.HeadNo:00}", 
                automationLabel, 
                axis.AxisName, 
                displayAxisReference, 
                status.Able ? "ABLE" : "DISABLE", 
                FormatScannerPosition(status.PositionFeedback), 
                FormatExtendedScannerPosition(status.AuxiliaryFeedback, axis.ReadExtendedStatus), 
                FormatExtendedScannerValue(status.CurrentFeedback, axis.ReadExtendedStatus), 
                FormatExtendedScannerValue(status.GalvoLaserOutputRaw, axis.ReadExtendedStatus), 
                FormatExtendedScannerTemperature(status.MotorTemperature, axis.ReadExtendedStatus), 
                status.HomeDone ? "OK" : "NO", 
                FormatFault(status.Fault), 
                status.HasError ? "ERROR" : "OK", 
                string.IsNullOrWhiteSpace(status.RawResponse) ? "-" : status.RawResponse
            );
        }
        catch (Exception exception)when (exception is not OperationCanceledException)
        {
            return new ST_SCANNER_AXIS_STATUS_ITEM($"H{axis.HeadNo:00}", automationLabel, axis.AxisName, displayAxisReference, "ERROR", "-", "-", "-", "-", "-", "-", "-", "ERROR", exception.Message);
        }
    }

    internal static IReadOnlyList<ST_SCANNER_AXIS_SETTING> BuildAxisDefinitions(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
        => CScannerAxisConfiguration.BuildAxisDefinitions(settings, headNo);

    internal static IReadOnlyList<ST_SCANNER_AXIS_SETTING> BuildAxisDefinitions(IReadOnlyDictionary<string, string> parameters, int headNo)
        => CScannerAxisConfiguration.BuildAxisDefinitions(parameters, headNo);

    internal static IReadOnlyList<int> BuildEnabledHeadNumbers(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headCount = 8)
        => CScannerAxisConfiguration.BuildEnabledHeadNumbers(settings, headCount);

    internal static bool IsHeadEnabled(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
        => CScannerAxisConfiguration.IsHeadEnabled(settings, headNo);

    internal static bool IsHeadEnabled(IReadOnlyDictionary<string, string> parameters, int headNo)
        => CScannerAxisConfiguration.IsHeadEnabled(parameters, headNo);

    private static IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> CreatePollingErrorSnapshot(string message)
    {
        return[new ST_SCANNER_AXIS_STATUS_ITEM("-", "-", "-", "-", "ERROR", "-", "-", "-", "-", "-", "-", "-", "ERROR", string.IsNullOrWhiteSpace(message) ? "Scanner status polling failed." : message)];
    }

    private void SetSnapshot(IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> snapshot)
    {
        lock (_snapshotLock)
        {
            _snapshot = snapshot;
        }
    }

    private void SetSnapshot(int headNo, IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> snapshot)
    {
        lock (_snapshotLock)
        {
            if (_selectedHeadNo != headNo)
            {
                return;
            }

            _snapshot = snapshot;
        }
    }

    private ST_SCANNER_STATUS_POLLING_CONTEXT ReadPollingContext()
    {
        lock (_snapshotLock)
        {
            return new ST_SCANNER_STATUS_POLLING_CONTEXT(_selectedHeadNo, _axisDefinitions);
        }
    }

    private static bool IsSameAxisDefinitions(IReadOnlyList<ST_SCANNER_AXIS_SETTING> left, IReadOnlyList<ST_SCANNER_AXIS_SETTING> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].Equals(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int NormalizeHeadNo(int headNo)
    {
        return Math.Clamp(headNo, 1, 8);
    }

    private static string FormatScannerPosition(double value)
    {
        if (!double.IsFinite(value))
        {
            return "-";
        }

        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string FormatExtendedScannerPosition(double value, bool readExtendedStatus)
    {
        return readExtendedStatus ? FormatScannerPosition(value) : "-";
    }

    private static string FormatScannerValue(double value)
    {
        if (!double.IsFinite(value))
        {
            return "-";
        }

        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string FormatExtendedScannerValue(double value, bool readExtendedStatus)
    {
        return readExtendedStatus ? FormatScannerValue(value) : "-";
    }

    private static string FormatScannerTemperature(double value)
    {
        return CGalvoTemperature.FormatCelsius(value);
    }

    private static string FormatExtendedScannerTemperature(double value, bool readExtendedStatus)
    {
        return readExtendedStatus ? FormatScannerTemperature(value) : "-";
    }

    private static string FormatFault(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private readonly record struct ST_SCANNER_STATUS_POLLING_CONTEXT(int HeadNo, IReadOnlyList<ST_SCANNER_AXIS_SETTING> AxisDefinitions);
}
