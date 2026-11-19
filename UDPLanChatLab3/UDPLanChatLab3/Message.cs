using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UDPLanChatLab3;

public enum MessageType
{
    Join,
    Here,
    Taken,
    Beat,
    Leave,
    Chat,
    Private,
}

public sealed record Message(
    MessageType Type,
    string Id,
    string Name,
    string? Text = null,
    string? To = null,
    string? Nonce = null,
    string? Data = null)
{
    public string Kind => Wire.KindOf(Type);
}

public static partial class Wire
{
    public const int Version = 1;
    public const int MaxText = 500;
    public const int MaxDatagram = 4096;

    private static readonly Dictionary<MessageType, string> Kinds = new()
    {
        [MessageType.Join] = "join",
        [MessageType.Here] = "here",
        [MessageType.Taken] = "taken",
        [MessageType.Beat] = "beat",
        [MessageType.Leave] = "leave",
        [MessageType.Chat] = "chat",
        [MessageType.Private] = "pm",
    };

    private static readonly Dictionary<string, MessageType> Types =
        Kinds.ToDictionary(pair => pair.Value, pair => pair.Key);

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,20}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();

    public static string KindOf(MessageType type) => Kinds[type];

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static bool NamesEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static byte[] Encode(Message message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", Version);
            json.WriteString("type", Kinds[message.Type]);
            json.WriteString("id", message.Id);
            json.WriteString("name", message.Name);
            if (message.Text is not null) json.WriteString("text", message.Text);
            if (message.To is not null) json.WriteString("to", message.To);
            if (message.Nonce is not null) json.WriteString("nonce", message.Nonce);
            if (message.Data is not null) json.WriteString("data", message.Data);
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> datagram, out Message? message, out string reason)
    {
        message = null;

        if (datagram.Length == 0)
        {
            reason = "empty";
            return false;
        }

        if (datagram.Length > MaxDatagram)
        {
            reason = $"too long ({datagram.Length} > {MaxDatagram} bytes)";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(datagram.ToArray());
        }
        catch (JsonException)
        {
            reason = "not JSON";
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                reason = "not a JSON object";
                return false;
            }

            if (!root.TryGetProperty("v", out JsonElement v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out int version) || version != Version)
            {
                reason = "missing or unknown version";
                return false;
            }

            string? kind = GetString(root, "type");
            if (kind is null || !Types.TryGetValue(kind, out MessageType type))
            {
                reason = "unknown type";
                return false;
            }

            string? id = GetString(root, "id");
            string? name = GetString(root, "name");
            if (id is null || !IdPattern().IsMatch(id))
            {
                reason = "bad id";
                return false;
            }

            if (name is null || !IsValidName(name))
            {
                reason = "bad name";
                return false;
            }

            string? text = GetString(root, "text");
            string? to = GetString(root, "to");
            string? nonce = GetString(root, "nonce");
            string? data = GetString(root, "data");

            switch (type)
            {
                case MessageType.Chat when text is null || text.Length == 0:
                    reason = "chat without text";
                    return false;
                case MessageType.Chat when text.Length > MaxText:
                    reason = $"text too long ({text.Length} > {MaxText} characters)";
                    return false;
                case MessageType.Taken when text is { Length: > MaxText }:
                    reason = "reason too long";
                    return false;
                case MessageType.Private when to is null || !IsValidName(to):
                    reason = "private message without a valid receiver";
                    return false;
                case MessageType.Private when nonce is null || data is null:
                    reason = "private message without nonce or data";
                    return false;
            }

            message = new Message(type, id, name, text, to, nonce, data);
            reason = "";
            return true;
        }
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
