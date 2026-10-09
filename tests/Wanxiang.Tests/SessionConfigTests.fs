module Wanxiang.Tests.SessionConfigTests

open Xunit
open Wanxiang.Core

/// T-006 补漏：§21.1 Unit 清单中的 SessionConfig 纯函数（validate/isValid）。
/// validate 返回全部问题（供 UI 精确指出哪一项不合法），isValid 是其布尔投影。
module SessionConfigValidate =

    [<Fact>]
    let ``empty 配置：provider/model 必填均报错`` () =
        let problems = SessionConfig.validate SessionConfig.empty
        Assert.Contains("provider: 必填", problems)
        Assert.Contains("model: 必填", problems)
        Assert.False(SessionConfig.isValid SessionConfig.empty)

    [<Fact>]
    let ``合法配置：isValid 为 true 且无问题`` () =
        let cfg =
            { SessionConfig.empty with
                provider = "openai"
                model = "gpt-4o" }
        Assert.Empty(SessionConfig.validate cfg)
        Assert.True(SessionConfig.isValid cfg)

    [<Fact>]
    let ``temperature 边界：0 和 2 合法，越界报错`` () =
        let baseCfg =
            { SessionConfig.empty with
                provider = "openai"
                model = "gpt-4o" }
        // 下界与上界本身合法
        Assert.True(SessionConfig.isValid { baseCfg with temperature = Some 0.0 })
        Assert.True(SessionConfig.isValid { baseCfg with temperature = Some 2.0 })
        // 越界报错
        let problems = SessionConfig.validate { baseCfg with temperature = Some 2.1 }
        Assert.Contains("temperature: 需在 0–2 之间", problems)
        let problemsLow = SessionConfig.validate { baseCfg with temperature = Some -0.1 }
        Assert.Contains("temperature: 需在 0–2 之间", problemsLow)

    [<Fact>]
    let ``topP 边界：0 排除、1 合法、越界报错`` () =
        let baseCfg =
            { SessionConfig.empty with
                provider = "openai"
                model = "gpt-4o" }
        Assert.True(SessionConfig.isValid { baseCfg with topP = Some 1.0 })
        let p0 = SessionConfig.validate { baseCfg with topP = Some 0.0 }
        Assert.Contains("topP: 需在 0–1 之间", p0)
        let pBig = SessionConfig.validate { baseCfg with topP = Some 1.1 }
        Assert.Contains("topP: 需在 0–1 之间", pBig)

    [<Fact>]
    let ``maxTokens：正整数合法，0/负数报错`` () =
        let baseCfg =
            { SessionConfig.empty with
                provider = "openai"
                model = "gpt-4o" }
        Assert.True(SessionConfig.isValid { baseCfg with maxTokens = Some 1 })
        let m0 = SessionConfig.validate { baseCfg with maxTokens = Some 0 }
        Assert.Contains("maxTokens: 需为正整数", m0)
        let mNeg = SessionConfig.validate { baseCfg with maxTokens = Some -5 }
        Assert.Contains("maxTokens: 需为正整数", mNeg)

    [<Fact>]
    let ``validate 返回全部问题而非第一个`` () =
        // 同时缺 provider、缺 model、temperature 越界 → 3 条问题
        let cfg =
            { SessionConfig.empty with
                temperature = Some 3.0 }
        let problems = SessionConfig.validate cfg
        Assert.Equal(3, problems.Length)
        Assert.Contains("provider: 必填", problems)
        Assert.Contains("model: 必填", problems)
        Assert.Contains("temperature: 需在 0–2 之间", problems)
