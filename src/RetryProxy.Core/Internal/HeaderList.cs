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

    /// <summary>
    /// 删掉 <paramref name="names"/> 中的全部头，换成一个新头，放在其中第一个出现的位置；都没有时追加到末尾。
    /// 例：<c>Accept, Authorization, x-api-key, User-Agent</c> 换成 <c>x-api-key</c> → <c>Accept, x-api-key, User-Agent</c>。
    /// </summary>
    public void ReplaceAll(IReadOnlyCollection<string> names, string name, string value)
    {
        var index = _entries.FindIndex(entry => Contains(names, entry.Key));
        _entries.RemoveAll(entry => Contains(names, entry.Key));
        _entries.Insert(index < 0 ? _entries.Count : index, new KeyValuePair<string, string>(name, value));
    }

    /// <summary>按原位置改写同名头的每个值；返回 null 表示删掉这一项。</summary>
    public void Update(string name, Func<string, string?> update)
    {
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index];
            if (!Matches(entry.Key, name))
            {
                continue;
            }

            if (update(entry.Value) is { } value)
            {
                _entries[index] = new KeyValuePair<string, string>(entry.Key, value);
            }
            else
            {
                _entries.RemoveAt(index);
            }
        }
    }

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

    private static bool Contains(IReadOnlyCollection<string> names, string name)
    {
        foreach (var candidate in names)
        {
            if (Matches(candidate, name))
            {
                return true;
            }
        }

        return false;
    }
}
