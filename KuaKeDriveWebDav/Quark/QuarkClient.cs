using System.Net;
using System.Net.Http.Headers;
using KuaKeDriveWebDav.WebDav;
using Microsoft.Extensions.Options;
using SeventyTwo.InfraKit.Autofac;
using SeventyTwo.InfraKit.Cache;
using SeventyTwo.InfraKit.Core;
using SeventyTwo.InfraKit.Http;

// ReSharper disable MemberCanBeMadeStatic.Local

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

    // 夸克对单连接下载大文件强制限速（约 100KB/s，会员亦然），需多连接并发突破；
    // OpenList 采用 3 路并发、单片 10MB；这里调小单片以适配 WebDAV 播放器常见的中小 Range 请求。
    private const int DownloadConcurrency = 3;
    private const int DownloadPartSize = 4 * 1024 * 1024;
    private const int MaxUpstreamConcurrency = 12;
    private const int PooledConnectionLifetimeMinutes = 10;
    private const int PooledConnectionIdleMinutes = 2;
    private const int ConnectTimeoutSeconds = 15;

    private readonly IHttpService _httpService;
    private readonly IRedisCacheService _redisCacheService;
    private readonly CacheConfiguration _cacheConfiguration;
    private readonly ILogger<QuarkClient> _logger;
    private readonly QuarkOptions _options;

    private CookieContainer _cookieContainer;
    private readonly string _cookieFile;
    private string _lastSavedCookie = string.Empty;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(MaxUpstreamConcurrency, MaxUpstreamConcurrency);

    private string? _rootFid;

    // 下载专用 HttpClient：底层 SocketsHttpHandler 绑定共享 CookieContainer，常驻复用连接池，
    // 避免 IHttpService 对带 Cookie 请求每次 new HttpClient 导致 TCP/TLS 重建与慢启动跑不满带宽。
    // 仅用于流式下载；列目录、取直链等小请求仍走 IHttpService。
    private HttpClient _httpClient;

    public QuarkClient(
        IHttpService httpService,
        IRedisCacheService redisCacheService,
        IOptions<CacheConfiguration> cacheOptions,
        IOptions<QuarkOptions> options,
        ILogger<QuarkClient> logger
    )
    {
        _httpService = httpService;
        _redisCacheService = redisCacheService;
        _cacheConfiguration = cacheOptions.Value;
        _logger = logger;
        _options = options.Value;
        _cookieContainer = new CookieContainer();
        _cookieFile = Path.IsPathRooted(_options.CookieFilePath)
            ? _options.CookieFilePath
            : Path.Combine(Environment.CurrentDirectory, _options.CookieFilePath);
        _httpClient = CreateHttpClient();

        // 沿用持久化文件中的最新 Cookie（不存在或为空则保持空容器）
        if (!File.Exists(_cookieFile))
            return;
        var initial = File.ReadAllText(_cookieFile).Trim();
        if (string.IsNullOrEmpty(initial))
            return;
        LoadCookieString(initial);
        _lastSavedCookie = GetCurrentCookieString();
        // 容器规整后内容与文件不一致时才落盘，避免启动期无意义覆盖写
        if (_lastSavedCookie != initial)
            File.WriteAllText(_cookieFile, _lastSavedCookie);
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
        return _redisCacheService.GetOrCreateCacheAsync(
            $"quark:children:{fid}",
            () => FetchListAsync(fid, ct),
            TimeSpan.FromMinutes(_options.ListCacheMinutes)
        );
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> OpenDownloadAsync(
        string fid,
        long totalSize,
        string? rangeHeader,
        CancellationToken ct = default
    )
    {
        var (start, length, hasRange) = ParseRange(rangeHeader, totalSize);
        _logger.LogInformation(
            "打开夸克下载 fid={Fid}, totalSize={TotalSize}, range={Range}, start={Start}, length={Length}",
            fid,
            totalSize,
            string.IsNullOrEmpty(rangeHeader) ? "-" : rangeHeader,
            start,
            length
        );

        // 非 Range 小文件走单流；Range 请求统一走可重试的分片流，哪怕只有一片也能在 body 卡住时重开。
        // 大范围把整段按 PartSize 切片、Concurrency 路并发拉取后按序拼合，用多条上游连接
        // 绕开夸克对单连接大文件的限速（参考 OpenList 夸克驱动 3 并发 × 10MB）。
        long? rangeStart = hasRange ? start : null;
        long? rangeEnd = hasRange ? start + length - 1 : null;
        if ((!hasRange && length <= DownloadPartSize) || DownloadConcurrency <= 1)
            return await DownloadWithRetryAsync(
                fid,
                (url, token) => OpenDownloadByUrlAsync(url, rangeStart, rangeEnd, token),
                hasRange,
                ct
            );

        var stream = new QuarkParallelDownloadStream(
            (s, l, token) =>
                DownloadWithRetryAsync(fid, (url, t) => OpenDownloadByUrlAsync(url, s, s + l - 1, t), true, token),
            start,
            length,
            DownloadPartSize,
            DownloadConcurrency,
            _logger,
            ct
        );
        return BuildRangeResponse(stream, start, length, totalSize, hasRange);
    }

    /// <summary>
    /// 解析 Range 头为（起始偏移、长度、是否带 Range）；无 Range 按整文件处理，非法 Range 抛出 416 异常
    /// </summary>
    private static (long Start, long Length, bool HasRange) ParseRange(string? rangeHeader, long totalSize)
    {
        if (string.IsNullOrEmpty(rangeHeader))
            return (0, totalSize, false);
        if (!RangeHeaderValue.TryParse(rangeHeader, out var parsed))
            throw new WebDavRangeNotSatisfiableException(totalSize, "请求的 Range 超出资源范围");

        if (totalSize <= 0 || parsed.Ranges.Count != 1)
            throw new WebDavRangeNotSatisfiableException(totalSize, "请求的 Range 超出资源范围");

        var r = parsed.Ranges.First();
        if (r.From is not null && r.To is not null)
        {
            var start = r.From.Value;
            var end = r.To.Value;
            if (start < 0 || end < start || start >= totalSize)
                throw new WebDavRangeNotSatisfiableException(totalSize, "请求的 Range 超出资源范围");
            end = Math.Min(end, totalSize - 1);
            return (start, end - start + 1, true);
        }
        if (r.From is not null)
        {
            var start = r.From.Value;
            if (start < 0 || start >= totalSize)
                throw new WebDavRangeNotSatisfiableException(totalSize, "请求的 Range 超出资源范围");
            return (start, totalSize - start, true);
        }
        if (r.To is not null)
        {
            var len = Math.Min(r.To.Value, totalSize);
            if (len <= 0)
                throw new WebDavRangeNotSatisfiableException(totalSize, "请求的 Range 超出资源范围");
            return (totalSize - len, len, true);
        }
        throw new WebDavRangeNotSatisfiableException(totalSize, "请求的 Range 超出资源范围");
    }

    /// <summary>
    /// 取直链下载，直链失效（HttpRequestException 或非成功状态码）时清缓存重取一次重试；
    /// 单流与分片各片都经此统一重试，opener 决定如何用直链发起请求
    /// </summary>
    private async Task<HttpResponseMessage> DownloadWithRetryAsync(
        string fid,
        Func<string, CancellationToken, Task<HttpResponseMessage>> opener,
        bool requirePartialContent,
        CancellationToken ct
    )
    {
        var retried = false;
        while (true)
        {
            var url = await GetDownloadUrlAsync(fid);
            try
            {
                var resp = await opener(url, ct);
                if (
                    resp.IsSuccessStatusCode
                    && (!requirePartialContent || resp.StatusCode == HttpStatusCode.PartialContent)
                )
                    return resp;

                var statusCode = resp.StatusCode;
                _logger.LogWarning(
                    "夸克下载响应异常，准备重试 fid={Fid}, statusCode={StatusCode}, requirePartialContent={RequirePartialContent}",
                    fid,
                    (int)statusCode,
                    requirePartialContent
                );
                resp.Dispose();
                throw new HttpRequestException($"夸克下载失败，状态码：{(int)statusCode}", null, statusCode);
            }
            catch (HttpRequestException ex) when (!retried)
            {
                retried = true;
                _logger.LogWarning(ex, "夸克下载失败，清除直链缓存后重试 fid={Fid}", fid);
                await _redisCacheService.DeleteCacheAsync(_cacheConfiguration.Wrapped(DownloadUrlKey(fid)));
            }
            catch (OperationCanceledException ex) when (!retried && !ct.IsCancellationRequested)
            {
                retried = true;
                _logger.LogWarning(ex, "夸克下载连接超时，清除直链缓存后重试 fid={Fid}", fid);
                await _redisCacheService.DeleteCacheAsync(_cacheConfiguration.Wrapped(DownloadUrlKey(fid)));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "夸克下载重试后仍失败 fid={Fid}", fid);
                throw;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "夸克下载连接超时，重试后仍失败 fid={Fid}", fid);
                throw new HttpRequestException("夸克下载连接超时，重试后仍失败", ex);
            }
        }
    }

    /// <summary>
    /// 按 fid 获取下载直链，经 IRedisCacheService 缓存（TTL 内复用，避免并发 Range 请求逐个回源）
    /// </summary>
    private Task<string> GetDownloadUrlAsync(string fid) =>
        _redisCacheService.GetOrCreateCacheAsync(
            DownloadUrlKey(fid),
            () => FetchDownloadUrlAsync(fid),
            TimeSpan.FromMinutes(_options.DownloadUrlCacheMinutes)
        );

    /// <summary>直链缓存 key</summary>
    private string DownloadUrlKey(string fid) => $"quark:dl:{fid}";

    /// <summary>
    /// 用直链发起流式下载：经共享 CookieContainer 的常驻 HttpClient 复用连接池，Cookie 由 handler 按 URL 域自动注入。
    /// 仅读到响应头即返回，响应体由调用方流式读取；start/end 非 null 时附带 Range 头。
    /// </summary>
    private async Task<HttpResponseMessage> OpenDownloadByUrlAsync(
        string downloadUrl,
        long? start,
        long? end,
        CancellationToken ct
    )
    {
        await _downloadGate.WaitAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Add("Referer", Referer);
        request.Headers.Add("User-Agent", UserAgent);
        if (start is not null || end is not null)
            request.Headers.Range = new RangeHeaderValue(start, end);
        try
        {
            var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.Content = new SemaphoreReleasingHttpContent(resp.Content, _downloadGate);
            return resp;
        }
        catch
        {
            _downloadGate.Release();
            throw;
        }
    }

    /// <summary>
    /// 把分片拼合流包装为带正确 Content-Range/Content-Length 的 HttpResponseMessage；
    /// 释放响应时 StreamContent 会连带释放分片流（及其在途的上游连接）
    /// </summary>
    private static HttpResponseMessage BuildRangeResponse(
        Stream stream,
        long start,
        long length,
        long totalSize,
        bool hasRange
    )
    {
        var resp = new HttpResponseMessage(hasRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };
        resp.Content.Headers.ContentLength = length;
        if (hasRange)
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, totalSize);
        return resp;
    }

    /// <summary>
    /// 创建下载专用 HttpClient：SocketsHttpHandler 绑定共享 CookieContainer 常驻复用连接池，
    /// 消除每请求重建 TCP/TLS 与 TCP 慢启动；流式取消交给调用方 CancellationToken，禁用整体超时避免大文件中断。
    /// </summary>
    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = _cookieContainer,
            MaxConnectionsPerServer = MaxUpstreamConcurrency,
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(PooledConnectionLifetimeMinutes),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(PooledConnectionIdleMinutes),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        };
        if (HostApp.HostEnvironment.IsDevelopment())
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class SemaphoreReleasingHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly SemaphoreSlim _gate;
        private int _released;

        public SemaphoreReleasingHttpContent(HttpContent inner, SemaphoreSlim gate)
        {
            _inner = inner;
            _gate = gate;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _inner.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        ) => _inner.CopyToAsync(stream, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            _inner.ReadAsStreamAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength is not null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _gate.Release();
            }
            base.Dispose(disposing);
        }
    }

    /// <inheritdoc />
    public async Task UpdateCookieAsync(string cookie, CancellationToken ct = default)
    {
        // 浏览器复制的 cookie 可能含换行，统一成 ; 分隔后由 LoadCookieString 切分
        var normalized = cookie.Replace("\r", ";").Replace("\n", ";");

        await _lock.WaitAsync(ct);
        try
        {
            // 整体替换：新建容器后重新加载，并同步落盘与基线，避免旧 Cookie 残留
            _cookieContainer = new CookieContainer();
            LoadCookieString(normalized);
            _lastSavedCookie = GetCurrentCookieString();
            await File.WriteAllTextAsync(_cookieFile, _lastSavedCookie, ct);
            _rootFid = null; // 清除根 fid 缓存，确保后续按新登录态重新解析
            _httpClient = CreateHttpClient(); // 重建下载客户端以绑定新 Cookie 容器（旧 client 交由 GC 回收）
            // 直链缓存（quark:dl:*）不主动清：IRedisCacheService 无批量失效，依赖 TTL 与 OpenDownloadAsync 失败重试自愈
            _logger.LogInformation("夸克 Cookie 已更新，下载客户端已重建");
        }
        finally
        {
            _lock.Release();
        }

        // 立即列根目录验证 cookie 有效性，失败则抛出（cookie 已写入，调用方据此重传）
        await FetchListAsync("0", ct);
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
    /// 调用 POST /file/download 获取单文件下载直链（供 GetOrCreateCacheAsync 的工厂回调使用）
    /// </summary>
    private async Task<string> FetchDownloadUrlAsync(string fid)
    {
        _logger.LogInformation("获取夸克下载直链 fid={Fid}", fid);
        var body = new { fids = new[] { fid } };
        using var httpSession = _httpService.CreateCookieSession(_cookieContainer);
        using var response = await httpSession.RequestAsync(
            BuildRequest("/file/download", HttpMethod.Post, null, body)
        );
        var resp =
            await response.Content.ReadFromJsonAsync<QuarkDownloadResp>()
            ?? throw new InvalidOperationException("夸克接口返回空响应");
        await EnsureSuccessAsync(resp, "获取夸克下载链接失败");
        return resp.Data?.FirstOrDefault()?.DownloadUrl ?? throw new InvalidOperationException("夸克未返回下载链接");
    }

    /// <summary>
    /// 分页拉取目录子项（参考 OpenList GetFiles，GET /file/sort）
    /// </summary>
    private async Task<List<QuarkFile>> FetchListAsync(string parentFid, CancellationToken ct)
    {
        var result = new List<QuarkFile>();
        using var httpSession = _httpService.CreateCookieSession(_cookieContainer);
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
            using var response = await httpSession.RequestAsync(
                BuildRequest("/file/sort", HttpMethod.Get, query, null)
            );
            var resp =
                await response.Content.ReadFromJsonAsync<QuarkSortResp>(ct)
                ?? throw new InvalidOperationException("夸克接口返回空响应");
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
        {
            _logger.LogWarning(
                "夸克接口返回异常 code={Code}, message={Message}, operation={Operation}",
                resp.Code,
                resp.Message,
                errorMessage
            );
            throw new InvalidOperationException($"{errorMessage}：{resp.Message}");
        }
    }

    /// <summary>
    /// 构造夸克 API 请求（统一带 Accept/Referer/UA、pr/fr 查询参数）
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
            model.HttpContentFactory = () => JsonContent.Create(body);
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
