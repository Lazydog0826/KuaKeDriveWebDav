using KuaKeDriveWebDav;
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
        var prefix = app.Services.GetRequiredService<IOptions<WebDavOptions>>().Value.Prefix.TrimEnd('/');

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        void ConfigureWebDav(IApplicationBuilder dav)
        {
            dav.UseMiddleware<BasicAuthMiddleware>();
            dav.UseMiddleware<WebDavMiddleware>();
        }

        // 根路径（/ 或空）下 app.Map 不支持以 / 结尾的路径，改为终端中间件接管全部请求；
        // 否则挂在 Prefix 分支
        if (prefix.Length == 0)
            ConfigureWebDav(app);
        else
            app.Map(prefix, ConfigureWebDav);

        await Task.CompletedTask;
    }
);
