using KuaKeDriveWebDav.Quark;

namespace KuaKeDriveWebDav.Api;

// CookieUpdateMiddleware 为终端中间件，主构造函数的 RequestDelegate next 不调用
#pragma warning disable CS9113

/// <summary>
/// 夸克 Cookie 更新接口：POST /api/quark/cookie，请求体为纯文本 cookie 字符串
/// </summary>
public class CookieUpdateMiddleware(RequestDelegate next, IQuarkClient quark)
{
    /// <summary>中间件入口：仅接受 POST，读取请求体替换夸克 Cookie 并立即验证有效性</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "POST";
            return;
        }

        string cookie;
        using (var reader = new StreamReader(context.Request.Body))
            cookie = await reader.ReadToEndAsync(context.RequestAborted);

        try
        {
            await quark.UpdateCookieAsync(cookie, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(ex.Message, context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("夸克 Cookie 更新成功", context.RequestAborted);
    }
}
