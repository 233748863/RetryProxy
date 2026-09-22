using System;

namespace RetryProxy.Core.Internal;

/// <summary>可增长的字节缓冲（对应 Rust 的 <c>Vec&lt;u8&gt;</c>）。</summary>
internal sealed class ByteBuffer
{
    private byte[] _buffer;
    private int _length;

    public ByteBuffer(int capacity = 256)
    {
        _buffer = new byte[Math.Max(capacity, 16)];
    }

    public int Length => _length;

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

    public void Append(byte value)
    {
        EnsureCapacity(_length + 1);
        _buffer[_length++] = value;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_length + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    public void Clear() => _length = 0;

    public void Pop()
    {
        if (_length > 0)
        {
            _length--;
        }
    }

    public byte[] ToArray() => Span.ToArray();

    /// <summary>取走内容并清空缓冲（对应 <c>std::mem::take</c>）。</summary>
    public byte[] Take()
    {
        var result = ToArray();
        _length = 0;
        return result;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var capacity = _buffer.Length;
        while (capacity < required)
        {
            capacity = capacity >= int.MaxValue / 2 ? int.MaxValue : capacity * 2;
        }

        Array.Resize(ref _buffer, capacity);
    }
}
