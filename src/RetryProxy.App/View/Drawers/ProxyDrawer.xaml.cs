using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Drawers;

public sealed record KeepAliveDraft(bool Enabled, double IdleMinutes, long ContextLimit, ReasoningEffort Effort);

public partial class ProxyDrawer : DrawerPage
{
    private readonly string _initialState;
    private readonly ClientType _client;
    private ServiceState _state;

    public ProxyDrawer(ProxyRoute route)
    {
        RouteId = route.Id;
        _client = route.ClientType;
        InitializeComponent();
        ClientText.Text = route.ClientType.Label();
        PortBox.Text = route.ListenPort.ToString(CultureInfo.InvariantCulture);
        RetriesBox.Text = route.MaxRetries.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = Number(route.TimeoutSeconds);
        TotalTimeoutBox.Text = Number(route.TotalTimeoutSeconds);
        GenerationTimeoutBox.Text = Number(route.GenerationTimeoutSeconds);
        BaseDelayBox.Text = Number(route.BaseDelaySeconds);
        MaxDelayBox.Text = Number(route.MaxDelaySeconds);
        CompressionCheck.IsChecked = route.PassThroughCompression;
        KeepAliveCheck.IsChecked = route.KeepaliveEnabled;
        IdleMinutesBox.Text = Number(route.KeepaliveIdleMinutes);
        ContextLimitBox.Text = route.KeepaliveContextLimit.ToString(CultureInfo.InvariantCulture);
        foreach (var effort in ReasoningEffortExtensions.AvailableFor(route.ClientType))
        {
            var item = new ComboBoxItem { Tag = effort };
            DrawerText.Bind(item, ContentControl.ContentProperty, effort.Label());
            EffortBox.Items.Add(item);
        }
        EffortBox.SelectedValue = route.KeepaliveReasoningEffort;
        _initialState = CaptureState();
    }

    public string RouteId { get; }
    public Func<Task<string?>>? ToggleAsync { get; set; }
    public override string TitleKey => "代理设置";
    public override bool HasChanges => CaptureState() != _initialState;

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private string CaptureState() => DrawerText.Snapshot(new
    {
        Port = PortBox.Text, Retries = RetriesBox.Text, Timeout = TimeoutBox.Text, Total = TotalTimeoutBox.Text,
        Generation = GenerationTimeoutBox.Text, Base = BaseDelayBox.Text, Max = MaxDelayBox.Text,
        Compression = CompressionCheck.IsChecked, KeepAlive = KeepAliveCheck.IsChecked,
        Idle = IdleMinutesBox.Text, Context = ContextLimitBox.Text, Effort = EffortBox.SelectedValue,
    });

    /// <summary>先验证全部参数，不改 Workspace；端口确认、停止完成后再次调用，使用最新的供应商与 Key。</summary>
    public string? TryRead(ProxyWorkspace workspace, out RouteEditor? editor, out KeepAliveDraft? keepAlive)
    {
        editor = null;
        keepAlive = null;
        var index = workspace.Config.Routes.FindIndex(route => route.Id == RouteId);
        if (index < 0) return "找不到转发通道";
        var current = workspace.Config.Routes[index];
        if (!double.TryParse(IdleMinutesBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var idle)
            || !double.IsFinite(idle) || idle < ConfigDefaults.MinKeepaliveIdleMinutes || idle > ConfigDefaults.MaxKeepaliveIdleMinutes)
            return "保活间隔必须在 0.5 到 1440 分钟之间";
        if (!long.TryParse(ContextLimitBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
            return "保活会话上限必须是正整数";
        if (EffortBox.SelectedValue is not ReasoningEffort effort || !effort.IsSupportedBy(_client))
            return "该客户端不支持所选保活思考强度，请重新选择";
        // OpenRouteEditor 会取页面当前选中的供应商；参数编辑必须固定为此刻通道的供应商。
        editor = new RouteEditor
        {
            Index = index, Name = current.Name, ClientType = current.ClientType,
            Provider = current.CurrentProviderId,
            ProviderName = workspace.Config.ProviderById(current.CurrentProviderId)?.Name ?? string.Empty,
            ProviderChanged = false,
            Port = PortBox.Text, Retries = RetriesBox.Text, Timeout = TimeoutBox.Text,
            TotalTimeout = TotalTimeoutBox.Text, GenerationTimeout = GenerationTimeoutBox.Text,
            BaseDelay = BaseDelayBox.Text, MaxDelay = MaxDelayBox.Text,
            PassThroughCompression = CompressionCheck.IsChecked == true,
            KeepaliveEnabled = KeepAliveCheck.IsChecked == true,
            KeepaliveIdleMinutes = idle,
            KeepaliveContextLimit = limit,
            KeepaliveReasoningEffort = effort,
        };
        keepAlive = new KeepAliveDraft(KeepAliveCheck.IsChecked == true, idle, limit, effort);
        try
        {
            var route = workspace.RouteFromEditor(editor);
            route.KeepaliveEnabled = keepAlive.Enabled;
            route.KeepaliveIdleMinutes = idle;
            route.KeepaliveContextLimit = limit;
            route.KeepaliveReasoningEffort = effort;
            route.Validate();
            var candidate = workspace.Config.Clone();
            candidate.Routes[index] = route;
            candidate.Normalize().Validate(false);
        }
        catch (WorkspaceException error) { return error.Message; }
        catch (ConfigException error) { return error.Message; }
        return null;
    }

    public void RefreshRuntime(ServiceState state)
    {
        _state = state;
        StateText.Text = DrawerText.T(state switch
        {
            ServiceState.Running => "代理运行中",
            ServiceState.Starting => "代理正在启动…",
            ServiceState.Stopping => "代理正在停止…",
            ServiceState.Error => "代理异常，请检查运行日志",
            _ => "代理已停止",
        });
        ToggleButton.Content = DrawerText.T(state == ServiceState.Running ? "停止代理" : "启动代理");
        ToggleButton.Appearance = state == ServiceState.Running ? ControlAppearance.Danger : ControlAppearance.Secondary;
        ToggleButton.IsEnabled = !IsBusy && state is ServiceState.Running or ServiceState.Stopped or ServiceState.Error;
    }

    private async void ToggleClicked(object sender, RoutedEventArgs e)
    {
        if (IsBusy || ToggleAsync is null) return;
        IsBusy = true;
        RefreshRuntime(_state);
        try { SetError(await ToggleAsync()); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception) { SetError("代理操作失败，请检查运行日志"); }
        finally
        {
            IsBusy = false;
            RefreshRuntime(_state);
        }
    }

    protected override void OnLanguageChanged() => RefreshRuntime(_state);
}
