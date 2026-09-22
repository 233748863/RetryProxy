using System;
using System.Collections.Generic;
using System.Text;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Stats;

internal readonly struct DecodedEvent
{
    public DecodedEvent(string name, byte[] data)
    {
        Name = name;
        Data = data;
    }

    public string Name { get; }

    public byte[] Data { get; }
}

/// <summary>
/// 增量 SSE 解码器（对应 response_stats.rs 的 EventDecoder）。
/// 只识别 data/event/id/retry 与注释行；其它行标记为 <see cref="Unsupported"/>。
/// </summary>
internal sealed class EventDecoder
{
    public const int MaxObservationBytes = 8 * 1024 * 1024;

    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[][] FieldPrefixes =
    {
        "data:"u8.ToArray(), "event:"u8.ToArray(), "id:"u8.ToArray(), "retry:"u8.ToArray(),
    };

    private readonly ByteBuffer _line = new();
    private readonly ByteBuffer _data = new();
    private string _name = string.Empty;
    private bool _lineHasBytes;
    private bool _previousCr;
    private bool _skipEvent;

    public bool Unsupported { get; private set; }

    public bool PendingLineIsSupported()
    {
        var raw = _line.Span;
        var line = raw.StartsWith(Bom) ? raw[Bom.Length..] : raw;
        if (Bom.AsSpan().StartsWith(raw))
        {
            return true;
        }

        if (line.Length > 0 && line[0] == (byte)':')
        {
            return true;
        }

        foreach (var field in FieldPrefixes)
        {
            if (field.AsSpan().StartsWith(line) || line.StartsWith(field))
            {
                return true;
            }
        }

        return false;
    }

    public List<DecodedEvent> Push(ReadOnlySpan<byte> chunk)
    {
        var events = new List<DecodedEvent>();
        foreach (var value in chunk)
        {
            if (value == (byte)'\n' && _previousCr)
            {
                _previousCr = false;
                continue;
            }

            _previousCr = value == (byte)'\r';
            if (value is (byte)'\r' or (byte)'\n')
            {
                if (FinishLine() is { } decoded)
                {
                    events.Add(decoded);
                }
            }
            else
            {
                _lineHasBytes = true;
                if (!_skipEvent)
                {
                    if (_line.Length + _data.Length + _name.Length < MaxObservationBytes)
                    {
                        _line.Append(value);
                    }
                    else
                    {
                        _line.Clear();
                        _data.Clear();
                        _name = string.Empty;
                        _skipEvent = true;
                        Unsupported = true;
                    }
                }
            }
        }

        return events;
    }

    private DecodedEvent? FinishLine()
    {
        DecodedEvent? decoded = null;
        if (!_lineHasBytes)
        {
            if (!_skipEvent && _data.Length > 0)
            {
                _data.Pop();
                decoded = new DecodedEvent(_name, _data.Take());
                _name = string.Empty;
            }

            _data.Clear();
            _name = string.Empty;
            _skipEvent = false;
        }
        else if (!_skipEvent)
        {
            var raw = _line.Span;
            var line = raw.StartsWith(Bom) ? raw[Bom.Length..] : raw;
            if (line.StartsWith("data:"u8))
            {
                var data = line["data:".Length..];
                if (data.Length > 0 && data[0] == (byte)' ')
                {
                    data = data[1..];
                }

                _data.Append(data);
                _data.Append((byte)'\n');
            }
            else if (line.StartsWith("event:"u8))
            {
                var name = line["event:".Length..];
                if (name.Length > 0 && name[0] == (byte)' ')
                {
                    name = name[1..];
                }

                _name = Encoding.UTF8.GetString(name);
            }
            else if (line.SequenceEqual("data"u8))
            {
                _data.Append((byte)'\n');
            }
            else if (line.SequenceEqual("event"u8))
            {
                _name = string.Empty;
            }
            else if (!(line.Length > 0 && line[0] == (byte)':')
                && !line.StartsWith("id:"u8)
                && !line.StartsWith("retry:"u8)
                && !line.SequenceEqual("id"u8)
                && !line.SequenceEqual("retry"u8))
            {
                Unsupported = true;
            }
        }

        _line.Clear();
        _lineHasBytes = false;
        return decoded;
    }
}
