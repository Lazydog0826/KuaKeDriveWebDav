using System.Xml.Linq;
using KuaKeDriveWebDav.Quark;
using Microsoft.AspNetCore.StaticFiles;

namespace KuaKeDriveWebDav.WebDav;

// WebDavMiddleware 为终端中间件，主构造函数的 RequestDelegate next 不调用
#pragma warning disable CS9113

/// <summary>
/// WebDAV 终端中间件：分发 OPTIONS / PROPFIND / GET / HEAD（只读）
/// </summary>
public class WebDavMiddleware(RequestDelegate next, IQuarkClient quark)
{
    private const string AllowedMethods = "OPTIONS, PROPFIND, GET, HEAD";

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            switch (context.Request.Method.ToUpperInvariant())
            {
                case "OPTIONS":
                    await HandleOptionsAsync(context);
                    return;
                case "PROPFIND":
                    await HandlePropfindAsync(context);
                    return;
                case "HEAD":
                    await HandleHeadAsync(context);
                    return;
                case "GET":
                    await HandleGetAsync(context);
                    return;
                default:
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers.Allow = AllowedMethods;
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    private static Task HandleOptionsAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.Allow = AllowedMethods;
        context.Response.Headers["DAV"] = "1, 2";
        context.Response.Headers["MS-Author-Via"] = "DAV";
        return Task.CompletedTask;
    }

    private async Task HandlePropfindAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var path = context.Request.Path.Value ?? "/";
        var node = await quark.GetByPathAsync(path, ct);
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var depth = context.Request.Headers["Depth"].ToString();
        var includeChildren =
            node.IsDirectory && !string.Equals(depth, "0", StringComparison.OrdinalIgnoreCase);

        var requested = await ParseRequestedPropsAsync(context.Request.Body, ct);
        var hrefBase = (context.Request.PathBase.Value ?? "") + path;
        var hrefWithSlash = EnsureTrailingSlash(hrefBase);

        var responses = new List<MultistatusResponse> { ToResponse(hrefBase, node) };
        if (includeChildren)
        {
            foreach (var child in await quark.ListChildrenAsync(node.Fid, ct))
                responses.Add(ToResponse(hrefWithSlash + child.FileName, child));
        }

        context.Response.StatusCode = StatusCodes.Status207MultiStatus;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await WebDavMultistatus.WriteAsync(context.Response.Body, responses, requested, ct);
    }

    private async Task HandleHeadAsync(HttpContext context)
    {
        var node = await quark.GetByPathAsync(
            context.Request.Path.Value ?? "/",
            context.RequestAborted
        );
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        if (node.IsDirectory)
            return;
        context.Response.ContentLength = node.Size;
        context.Response.ContentType = GuessContentType(node.FileName);
        context.Response.Headers.LastModified = WebDavMultistatus.FormatRfc1123(node.UpdatedAt);
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = MakeEtag(node);
    }

    private async Task HandleGetAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var node = await quark.GetByPathAsync(context.Request.Path.Value ?? "/", ct);
        if (node is null || node.IsDirectory)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var range = context.Request.Headers.Range.ToString();

        // 直链可能过期失效，失败时重新获取一次再下载
        async Task<HttpResponseMessage> OpenFreshAsync() =>
            await quark.OpenDownloadAsync(await quark.GetDownloadUrlAsync(node.Fid, ct), range, ct);

        HttpResponseMessage upstream;
        try
        {
            upstream = await OpenFreshAsync();
        }
        catch (HttpRequestException)
        {
            upstream = await OpenFreshAsync();
        }

        using (upstream)
        {
            context.Response.StatusCode = (int)upstream.StatusCode;
            context.Response.ContentType =
                upstream.Content.Headers.ContentType?.ToString() ?? GuessContentType(node.FileName);
            if (upstream.Content.Headers.ContentLength is not null)
                context.Response.ContentLength = upstream.Content.Headers.ContentLength;
            if (upstream.Content.Headers.ContentRange is not null)
                context.Response.Headers.ContentRange = upstream
                    .Content.Headers.ContentRange.ToString();
            context.Response.Headers.AcceptRanges = "bytes";
            await upstream.Content.CopyToAsync(context.Response.Body, ct);
        }
    }

    private static MultistatusResponse ToResponse(string href, QuarkFile file)
    {
        var isDir = !file.IsFile;
        var normalized = isDir && !href.EndsWith('/') ? href + "/" : href;
        return new MultistatusResponse
        {
            Href = EncodePath(normalized),
            Name = file.FileName,
            IsDirectory = isDir,
            Size = file.Size,
            UpdatedAt = file.UpdatedAt,
            CreatedAt = file.CreatedAt,
            ContentType = GuessContentType(file.FileName),
            Etag = MakeEtag(file),
        };
    }

    /// <summary>
    /// 解析 PROPFIND 请求体，返回客户端请求的属性名集合；null 表示 allprop（返回全集）
    /// </summary>
    private static async Task<HashSet<string>?> ParseRequestedPropsAsync(
        Stream body,
        CancellationToken ct
    )
    {
        XDocument doc;
        try
        {
            doc = await XDocument.LoadAsync(body, LoadOptions.None, ct);
        }
        catch
        {
            return null;
        }
        var propfind = doc.Root;
        if (propfind is null)
            return null;
        var ns = propfind.Name.Namespace;
        if (propfind.Element(ns + "allprop") is not null)
            return null;
        var prop = propfind.Element(ns + "prop");
        if (prop is null)
            return null;
        var names = prop.Elements().Select(e => e.Name.LocalName);
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? null : set;
    }

    private static string EncodePath(string path) =>
        string.Join(
            '/',
            path.Split('/').Select(s => s.Length == 0 ? s : Uri.EscapeDataString(s))
        );

    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith('/') ? path : path + "/";

    private static string GuessContentType(string fileName)
    {
        if (!ContentTypeProvider.TryGetContentType(fileName, out var contentType))
            contentType = "application/octet-stream";
        return contentType;
    }

    private static string MakeEtag(QuarkFile file) => $"\"{file.Fid}-{file.Size}-{file.UpdatedAt}\"";

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message, context.RequestAborted);
    }
}
