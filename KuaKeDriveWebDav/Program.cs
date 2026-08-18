using KuaKeDriveWebDav;
using KuaKeDriveWebDav.Api;
using KuaKeDriveWebDav.WebDav;
using Microsoft.Extensions.Options;
using SeventyTwo.InfraKit.Autofac;
using SeventyTwo.InfraKit.Http;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
});

// 强类型配置
builder.Services.Configure<QuarkOptions>(builder.Configuration.GetSection("Quark"));
builder.Services.Configure<WebDavOptions>(builder.Configuration.GetSection("WebDav"));
builder.Services.Configure<LocalOptions>(builder.Configuration.GetSection("Local"));

// Http 客户端（调用夸克接口）+ 内存缓存（缓存下载直链）
builder.Services.AddHttpService(builder.Environment.IsDevelopment());
builder.Services.AddMemoryCache();

// 保留原 HostApp 提供的基础服务
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();

// Autofac 自动扫描注册标记了 [AutofacDependency] 的应用类型
builder.Host.UseAutofac(containerBuilder =>
{
    containerBuilder.AutoAddDependency([.. typeof(AssemblyMark).Assembly.GetTypes()]);
});

var app = builder.Build();
var opt = app.Services.GetRequiredService<IOptions<WebDavOptions>>().Value;
app.UseRouting();

// Cookie 更新接口：独立分支，复用 WebDAV 的 Basic Auth 认证
app.Map(
    "/api/quark/cookie",
    api =>
    {
        api.UseMiddleware<BasicAuthMiddleware>();
        api.UseMiddleware<CookieUpdateMiddleware>();
    }
);

app.Map(opt.QuarkPrefix.TrimEnd('/'), ConfigureWebDav);
app.Map(opt.LocalPrefix.TrimEnd('/'), ConfigureWebDav);

app.MapHealthChecks("/health");

await app.RunAsync();
return;

// 两个 WebDAV 路由各自绑定不同数据源：/dav/kuake（夸克只读）、/dav/local（本地可读写）
void ConfigureWebDav(IApplicationBuilder dav)
{
    dav.UseMiddleware<BasicAuthMiddleware>();
    dav.UseMiddleware<WebDavMiddleware>();
}
