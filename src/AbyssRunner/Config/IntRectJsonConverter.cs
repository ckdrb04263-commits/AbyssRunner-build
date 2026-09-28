using System.Text.Json;
using System.Text.Json.Serialization;
using AbyssRunner.Core;

namespace AbyssRunner.Config;

public sealed class IntRectJsonConverter : JsonConverter<IntRect>
{
    public override IntRect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("IntRect는 [left,top,right,bottom] 배열이어야 합니다.");
        var vals = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException();
            vals[i] = reader.GetInt32();
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException();
        return new IntRect(vals[0], vals[1], vals[2], vals[3]);
    }

    public override void Write(Utf8JsonWriter writer, IntRect value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Left);
        writer.WriteNumberValue(value.Top);
        writer.WriteNumberValue(value.Right);
        writer.WriteNumberValue(value.Bottom);
        writer.WriteEndArray();
    }
}
