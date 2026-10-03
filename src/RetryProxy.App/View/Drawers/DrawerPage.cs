using RetryProxy.Service.I18n;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace RetryProxy.View.Drawers;

/// <summary>抽屉只编辑隔离草稿，保存委托成功后才由宿主关闭；校验错误留在原处。</summary>
public abstract class DrawerPage : UserControl, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private string? _error;
    private bool _busy;

    protected DrawerPage()
    {
        Lifetime = _lifetime.Token;
        I18nService.Instance.PropertyChanged += LanguageChanged;
    }

    public abstract string TitleKey { get; }
    public virtual string SaveButtonKey => "保存";
    public virtual bool IsReadOnly => false;
    public abstract bool HasChanges { get; }
    public CancellationToken Lifetime { get; }
    public Func<Task<string?>>? CommitAsync { get; set; }
    public event Action? StateChanged;
    public string ErrorText => _error is null ? string.Empty : DrawerText.Error(_error);
    public bool IsBusy
    {
        get => _busy;
        protected set { _busy = value; RaiseStateChanged(); }
    }

    public void SetError(string? error)
    {
        _error = error;
        RaiseStateChanged();
    }

    public async Task<bool> SaveAsync()
    {
        SetError(null);
        if (CommitAsync is null) return false;
        var error = await CommitAsync();
        SetError(error);
        return error is null;
    }

    protected void RaiseStateChanged() => StateChanged?.Invoke();
    protected virtual void OnLanguageChanged() { }
    private void LanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(I18nService.Revision)) return;
        OnLanguageChanged();
        RaiseStateChanged();
    }

    public virtual void CancelPendingOperations()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingOperations();
        I18nService.Instance.PropertyChanged -= LanguageChanged;
        _lifetime.Dispose();
    }
}
