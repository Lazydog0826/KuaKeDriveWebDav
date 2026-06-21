namespace KuaKeDriveWebDav.WebDav;

/// <summary>
/// WebDAV 统一节点模型：屏蔽夸克与本地存储的数据源差异
/// </summary>
public sealed record WebDavNode
{
    /// <summary>节点唯一标识，由各 store 自行解释（夸克为 fid，本地为相对根目录路径）</summary>
    public required string Id { get; init; }

    /// <summary>节点显示名（文件名/目录名），根节点为空串</summary>
    public required string Name { get; init; }

    /// <summary>true 为目录，false 为文件</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>文件字节数，目录为 0</summary>
    public long Size { get; init; }

    /// <summary>更新时间（毫秒时间戳），未知为 0</summary>
    public long UpdatedAt { get; init; }

    /// <summary>创建时间（毫秒时间戳），未知为 0</summary>
    public long CreatedAt { get; init; }
}

/// <summary>
/// WebDAV 文件读取结果：封装响应状态、元信息头与可释放的内容流，屏蔽夸克直链与本地文件流差异
/// </summary>
public sealed class WebDavContent : IDisposable, IAsyncDisposable
{
    /// <summary>HTTP 响应状态码</summary>
    public required int StatusCode { get; init; }

    /// <summary>Content-Type，未知为 null（由中间件按文件名推断）</summary>
    public string? ContentType { get; init; }

    /// <summary>Content-Length（字节数），无（如分块传输）为 null</summary>
    public long? ContentLength { get; init; }

    /// <summary>Content-Range 头原值（如 "bytes 0-99/1000"），非范围请求为 null</summary>
    public string? ContentRange { get; init; }

    /// <summary>实际内容流，调用方负责随 WebDavContent 一并释放</summary>
    public required Stream Stream { get; init; }

    /// <summary>附加需释放资源（夸克 store 传上游 HttpResponseMessage，本地传 null）</summary>
    public IDisposable? Owner { get; init; }

    public void Dispose()
    {
        Stream.Dispose();
        Owner?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        if (Owner is IAsyncDisposable asyncOwner)
            await asyncOwner.DisposeAsync();
        else
            Owner?.Dispose();
    }
}

/// <summary>
/// WebDAV Range 不可满足异常，用于返回 416 Range Not Satisfiable
/// </summary>
public sealed class WebDavRangeNotSatisfiableException(long totalSize, string message) : Exception(message)
{
    /// <summary>资源总长度</summary>
    public long TotalSize { get; } = totalSize;
}

/// <summary>
/// WebDAV 数据源支持的能力标志，决定 OPTIONS 响应的 Allow 头与写方法可用性
/// </summary>
[Flags]
public enum WebDavCapabilities
{
    None = 0,

    /// <summary>只读：OPTIONS / PROPFIND / GET / HEAD</summary>
    Read = 1,

    /// <summary>可写：PUT / MKCOL / DELETE / MOVE / COPY</summary>
    Write = 2,
}

/// <summary>
/// WebDAV 数据源抽象：统一夸克（只读）与本地存储（可读写）的节点访问与读写
/// </summary>
public interface IWebDavStore
{
    /// <summary>该 store 支持的能力（决定 OPTIONS 的 Allow 头与写方法可用性）</summary>
    WebDavCapabilities Capabilities { get; }

    /// <summary>按 WebDAV 相对路径解析节点，找不到返回 null；根路径返回合成的目录节点</summary>
    Task<WebDavNode?> GetByPathAsync(string webDavPath, CancellationToken ct = default);

    /// <summary>列出目录节点的直接子项；node 非目录时返回空列表</summary>
    Task<IReadOnlyList<WebDavNode>> ListChildrenAsync(WebDavNode node, CancellationToken ct = default);

    /// <summary>打开文件读取流（含 range 处理），调用方负责释放返回的 WebDavContent</summary>
    Task<WebDavContent> OpenReadAsync(WebDavNode node, string? rangeHeader, CancellationToken ct = default);

    /// <summary>生成节点 ETag（夸克用 fid，本地用文件指纹），由各 store 自行实现</summary>
    string GetEtag(WebDavNode node);

    /// <summary>上传/覆盖文件，返回写入后的节点；父目录不存在抛异常</summary>
    Task<WebDavNode> PutAsync(string webDavPath, Stream content, CancellationToken ct = default) =>
        throw new NotSupportedException("该数据源不支持写入");

    /// <summary>创建目录，父目录不存在抛异常；已存在抛异常</summary>
    Task<WebDavNode> MkcolAsync(string webDavPath, CancellationToken ct = default) =>
        throw new NotSupportedException("该数据源不支持写入");

    /// <summary>删除文件或目录；删除不存在资源返回 false</summary>
    Task<bool> DeleteAsync(string webDavPath, CancellationToken ct = default) =>
        throw new NotSupportedException("该数据源不支持写入");

    /// <summary>移动文件或目录到同 store 内的目标路径；目标存在且不允许覆盖时抛异常</summary>
    Task<WebDavNode> MoveAsync(string sourcePath, string destPath, bool overwrite, CancellationToken ct = default) =>
        throw new NotSupportedException("该数据源不支持写入");

    /// <summary>复制文件或目录到同 store 内的目标路径；目标存在且不允许覆盖时抛异常</summary>
    Task<WebDavNode> CopyAsync(string sourcePath, string destPath, bool overwrite, CancellationToken ct = default) =>
        throw new NotSupportedException("该数据源不支持写入");
}
