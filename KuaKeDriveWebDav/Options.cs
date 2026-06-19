namespace KuaKeDriveWebDav;

/// <summary>
/// 夸克网盘相关配置（对应 appsettings.json 的 Quark 节）
/// </summary>
public class QuarkOptions
{
    /// <summary>初始夸克登录 Cookie 字符串（浏览器复制）</summary>
    public string Cookie { get; set; } = string.Empty;

    /// <summary>Cookie 持久化文件路径，默认 quark-cookie.txt（相对程序目录）</summary>
    public string CookieFilePath { get; set; } = "quark-cookie.txt";

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
    /// <summary>WebDAV 路径前缀</summary>
    public string Prefix { get; set; } = "/dav";

    /// <summary>Basic Auth 用户名</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Basic Auth 密码</summary>
    public string Password { get; set; } = string.Empty;
}
