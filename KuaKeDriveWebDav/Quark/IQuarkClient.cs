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
    /// 以流方式打开文件下载（带 Cookie/Referer/UA，透传 Range）：内部按 fid 取直链并缓存，
    /// 直链失效时自动清缓存重取一次。返回上游响应（调用方负责释放）
    /// </summary>
    Task<HttpResponseMessage> OpenDownloadAsync(string fid, string? rangeHeader, CancellationToken ct = default);

    /// <summary>
    /// 用传入的 cookie 字符串整体替换当前登录态并持久化，随后调用一次列目录验证有效性；
    /// 验证失败时 cookie 已写入，异常向上抛出由调用方反馈
    /// </summary>
    Task UpdateCookieAsync(string cookie, CancellationToken ct = default);
}
