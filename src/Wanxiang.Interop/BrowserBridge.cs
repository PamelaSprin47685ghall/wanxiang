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

    /// <summary>本地草稿快照（IndexedDB 单条记录，JSON 字符串；无则空串）。W5：只存本机，不上传。</summary>
    [JSImport("draftGet", "wanxiang")]
    public static partial Task<string> DraftGet();

    /// <summary>保存本地草稿快照。</summary>
    [JSImport("draftPut", "wanxiang")]
    public static partial Task DraftPut(string json);

    /// <summary>删除本地草稿快照。</summary>
    [JSImport("draftDelete", "wanxiang")]
    public static partial Task DraftDelete();

    /// <summary>当前页面 URL（用于推导同源 ws(s) 连接地址）。</summary>
    [JSImport("pageUrl", "wanxiang")]
    public static partial string PageUrl();

    /// <summary>CSS 像素视口宽（响应式断点唯一依据；Bounds.Width 在 Browser 高 DPR 下不可靠）。</summary>
    [JSImport("wxViewportWidth", "wanxiang")]
    public static partial double ViewportWidth();

    /// <summary>网络恢复 / 页面回到前台的计数（第 213 节）：数值变化即“现在值得立刻重连”，
    /// 由 F# 侧轮询比较，跳过正在等待的退避延时。</summary>
    [JSImport("wxResumeEpoch", "wanxiang")]
    public static partial int ResumeEpoch();

    /// <summary>第一次完整对话成功结束：PWA 安装引导此后才允许出现（第 210 节）。</summary>
    [JSImport("wxConversationSucceeded", "wanxiang")]
    public static partial void ConversationSucceeded();

    /// <summary>在全局作用域执行一段脚本（highlight.js / KaTeX 的加载）。</summary>
    [JSImport("richLoad", "wanxiang")]
    public static partial void RichLoad(string source);

    /// <summary>highlight.js 着色，返回带 span 的 HTML；失败返回空串。</summary>
    [JSImport("richHighlight", "wanxiang")]
    public static partial string RichHighlight(string code, string language);

    /// <summary>KaTeX 排版，返回 HTML；失败返回空串。</summary>
    [JSImport("richMath", "wanxiang")]
    public static partial string RichMath(string tex, bool displayMode);

    /// <summary>决策 342：PWA 里用浏览器原生解码 + (Offscreen)Canvas 缩小图片，不占 wasm 单线程。
    /// 返回 JSON：{ok:true,b64,mediaType,width,height,originalWidth,originalHeight} 或 {ok:false,error}。</summary>
    [JSImport("imageShrink", "wanxiang")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> ImageShrink(
        [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] bytes, string mediaType, int maxDimension, double maxBytes);
}
