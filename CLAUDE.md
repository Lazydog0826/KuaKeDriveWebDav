# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

通过 WebDAV 协议同时暴露夸克网盘（只读）与本地文件系统（可读写）的 ASP.NET Core 服务。两个路由各自独立：`/dav/kuake`（夸克，只读：`PROPFIND` 列目录、`GET`/`HEAD` 下载）与 `/dav/local`（本地，可读写：额外支持 `PUT`/`MKCOL`/`DELETE`/`MOVE`/`COPY`）。客户端经共享的 Basic Auth 认证后访问。夸克接口的调用方式参考 OpenList 的 quark_uc 驱动（`drive.quark.cn/1/clouddrive`）。

## 常用命令

```powershell
# 运行（监听 appsettings.json 的 Urls，默认 http://localhost:8080）
dotnet run --project KuaKeDriveWebDav

# 构建
dotnet build KuaKeDriveWebDav.sln
```

项目无测试工程。目标框架为 `net10.0`，需 .NET 10 SDK。

## 架构

项目单一工程，运行在 `SeventyTwo.InfraKit`（v10.8.3）基础设施之上，依赖它提供的 `HostApp`、`IHttpService` 以及 Autofac 自动注册；缓存使用 ASP.NET Core `IMemoryCache`。

### 启动流程（`Program.cs`）

`HostApp.StartWebAppAsync` 接收两个回调：
- **configure 阶段**：`Services.Configure<QuarkOptions>` / `<WebDavOptions>` / `<LocalOptions>` 绑定 `appsettings.json` 的 `Quark`、`WebDav`、`Local` 节；注册 `IHttpService` 与 `IMemoryCache`；`Host.UseAutofac` 配合 `AutoAddDependency(HostApp.AppDomainTypes)` 自动扫描注册带 `[AutofacDependency]` 的类。
- **run 阶段**：读 `WebDavOptions` 的 `QuarkPrefix`/`LocalPrefix`，分别 `app.Map` 挂两个固定分支（默认 `/dav/kuake`、`/dav/local`），分支内按序挂载 `BasicAuthMiddleware` → `WebDavMiddleware`。两个 store 注册为各自具体类型（`QuarkWebDavStore` / `LocalWebDavStore`），由 `WebDavStoreResolver` 按请求 `PathBase` 解析出当前 store。

### 数据源抽象（`WebDav/IWebDavStore.cs`）

`WebDavMiddleware` 不直接依赖夸克，而是通过 `IWebDavStore` 抽象访问数据源：
- `WebDavNode`（record）：统一节点模型（`Id`/`Name`/`IsDirectory`/`Size`/`UpdatedAt`/`CreatedAt`，毫秒时间戳）。`Id` 由各 store 自行解释（夸克为 fid，本地为相对根目录路径）。ETag 不进节点模型，由 store 的 `GetEtag` 生成。
- `WebDavContent`：文件读取结果包装（状态码/头/流/附加释放资源 `Owner`），屏蔽夸克 `HttpResponseMessage` 与本地 `FileStream` 差异，实现 `IDisposable` 与 `IAsyncDisposable`。
- `WebDavCapabilities`（`[Flags]`）：`Read`/`Write`，驱动 OPTIONS 的 `Allow` 头与写方法可用性。夸克 store = `Read`，本地 store = `Read | Write`。写方法（`PutAsync`/`MkcolAsync`/`DeleteAsync`/`MoveAsync`/`CopyAsync`）在接口里有抛 `NotSupportedException` 的默认实现，只读 store 无需重写。
- `WebDavStoreResolver`：注入两个具体 store + `WebDavOptions`，按 `PathBase` 匹配前缀返回 store。

### WebDAV 层（`WebDav/`）

- `BasicAuthMiddleware`：校验 Basic Auth（凭据来自 `WebDavOptions`，两组路由共用），**放行 OPTIONS**（能力探测阶段客户端常不带认证）。
- `WebDavMiddleware`：**终端中间件**（主构造函数注入的 `RequestDelegate next` 不调用，故带 `#pragma warning disable CS9113`），注入 `IWebDavStoreResolver`。入口先按 `PathBase` 解析 store（null 返回 404）。读方法（`OPTIONS / PROPFIND / GET / HEAD`）所有 store 支持；写方法（`PUT / MKCOL / DELETE / MOVE / COPY`）先查 `Capabilities.HasFlag(Write)`，不支持直接 405。`Allow` 头按能力运行时拼接。异常映射：`DirectoryNotFoundException`→409、`FileNotFoundException`→404、`InvalidOperationException`→409、其余→502。
- `WebDavMultistatus`：生成 RFC 4918 的 207 multistatus XML。`PropWriters` 字典同时承担「支持的属性集合」与「写出器」两个职责——新增 WebDAV 属性只需加一个键值，单点维护。与数据源解耦，零改动复用。

### 夸克客户端（`Quark/`）

