using KuaKeDriveWebDav;
using KuaKeDriveWebDav.Api;
using KuaKeDriveWebDav.WebDav;
using Microsoft.Extensions.Options;
using SeventyTwo.InfraKit.Autofac;
using SeventyTwo.InfraKit.Cache;
using SeventyTwo.InfraKit.Core.App;
using SeventyTwo.InfraKit.Http;

await HostApp.StartWebAppAsync(
    args,
    async builder =>
    {
        // 强类型配置
        builder.Services.Configure<QuarkOptions>(builder.Configuration.GetSection("Quark"));
        builder.Services.Configure<WebDavOptions>(builder.Configuration.GetSection("WebDav"));
        builder.Services.Configure<LocalOptions>(builder.Configuration.GetSection("Local"));

        // Http 客户端（调用夸克接口）+ 内存缓存（缓存目录列表）
        builder.Services.AddHttpService();
        builder.Services.AddCacheService();

        // Autofac 自动扫描注册 QuarkClient（标记了 [AutofacDependency]）
        builder.Host.UseAutofac(containerBuilder =>
        {
            containerBuilder.AutoAddDependency(HostApp.AppDomainTypes);
        });

        await Task.CompletedTask;
    },
    async app =>
    {
        var opt = app.Services.GetRequiredService<IOptions<WebDavOptions>>().Value;

        // Cookie 更新接口：独立分支，复用 WebDAV 的 Basic Auth 认证
        app.Map(
            "/api/quark/cookie",
            api =>
            {
                api.UseMiddleware<BasicAuthMiddleware>();
                api.UseMiddleware<CookieUpdateMiddleware>();
            }
        );

        // 两个 WebDAV 路由各自绑定不同数据源：/dav/kuake（夸克只读）、/dav/local（本地可读写）
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        void ConfigureWebDav(IApplicationBuilder dav)
        {
            dav.UseMiddleware<BasicAuthMiddleware>();
            dav.UseMiddleware<WebDavMiddleware>();
        }

        app.Map(opt.QuarkPrefix.TrimEnd('/'), ConfigureWebDav);
        app.Map(opt.LocalPrefix.TrimEnd('/'), ConfigureWebDav);

        await Task.CompletedTask;
    }
);
