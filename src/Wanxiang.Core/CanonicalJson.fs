namespace Wanxiang.Core

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// 规范化 JSON：对象键按 ordinal 字典序排列、数字用固定编码、数组保持业务顺序、无空白。
/// 用于生成 commandId 的 canonical payload。
module CanonicalJson =

    let rec private writeValue (writer: Utf8JsonWriter) (node: JsonNode) =
        match node with
        | null -> writer.WriteNullValue()
        | :? JsonObject as obj ->
            writer.WriteStartObject()
            let keys = obj |> Seq.map (fun kv -> kv.Key) |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b))
            for key in keys do
                writer.WritePropertyName(key)
                writeValue writer (obj[key])
            writer.WriteEndObject()
        | :? JsonArray as arr ->
            writer.WriteStartArray()
            for item in arr do
                writeValue writer item
            writer.WriteEndArray()
        | :? JsonValue as value ->
            match value.TryGetValue<JsonElement>() with
            | true, element ->
                match element.ValueKind with
                | JsonValueKind.String -> writer.WriteStringValue(element.GetString())
                | JsonValueKind.Number ->
                    // 统一数字编码：优先按 decimal 最短表示输出，避免 1.0 与 1 的差异。
                    match element.TryGetDecimal() with
                    | true, d ->
                        let text = d.ToString("G29", CultureInfo.InvariantCulture)
                        writer.WriteRawValue(text, skipInputValidation = true)
                    | false, _ ->
                        writer.WriteRawValue(element.GetRawText(), skipInputValidation = true)
                | JsonValueKind.True -> writer.WriteBooleanValue(true)
                | JsonValueKind.False -> writer.WriteBooleanValue(false)
                | JsonValueKind.Null -> writer.WriteNullValue()
                | _ -> writer.WriteRawValue(element.GetRawText(), skipInputValidation = true)
            | false, _ ->
                // 直接由 .NET 值构造的 JsonValue
                value.WriteTo(writer)
        | _ -> writer.WriteNullValue()

    /// 将 JsonNode 序列化为规范化 JSON 字符串（UTF-8 无 BOM，不含空白）。
    let serialize (node: JsonNode) : string =
        use stream = new System.IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions())
        writeValue writer node
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    /// 将任意 JSON 文本解析后规范化。非法 JSON 返回 None。
    let tryNormalize (jsonText: string) : string option =
        try
            let node = JsonNode.Parse(jsonText)
            Some(serialize node)
        with _ ->
            None
