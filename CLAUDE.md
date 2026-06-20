# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

将夸克网盘通过 WebDAV 协议（只读）暴露出来的 ASP.NET Core 服务。客户端经 Basic Auth 认证后，用 `PROPFIND` 列目录、`GET`/`HEAD` 下载文件。夸克接口的调用方式参考 OpenList 的 quark_uc 驱动（`drive.quark.cn/1/clouddrive`）。

## 常用命令

```powershell
# 运行（监听 appsettings.json 的 Urls，默认 http://localhost:8080）
dotnet run --project KuaKeDriveWebDav

# 构建
dotnet build KuaKeDriveWebDav.sln
```

项目无测试工程。目标框架为 `net10.0`，需 .NET 10 SDK。

## 架构

项目单一工程，运行在 `SeventyTwo.InfraKit`（v10.0.0）基础设施之上，依赖它提供的 `HostApp`、`IHttpService`、`ICacheService` 以及 Autofac 自动注册。

### 启动流程（`Program.cs`）

`HostApp.StartWebAppAsync` 接收两个回调：
- **configure 阶段**：`Services.Configure<QuarkOptions>` / `<WebDavOptions>` 绑定 `appsettings.json` 的 `Quark`、`WebDav` 节；注册 `IHttpService` 与 `ICacheService`；`Host.UseAutofac` 配合 `AutoAddDependency(HostApp.AppDomainTypes)` 自动扫描注册带 `[AutofacDependency]` 的类。
- **run 阶段**：读 `WebDavOptions.Prefix`。若前缀为空（根路径），中间件直接接管整个管道（`Map` 不支持 `/` 结尾路径）；否则 `app.Map(prefix, ...)` 挂到分支（默认 `/dav`）。分支内按序挂载 `BasicAuthMiddleware` → `WebDavMiddleware`。

### WebDAV 层（`WebDav/`）

- `BasicAuthMiddleware`：校验 Basic Auth，**放行 OPTIONS**（能力探测阶段客户端常不带认证）。
- `WebDavMiddleware`：**终端中间件**（主构造函数注入的 `RequestDelegate next` 不调用，故带 `#pragma warning disable CS9113`）。仅支持 `OPTIONS / PROPFIND / GET / HEAD`，其余返回 405。`GET` 取下载直链后会重试一次（直链可能过期），并透传 `Range` 支持断点续传。
- `WebDavMultistatus`：生成 RFC 4918 的 207 multistatus XML。`PropWriters` 字典同时承担「支持的属性集合」与「写出器」两个职责——新增 WebDAV 属性只需加一个键值，单点维护。

### 夸克客户端（`Quark/`）

`QuarkClient` 以 **Singleton** 注册，跨请求维持登录态：
- 持有共享 `CookieContainer`（domain `.quark.cn`），构造时优先从 `CookieFilePath`（默认 `quark-cookie.txt`，相对 `AppContext.BaseDirectory`）读取，否则回退到 `QuarkOptions.Cookie`。
- `EnsureSuccessAsync` 在每次接口成功后调用 `PersistCookieIfChangedAsync`：容器内 Cookie 与上次落盘内容比对，不同才加锁回写（`_lock`）。
- `GetByPathAsync` 先 `ResolveRootFidAsync`（把 `QuarkOptions.RootPath` 映射为 fid，默认根目录 `"0"`，首次解析后缓存），再 `WalkAsync` 逐段匹配子节点。
- `ListChildrenAsync` 经 `ICacheService` 缓存（key `quark:children:{fid}`，TTL `ListCacheMinutes`）。底层 `FetchListAsync` 调用 `GET /file/sort` 分页（每页 100），文件名做 `HtmlDecode`。
- `GetDownloadUrlAsync` 调 `POST /file/download`；`OpenDownloadAsync` 用同一 `CookieContainer` 直接请求直链。

## 配置

全部配置集中在 `appsettings.json`：
- `Quark`：`Cookie`（初始登录 Cookie）、`CookieFilePath`（持久化路径）、`RootPath`（映射为 WebDAV 根的夸克路径，默认 `/`）、`ListCacheMinutes`（目录列表缓存分钟数，默认 2）。
- `WebDav`：`Prefix`（默认 `/dav`）、`Username`/`Password`（Basic Auth 凭据）。
- `CacheConfiguration`：`IsUseRedis`（false 时用内存缓存）、`KeyNamespace`（InfraKit 缓存 key 前缀）。

## 代码约定

- XML 文档注释使用中文。
- 终端中间件使用主构造函数注入 + `#pragma warning disable CS9113`（不调用 `next`）。
- DTO/响应模型使用 `record` 与 `[JsonPropertyName]`（见 `QuarkDtos.cs`）。
- 代码格式由 `.csharpierrc` 约定（`printWidth: 120`，4 空格缩进，行尾 `auto`）。
