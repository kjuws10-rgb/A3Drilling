using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;
using System.Reflection;

namespace Drilling.Common.Interface;
internal static class CComm
{
    private static readonly IReadOnlyList<CCommRegistration> CommTypes = LoadCommTypes();
    public static CCommBase Create(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option)
    {
        var interfaceType = CCommTypeAttribute.NormalizeName(data.InterfaceType.ToString());
        var deviceName = CCommTypeAttribute.NormalizeName(data.Device.ToString());
        bool ShouldIncludeItem(global::Drilling.Common.Interface.CComm.CCommRegistration item)
        {
            return item.IsMatch(interfaceType, deviceName);
        }

        int SelectSortKey(global::Drilling.Common.Interface.CComm.CCommRegistration item)
        {
            return item.DeviceNames.Count;
        }

        var commType = CommTypes.Where(ShouldIncludeItem).OrderByDescending(SelectSortKey).FirstOrDefault();
        if (commType is null)
        {
            return new CReadyOnlyComm(data, option);
        }

        return Activator.CreateInstance(commType.CommType, data, option) as CCommBase ?? throw new InvalidOperationException($"Interface communication creation failed: {data.InterfaceType}/{data.Device}");
    }

    private static IReadOnlyList<CCommRegistration> LoadCommTypes()
    {
        bool ShouldIncludeItem(global::System.Type type)
        {
            return !type.IsAbstract && typeof(CCommBase).IsAssignableFrom(type);
        }

        global::System.Collections.Generic.IEnumerable<global::Drilling.Common.Interface.CComm.CCommRegistration> BuildCCommRegistration(global::System.Type type)
        {
            global::Drilling.Common.Interface.CComm.CCommRegistration BuildCCommRegistration(global::Drilling.Common.Interface.CCommTypeAttribute attribute)
            {
                return new CCommRegistration(attribute.InterfaceType, attribute.DeviceNames, type);
            }

            return type.GetCustomAttributes<CCommTypeAttribute>().Select(BuildCCommRegistration);
        }

        return typeof(CCommBase).Assembly.GetTypes().Where(ShouldIncludeItem).SelectMany(BuildCCommRegistration).ToArray();
    }

    private sealed record CCommRegistration(string InterfaceType, IReadOnlyList<string> DeviceNames, Type CommType)
    {
        public bool IsMatch(string interfaceType, string deviceName)
        {
            return InterfaceType.Equals(interfaceType, StringComparison.OrdinalIgnoreCase) && (DeviceNames.Count == 0 || DeviceNames.Contains(deviceName, StringComparer.OrdinalIgnoreCase));
        }
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class CCommTypeAttribute(string interfaceType, params string[] deviceNames) : Attribute
{
    public string InterfaceType { get; } = NormalizeName(interfaceType);
    public IReadOnlyList<string> DeviceNames { get; } = deviceNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(NormalizeName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string NormalizeName(string value)
    {
        return value.Trim().ToUpperInvariant().Replace("_", "", StringComparison.OrdinalIgnoreCase).Replace("-", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ST_COMM_RECEIVED_MESSAGE(string RemoteEndPoint, string Message, DateTimeOffset ReceivedAt);
internal abstract class CCommBase(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option)
{
    protected readonly ST_INTERFACE_DATA Data = data;
    protected readonly ST_INTERFACE_CONNECT_OPTION Option = option;
    public EN_COMM_STATE ConnectionState { get; protected set; } = EN_COMM_STATE.Offline;

    public string Endpoint
    {
        get
        {
            return Option.Endpoint;
        }
    }

    public string LastSent { get; protected set; } = "";
    public string LastReceived { get; protected set; } = "";
    public string LastError { get; protected set; } = "";
    public DateTimeOffset? LastChangedAt { get; protected set; }

    public abstract Task Connect(CancellationToken cancellationToken = default);
    public virtual void ResetAutoReconnectPolicy()
    {
    }

    public virtual void SuppressAutoReconnect()
    {
    }

    public virtual Task Disconnect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(EN_COMM_STATE.Offline);
        return Task.CompletedTask;
    }

    public abstract Task<string> Execute(string function, CancellationToken cancellationToken = default);
    protected void SetState(EN_COMM_STATE state)
    {
        ConnectionState = state;
        LastChangedAt = DateTimeOffset.Now;
    }

    protected void SetError(Exception ex)
    {
        LastError = ex.Message;
        SetState(EN_COMM_STATE.Offline);
    }

    protected void SetError(string message)
    {
        LastError = message;
        SetState(EN_COMM_STATE.Offline);
    }

    protected void SetCommandError(string message)
    {
        LastError = message;
    }
}

internal sealed class CReadyOnlyComm(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CCommBase(data, option)
{
    public override Task Connect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetError($"Live channel is not ready for {Data.InterfaceType}.");
        return Task.CompletedTask;
    }

    public override Task<string> Execute(string function, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSent = function;
        LastReceived = "";
        SetError($"Live command is not ready for {Data.InterfaceType}.");
        return Task.FromResult("");
    }
}
