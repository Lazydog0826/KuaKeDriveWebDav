# Docker 部署指南

镜像已自包含，无需安装 .NET 运行时。在部署目录准备好以下三个文件，即可启动。

## 目录结构

```
部署目录/
├── docker-compose.yaml      # 容器编排
├── appsettings.json         # 服务配置（务必改密码）
├── cookie/                  # 夸克 Cookie 持久化（可读写）
│   └── quark-cookie.txt     #   首次可为空文件
└── local-root/              # 本地存储根目录（可读写）
```

PowerShell 建目录：

```powershell
New-Item -ItemType Directory -Force cookie, local-root | Out-Null
New-Item -ItemType File -Force cookie/quark-cookie.txt | Out-Null
```

Linux：

```bash
mkdir -p cookie local-root && touch cookie/quark-cookie.txt
```

---

## docker-compose.yaml

```yaml
services:
  kuake-drive-webdav:
    image: mc2635/kuake-drive-webdav
    container_name: kuake-drive-webdav
    restart: unless-stopped
    ports:
      - "8080:8080"
    volumes:
      # 配置：只读挂载
      - ./appsettings.json:/app/appsettings.json:ro
      # 夸克 Cookie 持久化（可读写）
      - ./cookie:/app/cookie
      # 本地存储根目录（可读写）
      - ./local-root:/app/local-root
```

### 参数说明

| 参数 | 作用 | 修改建议 |
|---|---|---|
| `image` | 从 Docker Hub 拉取官方镜像 | 不想联网拉取可改为本地构建的镜像名。 |
| `container_name` | 容器名 | 可随意改名。 |
| `restart` | 异常退出自动重启，手动停止不重启 | 保持默认即可。 |
| `ports` `"8080:8080"` | 左边宿主机端口，右边容器内固定 `8080` | 宿主机 8080 被占用时改左边，如 `"9090:8080"`，客户端地址端口同步改。 |
| `volumes` | 三个挂载 | 三项缺一不可。 |

**挂载对照表**（改 `appsettings.json` 路径时同步改这里）

| 宿主机文件 / 目录 | 容器内路径 | 权限 | 用途 |
|---|---|---|---|
| `./appsettings.json` | `/app/appsettings.json` | 只读 | 注入配置 |
| `./cookie` | `/app/cookie` | 读写 | 夸克 Cookie 落盘，重启不丢登录态 |
| `./local-root` | `/app/local-root` | 读写 | `/dav/local` 路由实际读写文件的位置 |

---

## appsettings.json

```json
{
    "Quark": {
        "CookieFilePath": "cookie/quark-cookie.txt",
        "RootPath": "/",
        "DownloadUrlCacheMinutes": 10
    },
    "WebDav": {
        "QuarkPrefix": "/dav/kuake",
        "LocalPrefix": "/dav/local",
        "Username": "admin",
        "Password": "请改成强口令"
    },
    "Local": {
        "RootPath": "local-root"
    },
    "Logging": {
        "LogLevel": {
            "Default": "Information",
            "Microsoft.AspNetCore": "Warning"
        }
    }
}
```

### 参数说明

**`Quark` 节（夸克网盘）**

| 参数 | 含义 | 默认值 | 何时改 |
|---|---|---|---|
| `CookieFilePath` | 夸克 Cookie 持久化文件路径（容器内相对路径） | `cookie/quark-cookie.txt` | 一般不改。改了要同步改 `docker-compose.yaml` 的挂载。 |
| `RootPath` | 暴露给 WebDAV 的夸克起始目录 | `/`（夸克根目录） | 只想把某个子目录当根展示时填，如 `/我的视频`。 |
| `DownloadUrlCacheMinutes` | 下载直链缓存分钟数 | `10` | 夸克直链会过期，太大可能偶尔下载失败（服务会自动重试一次），保持默认即可。 |

**`WebDav` 节（访问入口与认证，最重要）**

| 参数 | 含义 | 默认值 | 说明 |
|---|---|---|---|
| `QuarkPrefix` | 夸克路由前缀 | `/dav/kuake` | 客户端连夸克填 `http://<服务器>:8080/dav/kuake`。 |
| `LocalPrefix` | 本地存储路由前缀 | `/dav/local` | 客户端连本地填 `http://<服务器>:8080/dav/local`。 |
| `Username` | Basic Auth 用户名（两条路由 + 更新接口共用） | `admin` | 建议改成不常见值。 |
| `Password` | Basic Auth 密码 | `123456` | **必须改成强口令**。 |

