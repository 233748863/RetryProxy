using RetryProxy.Core.Config;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RetryProxy.View.Drawers;

public partial class KeyDrawer : DrawerPage
{
    private readonly ProviderEndpoint _provider;
    private readonly bool _isNew;
    private readonly string _initialState;
    private bool _syncingSecret;
    private bool _syncingContext;
    private bool _overrideContext;

    public KeyDrawer(ProviderEndpoint provider, ProviderKey draft, bool isNew, bool nested)
    {
        _provider = provider;
        _isNew = isNew;
        Draft = draft.Clone();
        InitializeComponent();
        DraftHint.Visibility = nested ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Text = Draft.Name;
        SecretBox.Password = Draft.ApiKey;
        NotesBox.Text = Draft.Notes;
        ModelBox.ItemsSource = provider.FetchedModels.Concat(new[] { provider.Models.Model })
            .Where(model => !string.IsNullOrWhiteSpace(model)).Distinct().ToArray();
        ModelBox.Text = Draft.ModelOverride?.Model ?? string.Empty;
        _overrideContext = Draft.ModelOverride?.Context1M ?? provider.Models.Context1M;
        ContextCheck.Visibility = provider.ClientType == ClientType.Claude ? Visibility.Visible : Visibility.Collapsed;
        ContextCheck.Checked += (_, _) => RememberContext();
        ContextCheck.Unchecked += (_, _) => RememberContext();
        ModelBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateContext()));
        ModelBox.SelectionChanged += (_, _) => Dispatcher.InvokeAsync(UpdateContext);
        UpdateContext();
        _initialState = CaptureState();
    }

    public ProviderKey Draft { get; }
    public override string TitleKey => _isNew ? "添加 Key" : "编辑 Key";
    public override bool HasChanges => CaptureState() != _initialState;

    private string CaptureState() => DrawerText.Snapshot(new
    {
        NameBox.Text, Secret = SecretBox.Password, Model = ModelBox.Text,
        Context = _overrideContext, Notes = NotesBox.Text,
    });

    public string? ReadDraft()
    {
        Draft.Name = NameBox.Text.Trim();
        Draft.ApiKey = SecretBox.Password.Trim();
        Draft.Notes = NotesBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        Draft.ModelOverride = model.Length == 0 ? null : new KeyModelOverride
        {
            Model = model, Context1M = _provider.ClientType == ClientType.Claude && ContextCheck.IsChecked == true,
        };
        return DrawerText.ValidateKey(Draft, _provider, _isNew);
    }

    private void RememberContext()
    {
        if (!_syncingContext && ContextCheck.IsEnabled) _overrideContext = ContextCheck.IsChecked == true;
    }

    private void UpdateContext()
    {
        _syncingContext = true;
        ContextCheck.IsEnabled = !string.IsNullOrWhiteSpace(ModelBox.Text);
        ContextCheck.IsChecked = ContextCheck.IsEnabled ? _overrideContext : _provider.Models.Context1M;
        _syncingContext = false;
    }

    private void SecretChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSecret || VisibleSecretBox is null) return;
        _syncingSecret = true;
        VisibleSecretBox.Text = ShowSecretCheck?.IsChecked == true ? SecretBox.Password : string.Empty;
        _syncingSecret = false;
    }

    private void VisibleSecretChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSecret || SecretBox is null || ShowSecretCheck?.IsChecked != true) return;
        _syncingSecret = true;
        SecretBox.Password = VisibleSecretBox.Text;
        _syncingSecret = false;
    }

    private void SecretVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (SecretBox is null || VisibleSecretBox is null) return;
        var show = ShowSecretCheck.IsChecked == true;
        _syncingSecret = true;
        VisibleSecretBox.Text = show ? SecretBox.Password : string.Empty;
        SecretBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        VisibleSecretBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _syncingSecret = false;
    }

    public override void Dispose()
    {
        base.Dispose();
        // 隐藏密码编辑器不留第二份可选中的明文。
        _syncingSecret = true;
        SecretBox.Clear();
        VisibleSecretBox.Clear();
    }
}
