using System.Xml.Linq;
using Microsoft.AspNetCore.StaticFiles;

namespace KuaKeDriveWebDav.WebDav;

// WebDavMiddleware 为终端中间件，主构造函数的 RequestDelegate next 不调用
#pragma warning disable CS9113

/// <summary>
/// WebDAV 终端中间件：分发 OPTIONS / PROPFIND / GET / HEAD（只读）以及本地存储的写方法
/// </summary>
public class WebDavMiddleware(RequestDelegate next, IWebDavStoreResolver resolver)
{
    private const string ReadMethods = "OPTIONS, PROPFIND, GET, HEAD";
    private const string WriteMethods = "PUT, MKCOL, DELETE, MOVE, COPY";

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    /// <summary>中间件入口：按 HTTP 方法分发，未支持的方法返回 405</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var store = resolver.Resolve(context);
        if (store is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var method = context.Request.Method.ToUpperInvariant();
        var canWrite = store.Capabilities.HasFlag(WebDavCapabilities.Write);
        if (!canWrite && method is "PUT" or "MKCOL" or "DELETE" or "MOVE" or "COPY")
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = BuildAllow(store);
            return;
        }

        try
        {
            switch (method)
            {
                case "OPTIONS":
                    HandleOptions(context, store);
                    return;
                case "PROPFIND":
                    await HandlePropfindAsync(context, store);
                    return;
                case "HEAD":
                    await HandleHeadAsync(context, store);
                    return;
                case "GET":
                    await HandleGetAsync(context, store);
                    return;
                case "PUT":
                    await HandlePutAsync(context, store);
                    return;
                case "MKCOL":
                    await HandleMkcolAsync(context, store);
                    return;
                case "DELETE":
                    await HandleDeleteAsync(context, store);
                    return;
                case "MOVE":
                    await HandleMoveAsync(context, store);
                    return;
                case "COPY":
                    await HandleCopyAsync(context, store);
                    return;
                default:
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers.Allow = BuildAllow(store);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DirectoryNotFoundException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>响应 OPTIONS 能力探测，声明允许的方法与 DAV 等级</summary>
    private static void HandleOptions(HttpContext context, IWebDavStore store)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.Allow = BuildAllow(store);
        context.Response.Headers["DAV"] = "1, 2";
        context.Response.Headers["MS-Author-Via"] = "DAV";
    }

    /// <summary>处理 PROPFIND：返回当前路径及其子节点的 multistatus 属性</summary>
    private async Task HandlePropfindAsync(HttpContext context, IWebDavStore store)
    {
        var ct = context.RequestAborted;
        var path = context.Request.Path.Value ?? "/";
        var node = await store.GetByPathAsync(path, ct);
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var depth = context.Request.Headers["Depth"].ToString();
        var includeChildren = node.IsDirectory && !string.Equals(depth, "0", StringComparison.OrdinalIgnoreCase);

        var requested = await ParseRequestedPropsAsync(context.Request.Body, ct);
        var hrefBase = (context.Request.PathBase.Value ?? "") + path;
        var hrefWithSlash = EnsureTrailingSlash(hrefBase);

        var responses = new List<MultistatusResponse> { ToResponse(store, hrefBase, node) };
        if (includeChildren)
        {
            foreach (var child in await store.ListChildrenAsync(node, ct))
                responses.Add(ToResponse(store, hrefWithSlash + child.Name, child));
        }

        context.Response.StatusCode = StatusCodes.Status207MultiStatus;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await WebDavMultistatus.WriteAsync(context.Response.Body, responses, requested, ct);
    }

    /// <summary>处理 HEAD：返回文件元信息头，不输出正文</summary>
    private async Task HandleHeadAsync(HttpContext context, IWebDavStore store)
    {
        var node = await store.GetByPathAsync(context.Request.Path.Value ?? "/", context.RequestAborted);
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        if (node.IsDirectory)
            return;
        context.Response.ContentLength = node.Size;
        context.Response.ContentType = GuessContentType(node.Name);
        context.Response.Headers.LastModified = WebDavMultistatus.FormatRfc1123(node.UpdatedAt);
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = store.GetEtag(node);
    }

    /// <summary>处理 GET：打开数据源读取流并透传，支持 Range 断点续传</summary>
    private async Task HandleGetAsync(HttpContext context, IWebDavStore store)
    {
        var ct = context.RequestAborted;
        var node = await store.GetByPathAsync(context.Request.Path.Value ?? "/", ct);
        if (node is null || node.IsDirectory)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var range = context.Request.Headers.Range.ToString();
        await using var content = await store.OpenReadAsync(node, string.IsNullOrEmpty(range) ? null : range, ct);
        context.Response.StatusCode = content.StatusCode;
        context.Response.ContentType = content.ContentType ?? GuessContentType(node.Name);
        if (content.ContentLength is not null)
            context.Response.ContentLength = content.ContentLength;
        if (content.ContentRange is not null)
            context.Response.Headers.ContentRange = content.ContentRange;
        context.Response.Headers.AcceptRanges = "bytes";
        // 1MB 缓冲，降低大文件流式透传的拷贝开销
        await content.Stream.CopyToAsync(context.Response.Body, 1024 * 1024, ct);
    }

    /// <summary>处理 PUT：上传/覆盖文件，统一返回 200</summary>
    private async Task HandlePutAsync(HttpContext context, IWebDavStore store)
    {
        var path = context.Request.Path.Value ?? "/";
        await store.PutAsync(path, context.Request.Body, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>处理 MKCOL：创建目录，成功返回 201</summary>
    private async Task HandleMkcolAsync(HttpContext context, IWebDavStore store)
    {
        var path = context.Request.Path.Value ?? "/";
        await store.MkcolAsync(path, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    /// <summary>处理 DELETE：删除资源，成功 204，不存在 404</summary>
    private async Task HandleDeleteAsync(HttpContext context, IWebDavStore store)
    {
        var path = context.Request.Path.Value ?? "/";
        var ok = await store.DeleteAsync(path, context.RequestAborted);
        context.Response.StatusCode = ok ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
    }

    /// <summary>处理 MOVE：在同一 store 内移动，目标前缀不符返回 400</summary>
    private async Task HandleMoveAsync(HttpContext context, IWebDavStore store)
    {
        var (sourcePath, destPath, overwrite) = ParseSourceDest(context);
        if (destPath is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "缺少或非法的 Destination 头");
            return;
        }
        await store.MoveAsync(sourcePath, destPath, overwrite, context.RequestAborted);
        context.Response.StatusCode = overwrite ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    /// <summary>处理 COPY：在同一 store 内复制，目标前缀不符返回 400</summary>
    private async Task HandleCopyAsync(HttpContext context, IWebDavStore store)
    {
        var (sourcePath, destPath, overwrite) = ParseSourceDest(context);
        if (destPath is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "缺少或非法的 Destination 头");
            return;
        }
        await store.CopyAsync(sourcePath, destPath, overwrite, context.RequestAborted);
        context.Response.StatusCode = overwrite ? StatusCodes.Status204NoContent : StatusCodes.Status201Created;
    }

    /// <summary>解析 MOVE/COPY 的源路径、目标路径（须同属当前 store 前缀）与 Overwrite 头</summary>
    private (string SourcePath, string? DestPath, bool Overwrite) ParseSourceDest(HttpContext context)
    {
        var sourcePath = context.Request.Path.Value ?? "/";
        var destination = context.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destination))
            return (sourcePath, null, Overwrite: false);

        var destUri = new Uri(destination, UriKind.RelativeOrAbsolute);
        var absolutePath = destUri.IsAbsoluteUri ? destUri.AbsolutePath : destUri.ToString();
        var pathBase = context.Request.PathBase.Value ?? "";
        if (!absolutePath.StartsWith(pathBase, StringComparison.OrdinalIgnoreCase))
            return (sourcePath, null, Overwrite: false);

        var overwrite = !string.Equals(
            context.Request.Headers["Overwrite"].ToString(),
            "F",
            StringComparison.OrdinalIgnoreCase
        );
        var destPath = absolutePath[pathBase.Length..];
        if (!destPath.StartsWith('/'))
            destPath = "/" + destPath;
        return (sourcePath, destPath, overwrite);
    }

    /// <summary>按数据源能力拼接 Allow 头</summary>
    private static string BuildAllow(IWebDavStore store) =>
        store.Capabilities.HasFlag(WebDavCapabilities.Write) ? $"{ReadMethods}, {WriteMethods}" : ReadMethods;

    /// <summary>将数据源节点转换为 WebDAV multistatus 响应项</summary>
    private static MultistatusResponse ToResponse(IWebDavStore store, string href, WebDavNode node)
    {
        var normalized = node.IsDirectory && !href.EndsWith('/') ? href + "/" : href;
        return new MultistatusResponse
        {
            Href = EncodePath(normalized),
            Name = node.Name,
            IsDirectory = node.IsDirectory,
            Size = node.Size,
            UpdatedAt = node.UpdatedAt,
            CreatedAt = node.CreatedAt,
            ContentType = GuessContentType(node.Name),
            Etag = store.GetEtag(node),
        };
    }

    /// <summary>
    /// 解析 PROPFIND 请求体，返回客户端请求的属性名集合；null 表示 allprop（返回全集）
    /// </summary>
    private static async Task<HashSet<string>?> ParseRequestedPropsAsync(Stream body, CancellationToken ct)
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

    /// <summary>对路径各段做 URL 编码，保留分隔符</summary>
    private static string EncodePath(string path) =>
        string.Join('/', path.Split('/').Select(s => s.Length == 0 ? s : Uri.EscapeDataString(s)));

    /// <summary>确保路径以 / 结尾</summary>
    private static string EnsureTrailingSlash(string path) => path.EndsWith('/') ? path : path + "/";

    /// <summary>根据文件名推断 MIME 类型，未知扩展名回退为 application/octet-stream</summary>
    private static string GuessContentType(string fileName)
    {
        if (!ContentTypeProvider.TryGetContentType(fileName, out var contentType))
            contentType = "application/octet-stream";
        return contentType;
    }

    /// <summary>以纯文本写出错误响应</summary>
    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message, context.RequestAborted);
    }
}
