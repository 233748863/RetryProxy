using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Diagnostics;

/// <summary>诊断的固定文案与白名单文本导出；只展示原记录，不推算结果或重试次数。</summary>
public static class RequestDiagnosticText
{
    public const string Missing = "未记录";
    public const string IncompleteWarning = "诊断记录不完整，不影响请求转发";

    private static string T(string key, Func<string, string>? translate) => translate?.Invoke(key) ?? key;
    private static string F(string key, Func<string, string>? translate, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key, translate), args);

    public static string Outcome(DiagnosticOutcome outcome, Func<string, string>? translate = null) => T(outcome switch
    {
        DiagnosticOutcome.Pending => "处理中",
        DiagnosticOutcome.Success => "成功",
        DiagnosticOutcome.Failure => "失败",
        _ => "结束状态未记录",
    }, translate);

    public static string Result(DiagnosticSummary summary, Func<string, string>? translate = null) =>
        summary.RetryCount == 0 ? Outcome(summary.Outcome, translate)
            : F("{0}（重试 {1} 次）", translate, Outcome(summary.Outcome, translate), summary.RetryCount);

    public static string Completeness(DiagnosticSummary summary, Func<string, string>? translate = null) =>
        summary.Incomplete ? T(IncompleteWarning, translate) : string.Empty;

    public static string Delivery(DiagnosticDelivery delivery, Func<string, string>? translate = null) => T(delivery switch
    {
        DiagnosticDelivery.NotStarted => "尚未交付",
        DiagnosticDelivery.Complete => "交付完成",
        DiagnosticDelivery.Interrupted => "交付中断",
        _ => "交付状态未记录",
    }, translate);

    public static string Seconds(double? seconds, Func<string, string>? translate = null) =>
        seconds is { } value && double.IsFinite(value) && value >= 0
            ? F("{0}秒", translate, value.ToString("0.0", CultureInfo.CurrentCulture)) : T(Missing, translate);

    public static string Value(string? value, Func<string, string>? translate = null) =>
        DiagnosticSafety.Text(value, 160) ?? T(Missing, translate);

    private static string Reason(string? value, Func<string, string>? translate) =>
        T(DiagnosticSafety.Text(value, 160) ?? Missing, translate);

    public static string Target(DiagnosticTarget? target, Func<string, string>? translate = null) => target is null
        ? T(Missing, translate) : $"{Value(target.ProviderName, translate)} · {Value(target.KeyName, translate)}";

    public static string EventTitle(DiagnosticEventKind kind, Func<string, string>? translate = null) => T(kind switch
    {
        DiagnosticEventKind.Started => "请求开始",
        DiagnosticEventKind.SendStarted => "实际发送",
        DiagnosticEventKind.ResponseHeaders => "收到响应头",
        DiagnosticEventKind.WaitingGeneration => "等待生成",
        DiagnosticEventKind.SendFinished => "本次发送结束",
        DiagnosticEventKind.RetryWaiting => "等待重试",
        DiagnosticEventKind.RetryWaitFinished => "重试等待结束",
        DiagnosticEventKind.CompatibilityResend => "兼容重发（不计重试）",
        DiagnosticEventKind.Switched => "手动改投（不计重试）",
        DiagnosticEventKind.ResponseReady => "响应已就绪",
        DiagnosticEventKind.Outcome => "原业务结果",
        DiagnosticEventKind.Delivery => "客户端交付",
        DiagnosticEventKind.Finished => "请求收尾",
        _ => "未识别事件",
    }, translate);

    public static string EventHeading(DiagnosticEvent item, Func<string, string>? translate = null) =>
        F("序号 {0} · {1} · {2}", translate, item.Sequence, Seconds(item.Entry.ElapsedSeconds, translate), EventTitle(item.Entry.Kind, translate));

    public static string EventBody(DiagnosticEntry source, Func<string, string>? translate = null)
    {
        var entry = DiagnosticSafety.Sanitize(source);
        var lines = new List<string>();
        void Add(string label, string value) => lines.Add(F("{0}：{1}", translate, T(label, translate), value));
        if (entry.Kind is DiagnosticEventKind.SendStarted or DiagnosticEventKind.ResponseHeaders
            or DiagnosticEventKind.SendFinished or DiagnosticEventKind.WaitingGeneration)
        {
            Add("供应商 / Key", Target(entry.Target, translate));
            Add("出站模型", Value(entry.Target?.Model, translate));
            Add("实际发送序号", entry.SendNumber?.ToString(CultureInfo.CurrentCulture) ?? T(Missing, translate));
            Add("外层尝试序号", entry.AttemptNumber?.ToString(CultureInfo.CurrentCulture) ?? T(Missing, translate));
        }
        if (entry.Kind is DiagnosticEventKind.ResponseHeaders or DiagnosticEventKind.SendFinished or DiagnosticEventKind.Outcome)
            Add("HTTP 状态", entry.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? T(Missing, translate));
        if (entry.Kind == DiagnosticEventKind.RetryWaiting) Add("计划等待", Seconds(entry.PlannedWaitSeconds, translate));
        if (entry.Kind == DiagnosticEventKind.RetryWaitFinished) Add("实际等待", Seconds(entry.DurationSeconds, translate));
        if (entry.Kind == DiagnosticEventKind.SendFinished) Add("本次耗时", Seconds(entry.DurationSeconds, translate));
        if (entry.Kind is DiagnosticEventKind.RetryWaiting or DiagnosticEventKind.RetryWaitFinished
            or DiagnosticEventKind.CompatibilityResend or DiagnosticEventKind.Switched or DiagnosticEventKind.SendFinished
            or DiagnosticEventKind.SendStarted or DiagnosticEventKind.WaitingGeneration)
            Add("原因", Reason(entry.Reason, translate));
        if (entry.Kind == DiagnosticEventKind.Outcome)
        {
            Add("结果", entry.Outcome is { } outcome ? Outcome(outcome, translate) : T(Missing, translate));
            Add("首字耗时", Seconds(entry.FirstContentSeconds, translate));
            Add("原因", Reason(entry.Reason, translate));
        }
        if (entry.Kind is DiagnosticEventKind.Delivery or DiagnosticEventKind.Finished)
            Add("客户端交付", entry.Delivery is { } delivery ? Delivery(delivery, translate) : T(Missing, translate));
        if (entry.RetryCount is { } retries) Add("原重试次数", retries.ToString(CultureInfo.CurrentCulture));
        if (entry.ErrorCode is not null) Add("错误码", Value(entry.ErrorCode, translate));
        if (entry.UpstreamRequestId is not null) Add("上游请求编号", Value(entry.UpstreamRequestId, translate));
        if (entry.LastEvent is not null) Add("最后事件", Value(entry.LastEvent, translate));
        return string.Join(Environment.NewLine, lines);
    }

    public static string Overview(DiagnosticSummary summary, Func<string, string>? translate = null)
    {
        var request = DiagnosticSafety.Sanitize(summary.Request);
        string Field(string name, string value) => F("{0}：{1}", translate, T(name, translate), value);
        var status = summary.StatusCode is >= 100 and <= 599
            ? summary.StatusCode.Value.ToString(CultureInfo.InvariantCulture) : T(Missing, translate);
        var lines = new List<string>
        {
            Field("请求编号", Value(request.RequestId, translate)),
            $"{(request.Client == ClientType.Claude ? "Claude Code" : "Codex")} · {request.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}",
            Field("接口", $"{request.Method} {request.Endpoint}"),
            Field("最近实际目标", Target(summary.LastTarget, translate)),
            Field("出站模型", Value(summary.LastTarget?.Model, translate)),
            $"{Field("结果", Outcome(summary.Outcome, translate))} · HTTP {status} · {Delivery(summary.Delivery, translate)}",
            $"{Field("实际发送次数", summary.SendCount.ToString(CultureInfo.CurrentCulture))} · {Field("原重试次数", summary.RetryCount.ToString(CultureInfo.CurrentCulture))}",
            $"{Field("首字耗时", Seconds(summary.FirstContentSeconds, translate))} · {Field("总耗时", Seconds(summary.TotalSeconds, translate))}",
        };
        if (summary.Outcome is DiagnosticOutcome.Failure or DiagnosticOutcome.Unknown)
            lines.Add(Field("结束原因", Reason(summary.FailureReason, translate)));
        if (summary.Incomplete) lines.Add(Completeness(summary, translate));
        return string.Join(Environment.NewLine, lines);
    }

    public static string SafeSummary(DiagnosticSummary summary, Func<string, string>? translate = null)
    {
        var request = DiagnosticSafety.Sanitize(summary.Request);
        var text = new StringBuilder();
        void Add(string label, string value) => text.AppendLine(F("{0}：{1}", translate, T(label, translate), value));
        Add("请求编号", Value(request.RequestId, translate));
        Add("客户端", request.Client == ClientType.Claude ? "Claude Code" : "Codex");
        Add("开始时间", request.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Add("接口", $"{request.Method} {request.Endpoint}");
        Add("最近实际目标", Target(summary.LastTarget, translate));
        Add("出站模型", Value(summary.LastTarget?.Model, translate));
        Add("结果", Result(summary, translate));
        Add("HTTP 状态", summary.StatusCode is >= 100 and <= 599
            ? summary.StatusCode.Value.ToString(CultureInfo.InvariantCulture) : T(Missing, translate));
        Add("客户端交付", Delivery(summary.Delivery, translate));
        Add("实际发送次数", summary.SendCount.ToString(CultureInfo.CurrentCulture));
        Add("原重试次数", summary.RetryCount.ToString(CultureInfo.CurrentCulture));
        Add("首字耗时", Seconds(summary.FirstContentSeconds, translate));
        Add("总耗时", Seconds(summary.TotalSeconds, translate));
        Add("结束原因", Reason(summary.FailureReason, translate));
        if (summary.Incomplete) text.AppendLine(Completeness(summary, translate));
        return text.ToString().TrimEnd();
    }
}
