using System.Text;
using System.Xml;

namespace KuaKeDriveWebDav.WebDav;

/// <summary>
/// WebDAV PROPFIND 的 multistatus 响应条目
/// </summary>
public sealed record MultistatusResponse
{
    public required string Href { get; init; }

    public required string Name { get; init; }

    public required bool IsDirectory { get; init; }

    public long Size { get; init; }

    public long UpdatedAt { get; init; }

    public long CreatedAt { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";

    public string Etag { get; init; } = "";
}

/// <summary>
/// 生成 WebDAV 207 multistatus XML（参考 RFC 4918）
/// </summary>
public static class WebDavMultistatus
{
    private const string Dav = "DAV:";

    /// <summary>支持的属性名 → 写出器，键集即支持的属性集合，单点维护</summary>
    private static readonly Dictionary<string, Action<XmlWriter, MultistatusResponse>> PropWriters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["displayname"] = (w, r) => w.WriteElementString("D", "displayname", Dav, r.Name),
            ["resourcetype"] = (w, r) =>
            {
                w.WriteStartElement("D", "resourcetype", Dav);
                if (r.IsDirectory)
                    w.WriteElementString("D", "collection", Dav, string.Empty);
                w.WriteEndElement();
            },
            ["getcontentlength"] = (w, r) =>
                w.WriteElementString(
                    "D",
                    "getcontentlength",
                    Dav,
                    r.IsDirectory ? "0" : r.Size.ToString()
                ),
            ["getlastmodified"] = (w, r) =>
            {
                if (r.UpdatedAt > 0)
                    w.WriteElementString("D", "getlastmodified", Dav, FormatRfc1123(r.UpdatedAt));
            },
            ["creationdate"] = (w, r) =>
            {
                if (r.CreatedAt > 0)
                    w.WriteElementString("D", "creationdate", Dav, FormatIso8601(r.CreatedAt));
            },
            ["getcontenttype"] = (w, r) =>
            {
                if (!r.IsDirectory)
                    w.WriteElementString("D", "getcontenttype", Dav, r.ContentType);
            },
            ["getetag"] = (w, r) => w.WriteElementString("D", "getetag", Dav, r.Etag),
        };

    /// <param name="requested">客户端请求的属性集合，null 表示 allprop（返回全集）</param>
    public static async Task WriteAsync(
        Stream output,
        IReadOnlyList<MultistatusResponse> responses,
        HashSet<string>? requested,
        CancellationToken ct = default
    )
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Async = true,
            OmitXmlDeclaration = false,
            Indent = false,
        };
        await using var writer = XmlWriter.Create(output, settings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync("D", "multistatus", Dav);

        // 请求范围与不支持列表与单条 response 无关，循环外只算一次
        var returning = requested is null
            ? PropWriters.Keys.ToList()
            : PropWriters.Keys.Where(requested.Contains).ToList();
        var unsupported = requested is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : requested.Where(p => !PropWriters.ContainsKey(p)).ToList();

        foreach (var r in responses)
        {
            await writer.WriteStartElementAsync("D", "response", Dav);
            await writer.WriteElementStringAsync("D", "href", Dav, r.Href);

            await writer.WriteStartElementAsync("D", "propstat", Dav);
            await writer.WriteStartElementAsync("D", "prop", Dav);
            foreach (var name in returning)
                PropWriters[name](writer, r);
            await writer.WriteEndElementAsync();
            await writer.WriteElementStringAsync("D", "status", Dav, "HTTP/1.1 200 OK");
            await writer.WriteEndElementAsync();

            if (unsupported.Count > 0)
                await WriteUnsupportedPropstatAsync(writer, unsupported);

            await writer.WriteEndElementAsync();
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// <summary>
    /// 把毫秒时间戳格式化为 RFC1123（WebDAV getlastmodified），0 或负值返回空串
    /// </summary>
    public static string FormatRfc1123(long millis) =>
        millis <= 0 ? string.Empty : DateTimeOffset.FromUnixTimeMilliseconds(millis).ToString("R");

    private static string FormatIso8601(long millis) =>
        DateTimeOffset.FromUnixTimeMilliseconds(millis)
            .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static async Task WriteUnsupportedPropstatAsync(
        XmlWriter writer,
        IReadOnlyList<string> unsupported
    )
    {
        await writer.WriteStartElementAsync("D", "propstat", Dav);
        await writer.WriteStartElementAsync("D", "prop", Dav);
        foreach (var name in unsupported)
        {
            await writer.WriteStartElementAsync("D", name, Dav);
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();
        await writer.WriteElementStringAsync("D", "status", Dav, "HTTP/1.1 404 Not Found");
        await writer.WriteEndElementAsync();
    }
}
