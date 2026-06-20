namespace KuaKeDriveWebDav;

/// <summary>
/// 夸克网盘相关配置（对应 appsettings.json 的 Quark 节）
/// </summary>
public class QuarkOptions
{
    /// <summary>Cookie 持久化文件路径，默认 cookie/quark-cookie.txt（相对当前工作目录）</summary>
    public string CookieFilePath { get; set; } = "cookie/quark-cookie.txt";

    /// <summary>映射为 WebDAV 根的夸克路径，默认 / 即夸克根目录</summary>
    public string RootPath { get; set; } = "/";

    /// <summary>目录列表缓存分钟数</summary>
    public int ListCacheMinutes { get; set; } = 2;
}

/// <summary>
/// WebDAV 服务相关配置（对应 appsettings.json 的 WebDav 节）
/// </summary>
public class WebDavOptions
{
    /// <summary>夸克网盘路由前缀，默认 /dav/kuake</summary>
    public string QuarkPrefix { get; set; } = "/dav/kuake";

    /// <summary>本地存储路由前缀，默认 /dav/local</summary>
    public string LocalPrefix { get; set; } = "/dav/local";

    /// <summary>Basic Auth 用户名（两组路由共用）</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Basic Auth 密码（两组路由共用）</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 本地存储相关配置（对应 appsettings.json 的 Local 节）
/// </summary>
public class LocalOptions
{
    /// <summary>本地存储根目录（绝对路径，或相对当前工作目录）</summary>
    public string RootPath { get; set; } = "local-root";
}
