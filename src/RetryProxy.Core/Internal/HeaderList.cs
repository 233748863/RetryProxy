using System;
using System.Collections;
using System.Collections.Generic;

namespace RetryProxy.Core.Internal;

/// <summary>
/// 保序、允许重名的 HTTP 头列表（对应 axum 的 HeaderMap）。头名比较不区分大小写。
/// </summary>
public sealed class HeaderList : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _entries = new();

    public HeaderList()
    {
    }

    public HeaderList(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var entry in entries)
        {
            Append(entry.Key, entry.Value);
        }
    }

    public int Count => _entries.Count;

    public void Append(string name, string value) => _entries.Add(new KeyValuePair<string, string>(name, value));

    /// <summary>对应 <c>HeaderMap::insert</c>：替换同名的全部值。</summary>
    public void Set(string name, string value)
    {
        _entries.RemoveAll(entry => Matches(entry.Key, name));
        Append(name, value);
    }

    public void Remove(string name) => _entries.RemoveAll(entry => Matches(entry.Key, name));

    public bool Contains(string name)
    {
        foreach (var entry in _entries)
        {
            if (Matches(entry.Key, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>对应 <c>HeaderMap::get</c>：第一个同名值。</summary>
    public string? Get(string name)
    {
        foreach (var entry in _entries)
        {
            if (Matches(entry.Key, name))
            {
                return entry.Value;
            }
        }

        return null;
    }

    public IEnumerable<string> GetAll(string name)
    {
        foreach (var entry in _entries)
        {
            if (Matches(entry.Key, name))
            {
                yield return entry.Value;
            }
        }
    }

    public HeaderList Clone() => new(_entries);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool Matches(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
