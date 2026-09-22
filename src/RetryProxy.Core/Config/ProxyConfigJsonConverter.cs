using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Config;

/// <summary>
/// 让 <see cref="ProxyConfig"/> 作为 AllConfig 的子对象时，仍按 Rust 规范 JSON
/// （snake_case、固定键序）读写，并在读取时执行迁移。
/// </summary>
public sealed class ProxyConfigJsonConverter : JsonConverter<ProxyConfig>
{
    public override ProxyConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ProxyConfigJson.ParseValue(document.RootElement).Config;
    }

    public override void Write(Utf8JsonWriter writer, ProxyConfig value, JsonSerializerOptions options)
    {
        ProxyConfigJson.WriteCanonical(writer, value);
    }
}
