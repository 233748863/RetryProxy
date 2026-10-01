using System;
using RetryProxy.Core.Workspace;

namespace RetryProxy.ViewModel.Pages;

/// <summary>供应商 Key 行和准备任务行共用状态文案，倒计时不重复记录请求用量。</summary>
internal static class PreparationStatusText
{
    public static string Status(PreparationTask? task, bool forKey = false)
    {
        if (task is null) return ClientPageText.Translate("未准备");
        if (task.CanStart && task.LastError is not null) return ClientPageText.Translate("准备失败");
        if (task.IsPreparing) return ClientPageText.Translate("准备中 {0}", Clock(task.PreparationElapsed));
        if (task.IsReady) return ClientPageText.Translate("已准备 · 下次 {0}", Clock(task.NextKeepAlive));
        return ClientPageText.Translate(forKey && task.CanStart ? "未准备" : task.Status);
    }

    public static string Statistics(PreparationTask task)
    {
        var today = task.DailyDate == DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return ClientPageText.Translate("今日：{0} 轮 · 成功 {1} 轮", today ? task.DailyRounds : 0, today ? task.DailySuccesses : 0);
    }

    private static string Clock(TimeSpan duration)
    {
        var seconds = Math.Max(0L, (long)Math.Ceiling(duration.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
