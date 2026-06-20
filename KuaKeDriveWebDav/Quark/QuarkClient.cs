using System.Net;
using Microsoft.Extensions.Options;
using SeventyTwo.InfraKit.Autofac;
using SeventyTwo.InfraKit.Cache;
using SeventyTwo.InfraKit.Http;

namespace KuaKeDriveWebDav.Quark;

/// <summary>
/// 夸克网盘客户端实现：参考 OpenList quark_uc 驱动，基于 Cookie 认证调用 drive.quark.cn 接口。
/// 以 Singleton 持有共享 CookieContainer，跨请求维持登录态并把刷新后的 Cookie 持久化到文件。
/// </summary>
[AutofacDependency(typeof(IQuarkClient), ServiceLifetime = ServiceLifetime.Singleton)]
public class QuarkClient : IQuarkClient
{
    private const string ApiBase = "https://drive.quark.cn/1/clouddrive";
    private const string Referer = "https://pan.quark.cn";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "quark-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 "
        + "Safari/537.36 Channel/pckk_other_ch";

    private readonly IHttpService _httpService;
    private readonly ICacheService _cacheService;
    private readonly QuarkOptions _options;

    private readonly CookieContainer _cookieContainer;
    private readonly string _cookieFile;
    private string _lastSavedCookie = string.Empty;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _rootFid;

    public QuarkClient(IHttpService httpService, ICacheService cacheService, IOptions<QuarkOptions> options)
    {
        _httpService = httpService;
        _cacheService = cacheService;
        _options = options.Value;
        _cookieContainer = new CookieContainer();
        _cookieFile = Path.IsPathRooted(_options.CookieFilePath)
            ? _options.CookieFilePath
            : Path.Combine(AppContext.BaseDirectory, _options.CookieFilePath);

        // 优先沿用持久化文件中的最新 Cookie，否则回退到配置中的初始 Cookie
        var fromFile = File.Exists(_cookieFile);
        var initial = fromFile ? File.ReadAllText(_cookieFile).Trim() : _options.Cookie.Trim();
        if (string.IsNullOrEmpty(initial))
            initial = _options.Cookie.Trim();

        // ReSharper disable once InvertIf
        if (!string.IsNullOrEmpty(initial))
        {
            LoadCookieString(initial);
            _lastSavedCookie = GetCurrentCookieString();
            // 来自配置或容器规整后内容与文件不一致时才落盘，避免启动期无意义覆盖写
            if (!fromFile || _lastSavedCookie != initial)
                File.WriteAllText(_cookieFile, _lastSavedCookie);
        }
    }

    /// <inheritdoc />
    public async Task<QuarkFile?> GetByPathAsync(string webDavPath, CancellationToken ct = default)
    {
        var rootFid = await ResolveRootFidAsync(ct);
        var segments = webDavPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return new QuarkFile { Fid = rootFid, IsFile = false };
        return await WalkAsync(rootFid, segments, throwIfMissing: false, ct);
    }

    /// <inheritdoc />
    public Task<List<QuarkFile>> ListChildrenAsync(string fid, CancellationToken ct = default)
    {
        return _cacheService.GetOrCreateCacheAsync(
            $"quark:children:{fid}",
            () => FetchListAsync(fid, ct),
            TimeSpan.FromMinutes(_options.ListCacheMinutes)
        );
    }

