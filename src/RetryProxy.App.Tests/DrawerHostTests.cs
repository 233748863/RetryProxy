using RetryProxy.View.Controls;
using RetryProxy.View.Drawers;
using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed class DrawerHostTests
{
    [Fact]
    public void EmptyHostAllowsNavigation() => Run(async () =>
    {
        Assert.True(await new DrawerHost().TryCloseAllAsync());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanOrReadOnlyDrawerClosesWithoutConfirmation(bool readOnly) => Run(async () =>
    {
        var host = new DrawerHost { ConfirmDiscardAsync = () => throw new InvalidOperationException() };
        using var page = new TestDrawer { ReadOnly = readOnly, Changed = readOnly };
        page.SetBusy(readOnly);
        var shown = await Open(host, page);
        Assert.True(await host.TryCloseAllAsync());
        Assert.False(host.IsOpen);
        Assert.Equal(Visibility.Collapsed, host.Visibility);
        Assert.True(page.Lifetime.IsCancellationRequested);
        Assert.False(await shown);
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CancelNavigationPreservesParentAndChild(bool parentChanged, bool childChanged) => Run(async () =>
    {
        var confirmations = 0;
        var host = new DrawerHost { ConfirmDiscardAsync = () => { confirmations++; return Task.FromResult(false); } };
        using var parent = new TestDrawer { Changed = parentChanged };
        using var child = new TestDrawer { Changed = childChanged };
        var parentShown = await Open(host, parent);
        var childShown = await Open(host, child);
        child.Input.Text = "尚未保存的 Key 名称";
        child.SetError("请填写 API Key");
        Assert.False(await host.TryCloseAllAsync());
        Assert.Equal(1, confirmations);
        Assert.Same(child, ((ContentControl)host.FindName("Body")).Content);
        Assert.Equal("尚未保存的 Key 名称", child.Input.Text);
        Assert.Equal("请填写 API Key", child.ErrorText);
        Assert.False(parent.Lifetime.IsCancellationRequested);
        Assert.False(child.Lifetime.IsCancellationRequested);
        Assert.False(parentShown.IsCompleted);
        Assert.False(childShown.IsCompleted);
        Assert.True(((ContentControl)host.FindName("Body")).IsEnabled);
        host.ConfirmDiscardAsync = () => Task.FromResult(true);
        Assert.True(await host.TryCloseAllAsync());
    });

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    public void DiscardNavigationClosesAllLayersWithOneConfirmation(bool parentChanged, bool childChanged, int expected) => Run(async () =>
    {
        var confirmations = 0;
        var host = new DrawerHost { ConfirmDiscardAsync = () => { confirmations++; return Task.FromResult(true); } };
        using var parent = new TestDrawer { Changed = parentChanged };
        using var child = new TestDrawer { Changed = childChanged };
        var parentShown = await Open(host, parent);
        var childShown = await Open(host, child);
        Assert.True(await host.TryCloseAllAsync());
        Assert.Equal(expected, confirmations);
        Assert.False(host.IsOpen);
        Assert.Null(((ContentControl)host.FindName("Body")).Content);
        Assert.True(parent.Lifetime.IsCancellationRequested);
        Assert.True(child.Lifetime.IsCancellationRequested);
        Assert.False(await parentShown);
        Assert.False(await childShown);
    });

    [Fact]
    public void OrdinaryCloseStillReturnsToUnchangedParentDraft() => Run(async () =>
    {
        var host = new DrawerHost { ConfirmDiscardAsync = () => throw new InvalidOperationException() };
        using var parent = new TestDrawer { Changed = true };
        using var child = new TestDrawer();
        var parentShown = await Open(host, parent);
        var childShown = await Open(host, child);
        await host.RequestCloseAsync();
        Assert.True(host.IsOpen);
        Assert.Same(parent, ((ContentControl)host.FindName("Body")).Content);
        Assert.False(parent.Lifetime.IsCancellationRequested);
        Assert.False(parentShown.IsCompleted);
        Assert.False(await childShown);
        host.ConfirmDiscardAsync = () => Task.FromResult(true);
        Assert.True(await host.TryCloseAllAsync());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyDraftInAnyLayerBlocksNavigation(bool busyParent) => Run(async () =>
    {
        var host = new DrawerHost();
        using var parent = new TestDrawer();
        using var child = new TestDrawer();
        await Open(host, parent);
        await Open(host, child);
        var busy = busyParent ? parent : child;
        busy.SetBusy(true);
        Assert.False(await host.TryCloseAllAsync());
        Assert.False(parent.Lifetime.IsCancellationRequested);
        Assert.False(child.Lifetime.IsCancellationRequested);
        busy.SetBusy(false);
        Assert.True(await host.TryCloseAllAsync());
    });

    [Fact]
    public void ConfirmationRejectsRepeatedNavigationCloseSaveAndNestedOpen() => Run(async () =>
    {
        var answer = new TaskCompletionSource<bool>();
        var confirmations = 0;
        var saves = 0;
        var host = new DrawerHost { ConfirmDiscardAsync = () => { confirmations++; return answer.Task; } };
        using var page = new TestDrawer { Changed = true, CommitAsync = () => { saves++; return Task.FromResult<string?>(null); } };
        await Open(host, page);
        var closing = host.TryCloseAllAsync();
        Assert.False(closing.IsCompleted);
        Assert.False(await host.TryCloseAllAsync());
        await host.RequestCloseAsync();
        using var nested = new TestDrawer();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ShowAsync(nested));
        ((Wpf.Ui.Controls.Button)host.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(0, saves);
        Assert.Equal(1, confirmations);
        answer.SetResult(false);
        Assert.False(await closing);
        Assert.False(page.Lifetime.IsCancellationRequested);
        host.ConfirmDiscardAsync = () => Task.FromResult(true);
        Assert.True(await host.TryCloseAllAsync());
    });

    [Fact]
    public void FailedConfirmationKeepsDraftAndAllowsRetry() => Run(async () =>
    {
        var host = new DrawerHost { ConfirmDiscardAsync = () => throw new InvalidOperationException("private details") };
        using var page = new TestDrawer { Changed = true };
        await Open(host, page);
        Assert.False(await host.TryCloseAllAsync());
        Assert.Equal("无法显示确认，请稍后重试", page.ErrorText);
        Assert.False(page.Lifetime.IsCancellationRequested);
        host.ConfirmDiscardAsync = () => Task.FromResult(true);
        Assert.True(await host.TryCloseAllAsync());
    });

    [Fact]
    public void AnotherModalDialogBlocksNavigation() => Run(async () =>
    {
        var host = new DrawerHost();
        using var page = new TestDrawer();
        await Open(host, page);
        using (host.SuspendFocusConstraint())
            Assert.False(await host.TryCloseAllAsync());
        Assert.True(await host.TryCloseAllAsync());
    });

    private sealed class TestDrawer : DrawerPage
    {
        public TextBox Input { get; } = new();
        public TestDrawer() => Content = Input;
        public bool Changed { get; init; }
        public bool ReadOnly { get; init; }
        public override string TitleKey => "测试草稿";
        public override bool HasChanges => Changed;
        public override bool IsReadOnly => ReadOnly;
        public void SetBusy(bool busy) => IsBusy = busy;
    }

    // 在真实 WPF Dispatcher 上运行异步动画，不通过修改私有状态跳过关闭生命周期。
    private static async Task<Task<bool>> Open(DrawerHost host, TestDrawer page)
    {
        var shown = host.ShowAsync(page);
        var watch = Stopwatch.StartNew();
        while (!((Wpf.Ui.Controls.Button)host.FindName("CloseButton")).IsEnabled)
        {
            if (shown.IsFaulted) await shown;
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "抽屉打开动画未完成");
            await Task.Delay(10);
        }
        return shown;
    }

    private static void Run(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await test(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "抽屉测试超时");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
