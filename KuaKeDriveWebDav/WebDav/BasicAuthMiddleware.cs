using System.Text;
using Microsoft.Extensions.Options;

namespace KuaKeDriveWebDav.WebDav;

/// <summary>
/// HTTP Basic Auth 中间件：校验 WebDAV 客户端凭据，OPTIONS 请求放行
/// </summary>
public class BasicAuthMiddleware(RequestDelegate next, IOptions<WebDavOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // 能力探测阶段部分客户端不带认证，放行 OPTIONS
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var opt = options.Value;
        var header = context.Request.Headers.Authorization.ToString();
        if (!TryValidate(header, opt.Username, opt.Password))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"KuaKeDriveWebDav\"";
            return;
        }

        await next(context);
    }

    private static bool TryValidate(string header, string username, string password)
    {
        if (
            string.IsNullOrEmpty(username)
            || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
        )
            return false;
        try
        {
            var raw = Convert.FromBase64String(header["Basic ".Length..]);
            var text = Encoding.UTF8.GetString(raw);
            var parts = text.Split(':', 2);
            return parts.Length == 2 && parts[0] == username && parts[1] == password;
        }
        catch
        {
            return false;
        }
    }
}
