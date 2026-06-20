using KuaKeDriveWebDav.Local;
using KuaKeDriveWebDav.Quark;
using Microsoft.Extensions.Options;
using SeventyTwo.InfraKit.Autofac;

namespace KuaKeDriveWebDav.WebDav;

/// <summary>
/// 按请求路由前缀解析对应的 WebDAV 数据源
/// </summary>
public interface IWebDavStoreResolver
{
    /// <summary>根据当前 HttpContext 的 PathBase 返回绑定的 store；无匹配返回 null</summary>
    IWebDavStore? Resolve(HttpContext context);
}

/// <summary>
/// 路由→数据源解析器：按分支前缀（PathBase）匹配 QuarkPrefix / LocalPrefix，返回对应 store
/// </summary>
[AutofacDependency(typeof(IWebDavStoreResolver), ServiceLifetime = ServiceLifetime.Singleton)]
public sealed class WebDavStoreResolver(QuarkWebDavStore quark, LocalWebDavStore local, IOptions<WebDavOptions> options)
    : IWebDavStoreResolver
{
    private readonly string _quarkPrefix = options.Value.QuarkPrefix.TrimEnd('/');
    private readonly string _localPrefix = options.Value.LocalPrefix.TrimEnd('/');

    public IWebDavStore? Resolve(HttpContext context)
    {
        var pathBase = (context.Request.PathBase.Value ?? "").TrimEnd('/');
        if (pathBase == _quarkPrefix)
            return quark;
        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (pathBase == _localPrefix)
            return local;
        return null;
    }
}
