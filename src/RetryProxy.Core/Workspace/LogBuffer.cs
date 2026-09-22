using System.Collections.Generic;

namespace RetryProxy.Core.Workspace;

/// <summary>
/// 界面上的日志缓冲：最多保留 <see cref="RetainLines"/> 行，超过后整批丢弃最旧的一段，避免每行都搬一次。
/// </summary>
public sealed class LogBuffer
{
    /// <summary>界面最多保留的日志行数。</summary>
    public const int RetainLines = 2000;

    /// <summary>超过上限时一次丢弃的行数。</summary>
    public const int TrimLines = 500;

    private readonly List<string> _lines = new();

    public int Count => _lines.Count;

    /// <summary>自创建以来被丢弃（整批裁剪或清空）的总行数；行的全局序号 = <see cref="Dropped"/> + 缓冲内下标。</summary>
    public long Dropped { get; private set; }

    /// <summary>自创建以来 Push 过的总行数。</summary>
    public long TotalPushed { get; private set; }

    public string this[int index] => _lines[index];

    public IReadOnlyList<string> Lines => _lines;

    public void Push(string line)
    {
        if (_lines.Count >= RetainLines)
        {
            _lines.RemoveRange(0, TrimLines);
            Dropped += TrimLines;
        }

        _lines.Add(line);
        TotalPushed++;
    }

    public void Clear()
    {
        Dropped += _lines.Count;
        _lines.Clear();
    }
}
