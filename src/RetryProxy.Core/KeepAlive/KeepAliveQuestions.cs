using System.Collections.Generic;

namespace RetryProxy.Core.KeepAlive;

/// <summary>保活探测随机抽取的 20 条短回复提示。</summary>
public static class KeepAliveQuestions
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "只回答“是”，不调用任何工具。",
        "只回答“好”，不调用任何工具。",
        "只回答“对”，不调用任何工具。",
        "只回答“对对”，不调用任何工具。",
        "只回答“好的”，不调用任何工具。",
        "只回答“可以”，不调用任何工具。",
        "只回答“行”，不调用任何工具。",
        "只回答“收到”，不调用任何工具。",
        "只回答“明白”，不调用任何工具。",
        "只回答“确认”，不调用任何工具。",
        "只回答“同意”，不调用任何工具。",
        "只回答“知道了”，不调用任何工具。",
        "只回答“没问题”，不调用任何工具。",
        "只回答“嗯”，不调用任何工具。",
        "只回答“OK”，不调用任何工具。",
        "只回答“ok”，不调用任何工具。",
        "只回答“yes”，不调用任何工具。",
        "只回答“1”，不调用任何工具。",
        "只回答“2”，不调用任何工具。",
        "只回答“3”，不调用任何工具。",
    };

    public static int Count => All.Count;
}
