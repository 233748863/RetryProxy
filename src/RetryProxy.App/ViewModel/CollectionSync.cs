using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RetryProxy.ViewModel;

/// <summary>按条目移动/插入更新集合，保留未变化行的焦点、展开状态与自动化节点。</summary>
internal static class CollectionSync
{
    public static void Update<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (var index = target.Count - 1; index >= 0; index--)
            if (!items.Contains(target[index])) target.RemoveAt(index);
        for (var index = 0; index < items.Count; index++)
        {
            var oldIndex = target.IndexOf(items[index]);
            if (oldIndex < 0) target.Insert(index, items[index]);
            else if (oldIndex != index) target.Move(oldIndex, index);
        }
    }
}
