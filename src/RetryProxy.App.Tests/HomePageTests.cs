using RetryProxy.View.Pages;
using RetryProxy.ViewModel.Pages;
using System;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed class HomePageTests
{
    private static readonly XNamespace Ui = "http://schemas.lepo.co/wpfui/2022/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void HomeIsFirstNavigationItemAndKeepsItsPageCached()
    {
        var menu = Load("MainWindow.xaml").Descendants(Ui + "NavigationView.MenuItems").Single();
        var home = menu.Elements(Ui + "NavigationViewItem").First();
        Assert.Equal("HomeNavigation", (string?)home.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("{x:Type pages:HomePage}", (string?)home.Attribute("TargetPageType"));
        Assert.Equal("Enabled", (string?)home.Attribute("NavigationCacheMode"));
    }

    [Theory]
    [InlineData("HomeProvidersCard", typeof(ProviderPage))]
    [InlineData("HomeStatisticsCard", typeof(StatisticsPage))]
    [InlineData("HomePreparationCard", typeof(PreparationPage))]
    [InlineData("HomeDiagnosticsCard", typeof(RequestDiagnosticsPage))]
    [InlineData("HomeLogsCard", typeof(LogPage))]
    [InlineData("HomeSettingsCard", typeof(SettingsPage))]
    public void ShortcutTargetsExistingSidebarPageThroughSharedNavigation(string id, Type pageType)
    {
        // 检查实际页面，防止卡片接回已删除的通道页或独立缓存页。
        var document = Load("HomePage.xaml");
        var card = document.Descendants(Ui + "CardAction")
            .Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == id);
        Assert.Equal($"{{x:Type views:{pageType.Name}}}", (string?)card.Attribute("CommandParameter"));
        Assert.Equal("{StaticResource ApplicationCard}", (string?)card.Attribute("Style"));
        Assert.Contains(Load("MainWindow.xaml").Descendants(Ui + "NavigationViewItem"),
            item => (string?)item.Attribute("TargetPageType") == $"{{x:Type pages:{pageType.Name}}}");
        var style = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "ApplicationCard");
        Assert.Contains(style.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Command"
                && (string?)setter.Attribute("Value") == "{Binding NavigateCommand}");
    }

    [Fact]
    public void HomeKeepsBannerAndAdaptiveGridWithUniqueShortcuts()
    {
        var document = Load("HomePage.xaml");
        Assert.Single(document.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "HomeBanner");
        // 改版后运行状态卡里也有 UniformGrid，这里按“包含快捷卡片的网格”定位。
        var grid = document.Descendants(Presentation + "UniformGrid")
            .Single(element => element.Elements(Ui + "CardAction").Any());
        Assert.Contains("AdaptiveUniformGridColumnsConverter", (string?)grid.Attribute("Columns"));
        var cards = grid.Elements(Ui + "CardAction").ToArray();
        Assert.Equal(6, cards.Length);
        Assert.Equal(cards.Length, cards.Select(card => (string?)card.Attribute("CommandParameter")).Distinct().Count());
    }

    [Fact]
    public void HomeStatusCardShowsStateProviderKeyAndTodayTiles()
    {
        var document = Load("HomePage.xaml");
        var card = document.Descendants(Ui + "Card")
            .Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "HomeStatusCard");
        var badge = card.Descendants(Ui + "InfoBadge").Single();
        Assert.Equal("{Binding StateSeverity}", (string?)badge.Attribute("Severity"));
        Assert.Equal("{Binding StateLabel}", (string?)badge.Attribute("Value"));
        var channels = card.Descendants(Presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "HomeChannelList");
        Assert.Equal("{Binding Channels}", (string?)channels.Attribute("ItemsSource"));
        var channelRow = channels.Descendants(Presentation + "DataTemplate").Single()
            .Descendants(Presentation + "TextBlock").ToArray();
        Assert.Equal(2, channelRow.Length);
        Assert.Equal("{Binding Label}", (string?)channelRow[0].Attribute("Text"));
        Assert.Equal("{Binding ProviderKey}", (string?)channelRow[1].Attribute("Text"));
        var tiles = card.Descendants(Presentation + "UniformGrid").Single();
        Assert.Contains("AdaptiveUniformGridColumnsConverter", (string?)tiles.Attribute("Columns"));
        var tileIds = tiles.Descendants(Presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("AutomationProperties.AutomationId"))
            .Where(id => id is not null)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "HomeTotalRequests",
                "HomeSuccessfulRequests",
                "HomeRetryCount",
                "HomeFailedRequests",
                "HomeActiveRequests",
                "HomeSuccessRate",
            },
            tileIds);
    }

    [Theory]
    [InlineData(0UL, 0UL, "—")]
    [InlineData(10UL, 0UL, "100.00%")]
    [InlineData(9UL, 1UL, "90.00%")]
    [InlineData(1UL, 2UL, "33.33%")]
    public void SuccessRateShowsFinalRatioOfCompletedRequests(ulong successful, ulong failed, string expected)
    {
        Assert.Equal(expected, HomePageViewModel.FormatSuccessRate(successful, failed));
    }

    private static XDocument Load(string name)
    {
        using var source = typeof(HomePageTests).Assembly.GetManifestResourceStream(name)!;
        return XDocument.Load(source);
    }
}
