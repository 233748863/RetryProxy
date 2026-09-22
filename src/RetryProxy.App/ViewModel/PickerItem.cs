namespace RetryProxy.ViewModel;

/// <summary>下拉框条目：Key 为标识（服务商名 / 通道 ID），Text 为显示文案。</summary>
public sealed record PickerItem(string Key, string Text)
{
    public override string ToString() => Text;
}
