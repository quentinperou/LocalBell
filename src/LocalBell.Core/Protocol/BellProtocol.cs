using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalBell.Core.Protocol;

/// <summary>
/// The constants of the LocalBell protocol, and its JSON. Every PC runs the same app, with no server anywhere: UDP
/// announcements to find each other (multicast and broadcast, as LocalSend), then small JSON requests over HTTP to the
/// little server of each PC (<see cref="Network.MiniHttpServer"/>).
/// </summary>
public static class BellProtocol
{
    /// <summary>Written in every announcement: datagrams of other apps on the port are ignored.</summary>
    public const string AppTag = "LocalBell";

    /// <summary>Raised only by a change that older versions cannot read; they ignore a higher one.</summary>
    public const int Version = 1;

    /// <summary>The UDP port of the announcements, and the TCP port each server tries first.</summary>
    public const int DefaultPort = 53318;

    /// <summary>
    /// A link-local group (224.0.0.0/24): never routed, and let through by the switches that filter the other groups.
    /// LocalSend uses 224.0.0.167 for the same reasons.
    /// </summary>
    public static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.168");

    public const string ApiRoot = "/api/localbell/v1";
    public const string HelloPath = ApiRoot + "/hello";
    public const string PeersPath = ApiRoot + "/peers";
    public const string RingPath = ApiRoot + "/ring";
    public const string ReplyPath = ApiRoot + "/reply";

    public const int MaxNameLength = 40;
    public const int MaxMessageLength = 300;
    public const int MaxIdLength = 64;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    /// <summary>The message, or null when the bytes are not one (another app, a damaged datagram).</summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(utf8, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A name or a message as received from another PC: control characters and line breaks become spaces, a run of
    /// spaces one space, the spaces around go; the invisible format characters (zero-width spaces, the marks that turn
    /// text right to left: a name shown empty or reversed) go too. It is cut to <paramref name="maxLength"/> without
    /// splitting a character.
    /// </summary>
    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format) continue;
            if (char.IsControl(c) || char.IsWhiteSpace(c) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
            }
            else builder.Append(c);
        }
        string clean = builder.ToString().Trim();
        if (clean.Length <= maxLength) return clean;
        int cut = char.IsHighSurrogate(clean[maxLength - 1]) ? maxLength - 1 : maxLength;
        return clean[..cut].TrimEnd();
    }

    /// <summary>
    /// An id made by a PC (its own, a ring's): letters, digits and dashes, 1 to <see cref="MaxIdLength"/> long. Ours are
    /// GUIDs; nothing else gets in (a line break in an id would forge lines of the developer log).
    /// </summary>
    public static bool IsId(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