`QuarkClient` 以 **Singleton** 注册，跨请求维持登录态：
- 持有共享 `CookieContainer`（domain `.quark.cn`），构造时从 `CookieFilePath`（默认 `cookie/quark-cookie.txt`，相对当前工作目录）读取。
- `EnsureSuccessAsync` 在每次接口成功后调用 `PersistCookieIfChangedAsync`：容器内 Cookie 与上次落盘内容比对，不同才加锁回写（`_lock`）。
- `GetByPathAsync` 先 `ResolveRootFidAsync`（把 `QuarkOptions.RootPath` 映射为 fid，默认根目录 `"0"`，首次解析后缓存），再 `WalkAsync` 逐段匹配子节点。
- `ListChildrenAsync` 经 `IMemoryCache` 缓存（key `quark:children:{fid}`，TTL `ListCacheMinutes`）。底层 `FetchListAsync` 调用 `GET /file/sort` 分页（每页 100），文件名做 `HtmlDecode`。
- `OpenDownloadAsync(fid, totalSize, rangeHeader)` 是对外下载原语：解析 Range 后，范围大于单片（常量 10MB）且并发开启（常量 3 > 1）时构造 `QuarkParallelDownloadStream`（按 10MB 切片、3 路并发向夸克直链发 Range、按序拼合成单流，滑动窗口把内存压在约 30MB），用多条上游连接绕开夸克对单连接大文件的限速（参考 OpenList 夸克驱动，对应 AList issue #4175 的 aria2 切片验证）；否则回退单流。并发数与切片大小为 `QuarkClient` 常量（3 / 10MB），不读配置。内部 `GetDownloadUrlAsync`（按 fid 经 `IMemoryCache` 缓存，key `quark:dl:{fid}`，TTL `DownloadUrlCacheMinutes` 默认 10 分钟）取直链；直链失效时自动清缓存重取一次（`HttpRequestException` 触发，单流与每片各自重试）。`QuarkParallelDownloadStream`、`GetDownloadUrlAsync`/`FetchDownloadUrlAsync`/`OpenDownloadByUrlAsync` 均为内部细节，不暴露给调用方。
- `QuarkWebDavStore` 包装 `IQuarkClient` 实现 `IWebDavStore`（只读），`OpenReadAsync` 直接调 `IQuarkClient.OpenDownloadAsync(fid, node.Size, rangeHeader)` 拿上游响应，不再感知直链/缓存/重试/分片，上游 `HttpResponseMessage`（分片模式下是包裹 `QuarkParallelDownloadStream` 的 `StreamContent`）作为 `WebDavContent.Owner` 一并释放。`CookieUpdateMiddleware` 仍直接依赖 `IQuarkClient`，二者互不影响。

### 本地存储（`Local/`）

`LocalWebDavStore` 以 **Singleton** 注册，实现 `IWebDavStore`（可读写）：
- 根目录由 `LocalOptions.RootPath` 决定（默认 `local-root`，相对当前工作目录）。`Id` 填相对根目录的标准化路径，`GetByPathAsync` 用 `Path.Combine(root, ...)` 反查物理路径。
- **路径安全**：`Path.GetFullPath` 后校验结果在 `root` 之下，拒绝 `../` 越界（抛 `UnauthorizedAccessException`→502）。
- `OpenReadAsync`：`FileStream`（异步）+ 自行解析 `Range`（`bytes=start-end` / 后缀范围），有 range 返回 206 + `ContentRange`，无 range 返回 200。range 读取用内部 `RangeStream` 限定长度。
- 写方法：`PutAsync`（`FileMode.Create`，允许覆盖，父目录不存在→409）、`MkcolAsync`（父不存在→409，已存在→409）、`DeleteAsync`（文件 `File.Delete`，目录 `Directory.Delete(recursive: true)` 递归删除，不存在返回 false→404）、`MoveAsync`/`CopyAsync`（同 store 内，目标已存在且 `overwrite=false`→409）。
- ETag 为弱 ETag `W/"{UpdatedAt}-{Size}"`。

## 配置

全部配置集中在 `appsettings.json`：
- `Quark`：`CookieFilePath`（Cookie 持久化文件路径，默认 `cookie/quark-cookie.txt`，相对当前工作目录）、`RootPath`（映射为 WebDAV 根的夸克路径，默认 `/`）、`ListCacheMinutes`（目录列表缓存分钟数，默认 2）、`DownloadUrlCacheMinutes`（下载直链缓存分钟数，默认 10）。（分片并发的连接数 3 与单片大小 10MB 是 `QuarkClient` 内的常量，不读配置。）
- `WebDav`：`QuarkPrefix`（默认 `/dav/kuake`）、`LocalPrefix`（默认 `/dav/local`）、`Username`/`Password`（Basic Auth 凭据，两组路由共用）。
- `Local`：`RootPath`（本地存储根目录，默认 `local-root`，相对当前工作目录）。

## 代码约定

- XML 文档注释使用中文。
- 终端中间件使用主构造函数注入 + `#pragma warning disable CS9113`（不调用 `next`）。
- DTO/响应模型使用 `record` 与 `[JsonPropertyName]`（见 `QuarkDtos.cs`）。
- 代码格式由 `.csharpierrc` 约定（`printWidth: 120`，4 空格缩进，行尾 `auto`）。