**`Local` 节（本地存储）**

| 参数 | 含义 | 默认值 | 说明 |
|---|---|---|---|
| `RootPath` | 本地存储根目录（容器内相对路径） | `local-root` | 对应 `docker-compose.yaml` 挂载的 `./local-root`，一般不改。 |

---

## 启动服务

```bash
docker compose up -d
docker compose logs -f
```

看到「Now listening on: http://[::]:8080」即成功。停止：

```bash
docker compose down
```

---

## 更新夸克 Cookie 接口

夸克 Cookie 会过期（几天到一两周）。调用此接口可热更新：立即校验有效性、写入 `cookie/quark-cookie.txt`、重建登录态，**无需重启容器**。

| 项 | 内容 |
|---|---|
| 方法 | 仅接受 `POST`（其它方法返回 `405`） |
| 路径 | `/api/quark/cookie` |
| 认证 | Basic Auth（用 `WebDav` 的 `Username` / `Password`） |
| 请求头 | `Content-Type: text/plain` |
| 请求体 | 浏览器复制的整段 Cookie 字符串（纯文本，含换行无妨） |
| 成功返回 | `200`，响应体：`夸克 Cookie 更新成功` |
| Cookie 无效 | `400`，响应体为错误原因（Cookie 已写入文件，请检查后重传） |
| 认证失败 | `401` |

### 获取夸克 Cookie

1. 浏览器打开 [pan.quark.cn](https://pan.quark.cn) 并登录。
2. `F12` 打开开发者工具 → Network 面板。
3. 刷新页面或点开一个目录，找任意一条请求。
4. 在 Request Headers 里复制 `Cookie:` 这一行**冒号后面的整段内容**。

### 调用示例

**curl**（Cookie 存在 `cookie/quark-cookie.txt` 复用）：

```bash
curl -u admin:你的密码 \
     -H "Content-Type: text/plain" \
     --data-binary "$(cat cookie/quark-cookie.txt)" \
     http://localhost:8080/api/quark/cookie
```

**PowerShell**：

```powershell
$user = "admin"
$pass = "你的密码"
$token = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("$user:$pass"))
$cookie = Get-Content -Raw .\cookie\quark-cookie.txt

Invoke-RestMethod -Uri "http://localhost:8080/api/quark/cookie" `
    -Method Post `
    -Headers @{ Authorization = "Basic $token"; "Content-Type" = "text/plain" } `
    -Body $cookie
```

> `localhost` 与端口 `8080` 是站在服务器本机调用的写法。从其它机器调用时，换成服务器 IP 与 `docker-compose.yaml` 映射的宿主机端口。

---

## 客户端接入

两条路由共用 `WebDav` 的账号密码：

| 数据源 | 连接地址 | 能力 |
|---|---|---|
| 夸克网盘（只读） | `http://<服务器IP>:8080/dav/kuake` | 浏览、下载 |
| 本地存储（读写） | `http://<服务器IP>:8080/dav/local` | 浏览、上传、重命名、删除 |

支持 RaiDrive、Cyberduck、Windows 映射网络驱动器、rclone 等任意标准 WebDAV 客户端。生产环境建议前置 Caddy / Nginx 终结 HTTPS，对外只开 443。

---

## 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| 客户端 `401 Unauthorized` | 用户名 / 密码与 `WebDav` 不一致。 |
| 更新接口返回 `400` | Cookie 已过期或格式不对，重新去浏览器复制一段再提交。 |
| 夸克目录列不出 / 下载失败 | 多半是 Cookie 过期，调用更新接口刷新。 |
| 容器读不到配置 | `appsettings.json` 没挂进去，或挂载路径与 `CookieFilePath` / `Local.RootPath` 不匹配。 |
| 端口冲突启动失败 | 把 `"8080:8080"` 左边改成空闲端口，客户端地址同步更新。 |
| 改配置不生效 | 配置启动时读取，改完执行 `docker compose restart`。 |
