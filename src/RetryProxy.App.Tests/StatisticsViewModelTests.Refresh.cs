using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using RetryProxy.Core.Config;
using RetryProxy.ViewModel.Pages;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class StatisticsViewModelTests
{
    [Fact]
    public void UnchangedRefreshKeepsComponentsAndSendsNoPresentationChanges() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        var chart = view.Chart;
        var months = view.HeatMonths;
        var changes = new List<string>();
        foreach (var row in PresentationRows(view)) row.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);
        foreach (var collection in PresentationCollections(view)) collection.CollectionChanged += (_, e) => changes.Add(e.Action.ToString());
        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(view.IsLoading) or nameof(view.ShowLoading) or nameof(view.UpdatedCaption)
                or nameof(view.CanExport) or nameof(view.CanPrevious) or nameof(view.CanNext))) changes.Add(e.PropertyName!);
        };
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Same(chart, view.Chart);
        Assert.Same(months, view.HeatMonths);
        Assert.Empty(changes);
    });

    [Fact]
    public void NewRequestUpdatesItsDayAndKeepsExistingRowsAndUnchangedChartPoints() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        var oldRows = view.RequestRows.ToDictionary(row => row.Identity);
        var oldDays = view.HeatDays.ToDictionary(day => day.Date);
        var oldRank = Assert.Single(view.RankRows);
        var oldPoints = view.Chart!.Points.ToArray();
        var changedDays = new HashSet<DateTime>();
        var rowChanges = new List<string>();
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        foreach (var day in view.HeatDays) day.PropertyChanged += (_, _) => changedDays.Add(day.Date);
        foreach (var row in view.RequestRows) row.PropertyChanged += (_, e) => rowChanges.Add(e.PropertyName!);
        view.RequestRows.CollectionChanged += (_, e) => collectionChanges.Add(e.Action);
        fixture.Append("new-request");
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("31", view.TotalRequests);
        Assert.Equal("new-request", view.RequestRows[0].Identity.RequestId);
        Assert.All(view.RequestRows.Skip(1), row => Assert.Same(oldRows[row.Identity], row));
        Assert.All(view.HeatDays, day => Assert.Same(oldDays[day.Date], day));
        Assert.Equal(DateTime.Today, Assert.Single(changedDays));
        Assert.Same(oldRank, Assert.Single(view.RankRows));
        Assert.Contains("31 次", oldRank.Detail);
        Assert.Empty(rowChanges);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }, collectionChanges);
        for (var hour = 0; hour < 24; hour++)
            if (hour != 13) Assert.Same(oldPoints[hour], view.Chart!.Points[hour]);
        Assert.NotSame(oldPoints[13], view.Chart!.Points[13]);
    });

    [Fact]
    public void RequestOutsideSelectedPeriodOnlyUpdatesTheAffectedCalendarDay() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        var chart = view.Chart;
        var changedDays = new HashSet<DateTime>();
        var otherChanges = new List<string>();
        foreach (var day in view.HeatDays) day.PropertyChanged += (_, _) => changedDays.Add(day.Date);
        foreach (var row in PresentationRows(view).Where(row => row is not StatisticsHeatDay))
            row.PropertyChanged += (_, e) => otherChanges.Add(e.PropertyName!);
        foreach (var collection in PresentationCollections(view)) collection.CollectionChanged += (_, e) => otherChanges.Add(e.Action.ToString());
        fixture.Append("yesterday-request", daysAgo: 1);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("30", view.TotalRequests);
        Assert.Same(chart, view.Chart);
        Assert.Equal(DateTime.Today.AddDays(-1), Assert.Single(changedDays));
        Assert.Empty(otherChanges);
    });

    [Fact]
    public void PriceEditUpdatesExistingRequestCostFieldsWithoutRecreatingRows() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        var rows = view.RequestRows.ToArray();
        var fields = new HashSet<string>();
        var collectionChanges = 0;
        var tileChanges = 0;
        foreach (var row in rows) row.PropertyChanged += (_, e) => fields.Add(e.PropertyName!);
        foreach (var tile in view.TokenTiles) tile.PropertyChanged += (_, _) => tileChanges++;
        view.RequestRows.CollectionChanged += (_, _) => collectionChanges++;
        view.EditingModel = "model-a";
        view.BasePrices.Input = "2";
        view.BasePrices.Output = "10";
        view.BasePrices.CacheRead = "0.2";
        await view.SavePriceCommand.ExecuteAsync(null);
        Assert.Equal("≈$0.0468", view.TotalCost);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, tileChanges);
        Assert.True(fields.SetEquals([nameof(StatisticsRequestRow.Cost), nameof(StatisticsRequestRow.ToolTip)]));
        for (var index = 0; index < rows.Length; index++) Assert.Same(rows[index], view.RequestRows[index]);
        Assert.All(rows, row => Assert.Equal("≈$0.00156", row.Cost));
    });

    [Fact]
    public void RequestIdentityIncludesRouteWhenClientsReuseTheSameRequestId() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        await Ready(view);
        var codex = view.RequestRows.Single(row => row.Identity.RequestId == "request-29");
        fixture.Append("request-29", client: ClientType.Claude);
        await view.RefreshCommand.ExecuteAsync(null);
        var matches = view.RequestRows.Where(row => row.Identity.RequestId == "request-29").ToArray();
        Assert.Equal(2, matches.Length);
        Assert.Equal(2, matches.Select(row => row.Identity.RouteId).Distinct().Count());
        Assert.Contains(codex, matches);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.All(matches, row => Assert.Same(row, view.RequestRows.Single(next => next.Identity == row.Identity)));
    });

    [Fact]
    public void AutomaticRefreshKeepsReportVisibleAndOnlyManualRefreshShowsProgress() => Run(async () =>
    {
        using var fixture = new Fixture();
        var view = fixture.View;
        view.OnNavigatedTo();
        Assert.True(view.ShowLoading);
        await Ready(view);
        var changes = new List<bool>();
        var hiddenReports = 0;
        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(view.ShowLoading)) changes.Add(view.ShowLoading);
            if (e.PropertyName == nameof(view.HasReport) && !view.HasReport) hiddenReports++;
        };
        fixture.Append("automatic-request");
        fixture.NotifyRefresh();
        await WaitUntil(() => view.TotalRequests == "31" && !view.IsLoading);
        Assert.Empty(changes);
        Assert.Equal(0, hiddenReports);
        await view.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(new[] { true, false }, changes);
        Assert.True(view.HasReport);
    });

    private static IEnumerable<INotifyPropertyChanged> PresentationRows(OverviewPageViewModel view) =>
        view.TokenTiles.Cast<INotifyPropertyChanged>().Concat(view.RankRows).Concat(view.HeatDays).Concat(view.RequestRows);

    private static INotifyCollectionChanged[] PresentationCollections(OverviewPageViewModel view) =>
        [view.TokenTiles, view.Categories, view.RankRows, view.HeatDays, view.RequestRows];
}
