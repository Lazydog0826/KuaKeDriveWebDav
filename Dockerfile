# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先单独拷贝工程文件以利用层缓存
COPY KuaKeDriveWebDav/KuaKeDriveWebDav.csproj ./KuaKeDriveWebDav/
RUN dotnet restore -r linux-musl-x64 ./KuaKeDriveWebDav/KuaKeDriveWebDav.csproj

# 拷贝源码并发布（自包含 + alpine/musl 产物，去掉调试符号）
COPY KuaKeDriveWebDav/ ./KuaKeDriveWebDav/
RUN dotnet publish ./KuaKeDriveWebDav/KuaKeDriveWebDav.csproj \
    -c Release -r linux-musl-x64 -o /app/publish \
    --self-contained true \
    /p:PublishReadyToRun=false \
    /p:DebugType=none /p:DebugSymbols=false

# 运行阶段：自包含无需运行时，用仅含 OS 依赖的最小镜像
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine
WORKDIR /app

# 容器内监听所有网卡的 8080（覆盖 appsettings.json 的 localhost:8080，便于端口映射）
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["./KuaKeDriveWebDav"]
