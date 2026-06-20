using KuaKeDriveWebDav.WebDav;
using SeventyTwo.InfraKit.Autofac;

namespace KuaKeDriveWebDav.Quark;

/// <summary>
/// 夸克 WebDAV 数据源：包装 IQuarkClient，把夸克直链两步下载与失效重试下沉至此，对外只读
/// </summary>
[AutofacDependency(typeof(QuarkWebDavStore), ServiceLifetime = ServiceLifetime.Singleton)]
public sealed class QuarkWebDavStore(IQuarkClient quark) : IWebDavStore
{
    /// <inheritdoc />
    public WebDavCapabilities Capabilities => WebDavCapabilities.Read;

    /// <inheritdoc />
    public async Task<WebDavNode?> GetByPathAsync(string webDavPath, CancellationToken ct = default)
    {
        var file = await quark.GetByPathAsync(webDavPath, ct);
        return file is null ? null : ToNode(file);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebDavNode>> ListChildrenAsync(WebDavNode node, CancellationToken ct = default)
    {
        var children = await quark.ListChildrenAsync(node.Id, ct);
        return children.Select(ToNode).ToList();
    }

    /// <inheritdoc />
    public async Task<WebDavContent> OpenReadAsync(WebDavNode node, string? rangeHeader, CancellationToken ct = default)
    {
        // 直链可能过期失效，失败时重新获取一次再下载
        async Task<HttpResponseMessage> OpenFreshAsync() =>
            await quark.OpenDownloadAsync(await quark.GetDownloadUrlAsync(node.Id, ct), rangeHeader, ct);

        HttpResponseMessage upstream;
        try
        {
            upstream = await OpenFreshAsync();
        }
        catch (HttpRequestException)
        {
            upstream = await OpenFreshAsync();
        }

        var stream = await upstream.Content.ReadAsStreamAsync(ct);
        return new WebDavContent
        {
            StatusCode = (int)upstream.StatusCode,
            ContentType = upstream.Content.Headers.ContentType?.ToString(),
            ContentLength = upstream.Content.Headers.ContentLength,
            ContentRange = upstream.Content.Headers.ContentRange?.ToString(),
            Stream = stream,
            Owner = upstream,
        };
    }

    /// <inheritdoc />
    public string GetEtag(WebDavNode node) => $"\"{node.Id}-{node.Size}-{node.UpdatedAt}\"";

    private static WebDavNode ToNode(QuarkFile file) =>
        new()
        {
            Id = file.Fid,
            Name = file.FileName,
            IsDirectory = file.IsDirectory,
            Size = file.Size,
            UpdatedAt = file.UpdatedAt,
            CreatedAt = file.CreatedAt,
        };
}
