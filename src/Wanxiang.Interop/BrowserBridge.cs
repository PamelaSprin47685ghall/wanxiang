using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace Wanxiang.Interop;

/// <summary>
/// 浏览器 JS 桥（PWA 的 wwwroot/main.js 通过 setModuleImports("wanxiang", …) 注册）。
/// JSImport 的绑定与编组由 C# source generator 生成（F# 不运行 generator，故本桥必须是 C# 项目；
/// 桌面端不调用这些方法，CredentialStore 以 OperatingSystem.IsBrowser() 分流保护）。
/// </summary>
public static partial class BrowserBridge
{
    /// <summary>全部连接记录（JSON 字符串，按 updatedAt 降序；Q191：instanceId 为主键）。</summary>
    [JSImport("credList", "wanxiang")]
    public static partial Task<string> CredList();

    /// <summary>保存/更新一条连接凭据。</summary>
    [JSImport("credPut", "wanxiang")]
    public static partial Task CredPut(string instanceId, string url, string token, string name);

    /// <summary>删除一条连接凭据。</summary>
    [JSImport("credDelete", "wanxiang")]
    public static partial Task CredDelete(string instanceId);

    /// <summary>当前页面 URL（用于推导同源 ws(s) 连接地址）。</summary>
    [JSImport("pageUrl", "wanxiang")]
    public static partial string PageUrl();
}
