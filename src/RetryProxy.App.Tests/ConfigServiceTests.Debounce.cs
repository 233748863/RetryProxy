using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class ConfigServiceTests
{
    private static void TrackWrites(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE write_audit (updated_at INTEGER NOT NULL);
        CREATE TRIGGER track_config AFTER UPDATE ON config
        BEGIN INSERT INTO write_audit VALUES (NEW.updated_at); END;
        """);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void ObservableChangesKeepCallbackAndDebounceToOneTransaction()
    {
        Seed(Rows(Sample()));
        using var connection = Connect();
        TrackWrites(connection);
        var config = Open().Get();
        var schedule = config.OnAnyChangedAction;
        Assert.NotNull(schedule);
        var callbacks = 0;
        config.OnAnyChangedAction = () => { callbacks++; schedule(); };
        var changedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        config.CommonConfig.ExitToTray = true;
        config.CommonConfig.RunForVersion = "after";
        config.OtherConfig.UiCultureInfoName = "en";

        Assert.Equal(3, callbacks);
        Assert.True(SpinWait.SpinUntil(() => Scalar(connection, "SELECT COUNT(*) FROM write_audit;") >= 4,
            TimeSpan.FromSeconds(5)), "200ms 防抖保存未完成");
        Assert.Equal(4, Scalar(connection, "SELECT COUNT(*) FROM write_audit;"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(DISTINCT updated_at) FROM write_audit;"));
        Assert.True(Scalar(connection, "SELECT MIN(updated_at) FROM write_audit;") - changedAt >= 180,
            "属性变化应保持 200ms 防抖，允许计时器误差 20ms");
        AssertRows(Rows(config), ReadRows());
    }

    [Fact]
    public async Task SaveCheckedCancelsPendingDebounce()
    {
        Seed(Rows(Sample()));
        using var connection = Connect();
        TrackWrites(connection);
        var service = Open();
        service.Get().CommonConfig.RunForVersion = "立即保存";
        service.SaveChecked();
        Assert.Equal(4, Scalar(connection, "SELECT COUNT(*) FROM write_audit;"));
        await Task.Delay(350);
        Assert.Equal(4, Scalar(connection, "SELECT COUNT(*) FROM write_audit;"));
    }
}
