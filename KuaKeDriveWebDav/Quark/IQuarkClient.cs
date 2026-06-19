namespace KuaKeDriveWebDav.Quark;

/// <summary>
/// 夸克网盘客户端：负责路径解析、列目录、获取下载链接与流式下载
/// </summary>
public interface IQuarkClient
{
    /// <summary>
    /// 按 WebDAV 相对路径解析夸克节点，找不到返回 null；根路径返回合成的目录节点
    /// </summary>
    Task<QuarkFile?> GetByPathAsync(string webDavPath, CancellationToken ct = default);

    /// <summary>
    /// 列出指定 fid 目录的直接子项（带缓存）
    /// </summary>
    Task<List<QuarkFile>> ListChildrenAsync(string fid, CancellationToken ct = default);

    /// <summary>
    /// 获取文件下载直链
    /// </summary>
    Task<string> GetDownloadUrlAsync(string fid, CancellationToken ct = default);

    /// <summary>
    /// 以流方式打开下载（带 Cookie/Referer/UA，透传 Range），返回上游响应（调用方负责释放）
    /// </summary>
    Task<HttpResponseMessage> OpenDownloadAsync(string downloadUrl, string? rangeHeader, CancellationToken ct = default);
}
