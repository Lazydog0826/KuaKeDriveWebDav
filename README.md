# KuaKeDriveWebDav

通过 WebDAV 协议同时暴露夸克网盘（只读）与本地文件系统（可读写）的 ASP.NET Core 服务。客户端经共享的 Basic Auth 认证后，即可用任意支持 WebDAV 的客户端（RaiDrive、Cyberduck、Windows 映射网络驱动器等）像操作本地磁盘一样浏览、下载夸克网盘文件，并在本地存储路由下上传、重命名、删除文件。

夸克接口的调用方式参考 [OpenList](https://github.com/OpenListTeam/OpenList) 的 `quark_uc` 驱动（`drive.quark.cn/1/clouddrive`）。

## 特性

- **双数据源**：`/dav/kuake`（夸克网盘，只读）与 `/dav/local`（本地文件系统，可读写）各自独立路由。
- **标准 WebDAV**：夸克路由支持 `OPTIONS / PROPFIND / GET / HEAD`；本地路由额外支持 `PUT / MKCOL / DELETE / MOVE / COPY`。
- **登录态托管**：夸克 `QuarkClient` 以 Singleton 运行，跨请求维持登录态并持久化 Cookie 到磁盘；提供 `POST /api/quark/cookie` 接口热更新 Cookie。
- **缓存优化**：目录列表与下载直链均经缓存（默认下载直链缓存 10 分钟，避免并发 Range 请求逐个回源）。
- **Range 下载**：本地路由与夸克路由均支持 HTTP Range 请求，便于大文件分块下载与播放。
- **容器化部署**：自包含镜像（net10.0 + alpine/musl），无运行时依赖；敏感配置通过 volume 挂载注入，不打入镜像。

## 路由总览

| 路由 | 数据源 | 能力 | 支持的 WebDAV 方法 |
|---|---|---|---|
| `/dav/kuake` | 夸克网盘 | 只读 | `OPTIONS`、`PROPFIND`、`GET`、`HEAD` |
| `/dav/local` | 本地文件系统 | 读写 | 上述全部 + `PUT`、`MKCOL`、`DELETE`、`MOVE`、`COPY` |
| `/api/quark/cookie` | — | 管理 | `POST`（热更新夸克 Cookie） |

三个路由共用同一组 Basic Auth 凭据。`OPTIONS` 探测请求放行免认证。

## 快速部署（Docker）

镜像自包含，无运行时依赖。推荐使用 `docker-compose`，会自动挂载配置、Cookie 与本地存储目录。

1. 在部署目录准备一份 `appsettings.json`（可从仓库根的样例改写，**务必修改 `Username` / `Password` 为强口令**）。
2. 创建挂载所需的目录：`cookie`（夸克登录态持久化）、`local-root`（本地存储根目录）。
3. 启动服务：

```bash
docker compose up -d
```

服务监听容器内 `8080`，compose 默认映射宿主机 `8080:8080`。

挂载关系（见 `docker-compose.yaml`）：

| 宿主机路径 | 容器路径 | 用途 | 权限 |
|---|---|---|---|
| `./appsettings.json` | `/app/appsettings.json` | 服务配置 | 只读 |
| `./cookie` | `/app/cookie` | 夸克 Cookie 持久化 | 读写 |
| `./local-root` | `/app/local-root` | 本地存储根目录 | 读写 |

> 镜像内**不含** `appsettings.json` 与 `cookie`（已被 `.dockerignore` 排除），因此部署前必须在宿主机准备好这两个挂载源，否则服务无法读取配置与登录态。

本地自行构建镜像：

```bash
docker build -t kuake-drive-webdav .
```

## 配置

全部配置集中在 `appsettings.json`：

```json
{
    "Quark": {
        "CookieFilePath": "cookie/quark-cookie.txt",
        "RootPath": "/",
        "ListCacheMinutes": 2,
        "DownloadUrlCacheMinutes": 10
    },
    "WebDav": {
        "QuarkPrefix": "/dav/kuake",
        "LocalPrefix": "/dav/local",
        "Username": "admin",
        "Password": "123456"
    },
    "Local": {
        "RootPath": "local-root"
    }
}
```

| 配置节 | 键 | 说明 | 默认值 |
|---|---|---|---|
| `Quark` | `CookieFilePath` | Cookie 持久化文件路径（相对当前工作目录） | `cookie/quark-cookie.txt` |
| `Quark` | `RootPath` | 映射为 WebDAV 根的夸克路径 | `/` |
| `Quark` | `ListCacheMinutes` | 目录列表缓存分钟数 | `2` |
| `Quark` | `DownloadUrlCacheMinutes` | 下载直链缓存分钟数 | `10` |
| `WebDav` | `QuarkPrefix` | 夸克路由前缀 | `/dav/kuake` |
| `WebDav` | `LocalPrefix` | 本地路由前缀 | `/dav/local` |
| `WebDav` | `Username` / `Password` | Basic Auth 凭据（两组路由共用） | — |
| `Local` | `RootPath` | 本地存储根目录（相对当前工作目录） | `local-root` |

## 更新夸克 Cookie

夸克 Cookie 会过期。服务提供热更新接口，更新后立即校验有效性并持久化到磁盘：

```bash
curl -u admin:123456 \
     -H "Content-Type: text/plain" \
     --data-binary "$(cat quark-cookie.txt)" \
     http://localhost:8080/api/quark/cookie
```

- 请求体为**纯文本**的完整 Cookie 字符串（浏览器抓取自 `pan.quark.cn` 登录态）。
- 成功返回 `200` 与提示文案；Cookie 无效返回 `400` 与错误原因。

## WebDAV 客户端接入

以 RaiDrive / Cyberduck 等客户端为例，按以下参数连接：

- **夸克网盘（只读）**：地址 `http://<host>:8080/dav/kuake`，账号密码为 `WebDav` 配置的凭据。
- **本地存储（读写）**：地址 `http://<host>:8080/dav/local`，账号密码同上。

生产环境建议在服务前置反向代理（如 Caddy / Nginx）终结 TLS，对外仅暴露 HTTPS。

## 本地开发

```powershell
# 运行（监听 appsettings.json 的 Urls，默认 http://localhost:8080）
dotnet run --project KuaKeDriveWebDav

# 构建
dotnet build KuaKeDriveWebDav.sln
```

- 目标框架 `net10.0`，需 .NET 10 SDK。
- 项目运行在 [`SeventyTwo.InfraKit`](https://www.nuget.org/packages/SeventyTwo.InfraKit)（v10.9.0）之上，使用 `WebApplication` 显式创建宿主，并依赖其 `IHttpService` 与 Autofac 自动注册；缓存使用 ASP.NET Core 进程内内存缓存。
- 本地运行前需准备 `cookie/quark-cookie.txt`（夸克登录态）与 `local-root/` 目录。

## 架构

```
客户端 ──Basic Auth──┬── /dav/kuake  ── WebDavMiddleware ── QuarkWebDavStore  ── QuarkClient ── 夸克云端
                     ├── /dav/local  ── WebDavMiddleware ── LocalWebDavStore  ── 本地文件系统
                     └── /api/quark/cookie ── CookieUpdateMiddleware ── QuarkClient
```

- **`IWebDavStore`**（`WebDav/IWebDavStore.cs`）：数据源抽象，`WebDavMiddleware` 不直接依赖夸克。`WebDavNode`（record）统一节点模型，`WebDavCapabilities`（`Read` / `Write`）驱动 OPTIONS 的 `Allow` 头与写方法可用性，`WebDavStoreResolver` 按请求 `PathBase` 解析当前 store。
- **`QuarkClient`**（`Quark/`）：Singleton，持有共享 `CookieContainer`，跨请求维持登录态。目录列表与下载直链经 `IMemoryCache` 缓存；直链失效时自动清缓存重取。
- **`LocalWebDavStore`**（`Local/`）：实现可读写 `IWebDavStore`，带路径越界校验（拒绝 `../`）、Range 解析与弱 ETag。

更详细的设计说明见 [`CLAUDE.md`](./CLAUDE.md)。
