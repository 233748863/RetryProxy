using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RetryProxy.Core.Balance;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public sealed class BalanceManagementTests
{
    private const string ProviderId = "provider";
    private const string KeyId = "query-key";

    // 只使用临时日志目录与内存保存回调；所有通道关闭，不读取客户端配置、不启动应用或监听端口。
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        public ProxyLogger Logger { get; }
        public ProxyWorkspace App { get; }
        public bool FailSave { get; set; }
        public Action<ProxyConfig>? BeforeSave { get; set; }
        public ProxyConfig? Saved { get; private set; }
        public List<string> Events { get; } = new();
        public ProviderEndpoint Provider => App.Config.ProviderById(ProviderId)!;

        public Fixture()
        {
            Logger = ProxyLogger.Create(_directory);
            var config = ProxyConfig.Builtin();
            foreach (var route in config.Routes)
            {
                route.DesiredRunning = false;
                route.KeepaliveEnabled = false;
            }
            config.Providers.Clear();
            config.Providers.Add(new ProviderEndpoint
            {
                Id = ProviderId,
                Name = "测试供应商",
                ClientType = ClientType.Codex,
                BaseUrl = "https://balance.example/v1",
                BalanceQuery = new BalanceQuery { Mode = BalanceQueryMode.Auto },
                Keys = new List<ProviderKey>
                {
                    new() { Id = KeyId, Name = "查询 Key", ApiKey = "sk-balance-query-secret" },
                    new() { Id = "current-key", Name = "当前 Key", ApiKey = "sk-balance-current-secret" },
                },
            });
            var codex = config.RouteFor(ClientType.Codex)!;
            codex.CurrentProviderId = ProviderId;
            codex.CurrentKeyId = "current-key";
            config.SelectedRouteId = codex.Id;
            App = new ProxyWorkspace(Logger, config, candidate =>
            {
                Events.Add("save");
                BeforeSave?.Invoke(candidate);
                if (FailSave) throw new IOException("模拟写入失败：sk-balance-query-secret，https://balance.example/v1");
                Saved = candidate.Clone();
            });
            App.SetUiNotifier(() => Events.Add("notify"));
        }

        public BalanceRequest Request(BalanceQueryMode? mode = null)
        {
            var query = Provider.BalanceQuery.Clone();
            if (mode is { } requestedMode) query.Mode = requestedMode;
            return new BalanceRequest
            {
                Key = new BalanceKey(ProviderId, KeyId),
                ClientType = Provider.ClientType,
                BaseUrl = Provider.BaseUrl,
                ApiKey = Provider.KeyById(KeyId)!.ApiKey,
                Query = query,
            };
        }

        public void Dispose()
        {
            App.SetUiNotifier(null);
            foreach (var service in App.Services.Values) service.Stop(TimeSpan.FromSeconds(1));
            Logger.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(BalanceQueryMode.Usage)]
    [InlineData(BalanceQueryMode.UserBalance)]
    [InlineData(BalanceQueryMode.OpenAiBilling)]
    public void RememberDetectionSavesThenPublishesOnlyTheDetectedModeWithoutChangingTheCurrentKey(BalanceQueryMode mode)
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        // 名称和备注不是查询来源，打开查询后修改这些字段仍可保存识别结果。
        fixture.Provider.Name = "查询期间修改的名称";
        fixture.Provider.Notes = "保留备注";
        var before = fixture.App.Config;
        var expected = before.Clone();
        expected.ProviderById(ProviderId)!.BalanceQuery.Detected = mode;
        var selected = (fixture.App.SelectedClient, fixture.App.SelectedProvider, fixture.App.SelectedRoute);
        fixture.BeforeSave = _ =>
        {
            Assert.Same(before, fixture.App.Config);
            Assert.Null(fixture.Provider.BalanceQuery.Detected);
        };

        Assert.Null(fixture.App.RememberBalanceDetection(request, mode));
        Assert.Equal(new[] { "save", "notify" }, fixture.Events);
        Assert.Equal(expected, fixture.Saved);
        Assert.Equal(expected, fixture.App.Config);
        Assert.NotSame(before, fixture.App.Config);
        Assert.Equal(selected, (fixture.App.SelectedClient, fixture.App.SelectedProvider, fixture.App.SelectedRoute));
        Assert.Equal("current-key", fixture.App.Config.RouteFor(ClientType.Codex)!.CurrentKeyId);
        Assert.Empty(fixture.App.Services);
        Assert.Empty(fixture.App.RouteKeepAlives);
        Assert.Null(request.Query.Detected);
    }

    [Theory]
    [InlineData("provider-deleted")]
    [InlineData("key-deleted")]
    [InlineData("client")]
    [InlineData("url")]
    [InlineData("key")]
    [InlineData("provider-mode")]
    [InlineData("request-mode")]
    public void RememberDetectionIgnoresResultsWhoseSourceNoLongerMatches(string change)
    {
        using var fixture = new Fixture();
        var request = fixture.Request(change == "request-mode" ? BalanceQueryMode.Usage : null);
        switch (change)
        {
            case "provider-deleted": fixture.App.Config.Providers.Clear(); break;
            case "key-deleted": fixture.Provider.Keys.RemoveAll(key => key.Id == KeyId); break;
            case "client": fixture.Provider.ClientType = ClientType.Claude; break;
            case "url": fixture.Provider.BaseUrl = "https://replacement.example/v1"; break;
            case "key": fixture.Provider.KeyById(KeyId)!.ApiKey = "sk-replacement-secret"; break;
            case "provider-mode": fixture.Provider.BalanceQuery.Mode = BalanceQueryMode.UserBalance; break;
            case "request-mode": break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        var before = fixture.App.Config.Clone();

        Assert.Null(fixture.App.RememberBalanceDetection(request, BalanceQueryMode.Usage));
        Assert.Equal(before, fixture.App.Config);
        Assert.Empty(fixture.Events);
        Assert.Null(fixture.Saved);
    }

    [Fact]
    public void AlreadyRememberedModeIsANoOpEvenWhenTheRequestHasAnOlderDetectionSnapshot()
    {
        using var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Provider.BalanceQuery.Detected = BalanceQueryMode.UserBalance;
        var before = fixture.App.Config;
        Assert.Null(fixture.App.RememberBalanceDetection(request, BalanceQueryMode.UserBalance));
        Assert.Same(before, fixture.App.Config);
        Assert.Equal(BalanceQueryMode.UserBalance, fixture.Provider.BalanceQuery.Detected);
        Assert.Empty(fixture.Events);
        Assert.Null(fixture.Saved);
    }

    [Theory]
    [InlineData(BalanceQueryMode.Auto)]
    [InlineData(BalanceQueryMode.None)]
    [InlineData((BalanceQueryMode)12345)]
    public void RememberDetectionRejectsNonConcreteOrUndefinedModesWithoutSaving(BalanceQueryMode mode)
    {
        using var fixture = new Fixture();
        var before = fixture.App.Config.Clone();
        Assert.Equal("余额识别方式无效", fixture.App.RememberBalanceDetection(fixture.Request(), mode));
        Assert.Equal(before, fixture.App.Config);
        Assert.Empty(fixture.Events);
        Assert.Null(fixture.Saved);
    }

    [Fact]
    public void FailedDetectionSaveReturnsASafeMessageAndDoesNotPublishCandidateOrNotify()
    {
        using var fixture = new Fixture();
        fixture.Provider.BalanceQuery.Detected = BalanceQueryMode.Usage;
        var request = fixture.Request();
        var before = fixture.App.Config;
        var expected = before.Clone();
        fixture.FailSave = true;
        fixture.BeforeSave = candidate =>
        {
            Assert.Equal(BalanceQueryMode.UserBalance, candidate.ProviderById(ProviderId)!.BalanceQuery.Detected);
            Assert.Same(before, fixture.App.Config);
        };

        var error = fixture.App.RememberBalanceDetection(request, BalanceQueryMode.UserBalance);
        Assert.Equal("已查询余额，但识别方式保存失败", error);
        Assert.DoesNotContain(request.ApiKey, error!);
        Assert.DoesNotContain(request.BaseUrl, error!);
        Assert.Same(before, fixture.App.Config);
        Assert.Equal(expected, fixture.App.Config);
        Assert.Null(fixture.Saved);
        Assert.Equal(new[] { "save" }, fixture.Events);
    }

    [Fact]
    public void SavingAChangedProviderUrlClearsDetectionWithoutMutatingTheDraft()
    {
        using var fixture = new Fixture();
        fixture.Provider.BalanceQuery.Detected = BalanceQueryMode.UserBalance;
        var draft = fixture.Provider.Clone();
        draft.BaseUrl = "https://replacement.example/v1/";
        var draftBefore = draft.Clone();

        Assert.Null(fixture.App.SaveProvider(draft, false));
        Assert.Equal("https://replacement.example/v1", fixture.Provider.BaseUrl);
        Assert.Null(fixture.Provider.BalanceQuery.Detected);
        Assert.Null(fixture.Saved!.ProviderById(ProviderId)!.BalanceQuery.Detected);
        Assert.Equal(draftBefore, draft);
    }

    [Theory]
    [InlineData(BalanceQueryMode.Auto, BalanceQueryMode.Usage)]
    [InlineData(BalanceQueryMode.Auto, BalanceQueryMode.None)]
    [InlineData(BalanceQueryMode.Usage, BalanceQueryMode.Auto)]
    public void SavingAChangedQueryModeClearsDetectionIncludingStaleDraftDetection(BalanceQueryMode oldMode, BalanceQueryMode newMode)
    {
        using var fixture = new Fixture();
        fixture.Provider.BalanceQuery.Mode = oldMode;
        fixture.Provider.BalanceQuery.Detected = oldMode == BalanceQueryMode.Auto ? BalanceQueryMode.UserBalance : null;
        var draft = fixture.Provider.Clone();
        draft.BalanceQuery.Mode = newMode;
        draft.BalanceQuery.Detected = BalanceQueryMode.OpenAiBilling;

        Assert.Null(fixture.App.SaveProvider(draft, false));
        Assert.Equal(newMode, fixture.Provider.BalanceQuery.Mode);
        Assert.Null(fixture.Provider.BalanceQuery.Detected);
        Assert.Null(fixture.Saved!.ProviderById(ProviderId)!.BalanceQuery.Detected);
        Assert.Equal(BalanceQueryMode.OpenAiBilling, draft.BalanceQuery.Detected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(BalanceQueryMode.Usage)]
    public void AnOpenDraftCannotOverwriteNewAsyncDetectionWhenTheQuerySourceIsUnchanged(BalanceQueryMode? oldDetection)
    {
        using var fixture = new Fixture();
        fixture.Provider.BalanceQuery.Detected = oldDetection;
        var draft = fixture.Provider.Clone();
        Assert.Null(fixture.App.RememberBalanceDetection(fixture.Request(), BalanceQueryMode.UserBalance));
        Assert.Equal(BalanceQueryMode.UserBalance, fixture.Provider.BalanceQuery.Detected);
        fixture.Events.Clear();
        draft.Name = "用户保存的新名称";
        draft.Notes = "草稿备注";
        draft.BaseUrl += "/";

        Assert.Null(fixture.App.SaveProvider(draft, false));
        Assert.Equal(BalanceQueryMode.UserBalance, fixture.Provider.BalanceQuery.Detected);
        Assert.Equal(BalanceQueryMode.UserBalance, fixture.Saved!.ProviderById(ProviderId)!.BalanceQuery.Detected);
        Assert.Equal(("用户保存的新名称", "草稿备注", "https://balance.example/v1"),
            (fixture.Provider.Name, fixture.Provider.Notes, fixture.Provider.BaseUrl));
        Assert.Equal(oldDetection, draft.BalanceQuery.Detected);
        Assert.Single(fixture.Events, item => item == "save");
    }

    [Fact]
    public void FailedProviderSaveDoesNotClearTheLatestDetectionOrPublishTheChangedUrl()
    {
        using var fixture = new Fixture();
        var draft = fixture.Provider.Clone();
        Assert.Null(fixture.App.RememberBalanceDetection(fixture.Request(), BalanceQueryMode.UserBalance));
        var before = fixture.App.Config;
        var expected = before.Clone();
        var lastSaved = fixture.Saved;
        fixture.Events.Clear();
        fixture.FailSave = true;
        draft.BaseUrl = "https://replacement.example/v1";

        Assert.Contains("保存失败", fixture.App.SaveProvider(draft, false));
        Assert.Same(before, fixture.App.Config);
        Assert.Equal(expected, fixture.App.Config);
        Assert.Same(lastSaved, fixture.Saved);
        Assert.Equal(new[] { "save" }, fixture.Events);
    }
}