    /// <inheritdoc />
    public async Task<string> GetDownloadUrlAsync(string fid, CancellationToken ct = default)
    {
        var body = new { fids = new[] { fid } };
        var resp =
            (
                await _httpService.RequestAsync<QuarkDownloadResp>(
                    BuildRequest("/file/download", HttpMethod.Post, null, body)
                )
            ) ?? throw new InvalidOperationException("夸克接口返回空响应");
        await EnsureSuccessAsync(resp, "获取夸克下载链接失败");
        return resp.Data?.FirstOrDefault()?.DownloadUrl ?? throw new InvalidOperationException("夸克未返回下载链接");
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> OpenDownloadAsync(
        string downloadUrl,
        string? rangeHeader,
        CancellationToken ct = default
    )
    {
        var model = new HttpRequestModel
        {
            UriBuilder = new UriBuilder(downloadUrl),
            HttpMethod = HttpMethod.Get,
            CookieContainer = _cookieContainer,
            Timeout = 600,
            Heads = new Dictionary<string, string> { ["Referer"] = Referer, ["User-Agent"] = UserAgent },
        };
        if (!string.IsNullOrEmpty(rangeHeader))
            model.Heads["Range"] = rangeHeader;
        return await _httpService.RequestAsync(model, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>
    /// 从起始 fid 沿路径逐段匹配子节点（参考 OpenList GetFiles 的逐级定位）
    /// </summary>
    private async Task<QuarkFile?> WalkAsync(
        string startFid,
        string[] segments,
        bool throwIfMissing,
        CancellationToken ct
    )
    {
        var fid = startFid;
        QuarkFile? current = null;
        foreach (var seg in segments)
        {
            var children = await ListChildrenAsync(fid, ct);
            current = children.FirstOrDefault(c => c.FileName == seg);
            if (current is null)
            {
                return throwIfMissing ? throw new InvalidOperationException($"夸克路径解析失败：找不到 {seg}") : null;
            }
            fid = current.Fid;
        }
        return current;
    }

    /// <summary>
    /// 解析 RootPath 为夸克 fid（首次解析后缓存），默认 / 即根目录 fid "0"
    /// </summary>
    private async Task<string> ResolveRootFidAsync(CancellationToken ct)
    {
        if (_rootFid is not null)
            return _rootFid;
        var root = _options.RootPath.Trim('/');
        var fid = "0";
        if (!string.IsNullOrEmpty(root))
        {
            var segments = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
            fid = (await WalkAsync("0", segments, throwIfMissing: true, ct))!.Fid;
        }
        // string 引用赋值原子；并发首次解析最坏各解析一次，结果一致
        _rootFid = fid;
        return fid;
    }

    /// <summary>
    /// 分页拉取目录子项（参考 OpenList GetFiles，GET /file/sort）
    /// </summary>
    private async Task<List<QuarkFile>> FetchListAsync(string parentFid, CancellationToken ct)
    {
        var result = new List<QuarkFile>();
        const int size = 100;
        var page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var query = new Dictionary<string, string>
            {
                ["pdir_fid"] = parentFid,
                ["_page"] = page.ToString(),
                ["_size"] = size.ToString(),
                ["_fetch_total"] = "1",
                ["fetch_all_file"] = "1",
                ["fetch_risk_file_name"] = "1",
            };
            var resp =
                (
                    await _httpService.RequestAsync<QuarkSortResp>(
                        BuildRequest("/file/sort", HttpMethod.Get, query, null)
                    )
                ) ?? throw new InvalidOperationException("夸克接口返回空响应");
            await EnsureSuccessAsync(resp, "夸克列目录失败");
            if (resp.Data?.List is not null)
            {
                foreach (var f in resp.Data.List)
                {
                    f.FileName = WebUtility.HtmlDecode(f.FileName);
                    result.Add(f);
                }
            }
            var total = resp.Metadata?.Total ?? 0;
            if (page * size >= total)
                break;
            page++;
        }
        return result;
    }

    /// <summary>
    /// 刷新 Cookie 持久化并校验夸克响应码
    /// </summary>
    private async Task EnsureSuccessAsync(QuarkResp resp, string errorMessage)
    {
        await PersistCookieIfChangedAsync();
        if (resp.Code != 0)
            throw new InvalidOperationException($"{errorMessage}：{resp.Message}");
    }

    /// <summary>
    /// 构造夸克 API 请求（统一带 Cookie 容器、Accept/Referer/UA、pr/fr 查询参数）
    /// </summary>
    private HttpRequestModel BuildRequest(
        string pathname,
        HttpMethod method,
        Dictionary<string, string>? query,
        object? body
    )
    {
        var ub = new UriBuilder(ApiBase + pathname);
        var q = new Dictionary<string, string> { ["pr"] = "ucpro", ["fr"] = "pc" };
        if (query is not null)
        {
            foreach (var kv in query)
                q[kv.Key] = kv.Value;
        }
        ub.Query = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var model = new HttpRequestModel
        {
            UriBuilder = ub,
            HttpMethod = method,
            CookieContainer = _cookieContainer,
            ResponseContentType = "application/json",
            RetryCount = 2,
            Heads = new Dictionary<string, string>
            {
                ["Accept"] = "application/json, text/plain, */*",
                ["Referer"] = Referer,
                ["User-Agent"] = UserAgent,
            },
        };
        if (body is not null)
            model.HttpContent = JsonContent.Create(body);
        return model;
    }

    /// <summary>
    /// 把 Cookie 字符串注入共享容器（domain 设为 .quark.cn 以覆盖所有子域）
    /// </summary>
    private void LoadCookieString(string cookie)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;
            var name = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            try
            {
                _cookieContainer.Add(new Cookie(name, value) { Domain = ".quark.cn", Path = "/" });
            }
            catch (CookieException)
            {
                // 单个非法值不影响其余 Cookie 加载
            }
        }
    }

    /// <summary>
    /// 从容器提取当前 Cookie 字符串
    /// </summary>
    private string GetCurrentCookieString()
    {
        return string.Join(";", _cookieContainer.GetAllCookies().Select(c => $"{c.Name}={c.Value}"));
    }

    /// <summary>
    /// 当 Cookie 发生变化时持久化到文件
    /// </summary>
    private async Task PersistCookieIfChangedAsync()
    {
        var current = GetCurrentCookieString();
        if (current == _lastSavedCookie)
            return;
        await _lock.WaitAsync();
        try
        {
            if (current == _lastSavedCookie)
                return;
            _lastSavedCookie = current;
            await File.WriteAllTextAsync(_cookieFile, current);
        }
        finally
        {
            _lock.Release();
        }
    }
}
