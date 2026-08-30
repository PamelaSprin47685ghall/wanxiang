#!/usr/bin/env python3
"""本地 OpenAI 兼容 mock 端点，仅用于开发期 E2E 与视觉 QA。

不是产品组成部分：让 `万象` 在没有真实 API Key 的环境里也能跑完
「发送 → 流式增量 → 工具调用 → 记账完成」全链路。

用法：
    python3 tools/mock_openai.py [--port 8799]

支持：
  GET  /v1/models                 返回若干 mock 模型
  POST /v1/chat/completions       SSE 流式或一次性返回
        - 用户消息含 "TOOL" → 触发一次 tool_call（echo 工具）
        - 用户消息含 "REASON" → 先流式 reasoning_content
        - 用户消息含 "LONG" → 返回一段含 markdown/代码块的长文
        - 用户消息含 "FAIL" → 返回 500
"""

from __future__ import annotations

import argparse
import json
import re
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODELS = ["mock-gpt", "mock-gpt-mini", "mock-reasoner"]

LONG_REPLY = r"""### 关于「万象」的排版验证

这是一段用于验证 **Markdown 渲染**、`行内代码`、列表与代码块的长文本。

1. 有序列表第一项
2. 有序列表第二项，包含一个[链接](https://example.com)
3. 第三项

- 无序项 A
- 无序项 B

> 引用块：单写者（Commit Coordinator）是 NDJSON 的唯一写入者。

| 组件 | 职责 |
|---|---|
| Server | Agent 执行、NDJSON、投影 |
| Client | 界面与协议 |

```fsharp
/// 斐波那契：验证注释、字符串与类型的着色
let rec fib (n: int) : int =
    match n with
    | 0 | 1 -> n
    | _ -> fib (n - 1) + fib (n - 2)

let msg = sprintf "fib 10 = %d" (fib 10)
type Shape = Circle of float | Square of float
```

行内公式 $E = mc^2$ 与 $\alpha_i^2 + \beta^{n+1}$ 混在正文里，基线应当对齐。

展示式公式：

$$\frac{-b \pm \sqrt{b^2 - 4ac}}{2a}$$

$$\sum_{i=1}^{n} i = \frac{n(n+1)}{2}$$

结束。
"""


def _slice(text: str, size: int = 24) -> list[str]:
    """按固定长度切片流式下发。够小才能验出增量拼接的错。"""
    return [text[i : i + size] for i in range(0, len(text), size)] or [""]


def _sse(payload: dict) -> bytes:
    return b"data: " + json.dumps(payload, ensure_ascii=False).encode() + b"\n\n"


def _sse_named(event: str, payload: dict) -> bytes:
    """Anthropic 的 SSE 带 event: 名字；Gemini 只有 data:。"""
    return (
        b"event: " + event.encode() + b"\n"
        b"data: " + json.dumps(payload, ensure_ascii=False).encode() + b"\n\n"
    )


def _chunk(cid: str, model: str, delta: dict, finish: str | None = None) -> dict:
    return {
        "id": cid,
        "object": "chat.completion.chunk",
        "created": int(time.time()),
        "model": model,
        "choices": [{"index": 0, "delta": delta, "finish_reason": finish}],
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # noqa: A003 - quiet
        pass

    # ---- helpers ----
    def _json(self, code: int, body: dict) -> None:
        raw = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _read_body(self) -> dict:
        n = int(self.headers.get("Content-Length") or 0)
        if n <= 0:
            return {}
        try:
            return json.loads(self.rfile.read(n) or b"{}")
        except json.JSONDecodeError:
            return {}

    # ---- routes ----
    def do_GET(self) -> None:  # noqa: N802
        if self.path.rstrip("/").endswith("/models"):
            self._json(
                200,
                {
                    "object": "list",
                    "data": [
                        {"id": m, "object": "model", "owned_by": "mock"} for m in MODELS
                    ],
                },
            )
            return
        self._json(404, {"error": {"message": "not found"}})

    def _begin_sse(self) -> None:
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "keep-alive")
        self.end_headers()

    @staticmethod
    def _reply_for(last_user: str, has_tool_result: bool) -> str:
        if "LONG" in last_user:
            return LONG_REPLY
        if has_tool_result:
            return "工具已返回结果，下面是总结：调用成功。"
        return f"收到：{last_user.strip() or '（空消息）'}\n\n这是 mock provider 的回复。"

    # ---- Anthropic Messages API ----
    def _anthropic(self, body: dict) -> None:
        model = body.get("model") or "mock-claude"
        msgs = body.get("messages") or []
        last_user = ""
        has_tool_result = False
        for m in msgs:
            blocks = m.get("content")
            if isinstance(blocks, list):
                for b in blocks:
                    if isinstance(b, dict) and b.get("type") == "tool_result":
                        has_tool_result = True
        for m in reversed(msgs):
            if m.get("role") == "user":
                blocks = m.get("content")
                if isinstance(blocks, str):
                    last_user = blocks
                elif isinstance(blocks, list):
                    last_user = " ".join(
                        b.get("text", "") for b in blocks if isinstance(b, dict) and b.get("type") == "text"
                    )
                break

        if "FAIL" in last_user:
            self._json(500, {"type": "error", "error": {"type": "api_error", "message": "mock upstream failure"}})
            return
        if not body.get("max_tokens"):
            # 真实 API 会 400；mock 也照做，否则请求构造漏字段不会被发现
            self._json(400, {"type": "error", "error": {"type": "invalid_request_error", "message": "max_tokens: required"}})
            return

        text = self._reply_for(last_user, has_tool_result)
        want_tool = "TOOL" in last_user and not has_tool_result

        self._begin_sse()

        def push(event: str, payload: dict) -> None:
            self.wfile.write(_sse_named(event, payload))
            self.wfile.flush()

        push("message_start", {
            "type": "message_start",
            "message": {"id": "msg_" + uuid.uuid4().hex[:20], "type": "message", "role": "assistant",
                        "model": model, "content": [], "usage": {"input_tokens": 128, "output_tokens": 0}},
        })
        if "REASON" in last_user:
            # 原生思维链：thinking 块 + thinking_delta，末尾还有一个 signature_delta
            push("content_block_start", {"type": "content_block_start", "index": 0,
                                         "content_block": {"type": "thinking", "thinking": ""}})
            for piece in _slice("先确认问题边界，再给结论。", 8):
                push("content_block_delta", {"type": "content_block_delta", "index": 0,
                                             "delta": {"type": "thinking_delta", "thinking": piece}})
            push("content_block_delta", {"type": "content_block_delta", "index": 0,
                                         "delta": {"type": "signature_delta", "signature": "mock-sig"}})
            push("content_block_stop", {"type": "content_block_stop", "index": 0})

        push("content_block_start", {"type": "content_block_start", "index": 0,
                                     "content_block": {"type": "text", "text": ""}})
        for piece in _slice(text):
            push("content_block_delta", {"type": "content_block_delta", "index": 0,
                                         "delta": {"type": "text_delta", "text": piece}})
            time.sleep(0.01)
        push("content_block_stop", {"type": "content_block_stop", "index": 0})

        if want_tool:
            push("content_block_start", {"type": "content_block_start", "index": 1,
                                         "content_block": {"type": "tool_use",
                                                           "id": "toolu_" + uuid.uuid4().hex[:16],
                                                           "name": "echo", "input": {}}})
            # 入参按片段下发：这是原生协议特有的路径，必须被真实覆盖
            for piece in _slice(json.dumps({"text": last_user[:80]}, ensure_ascii=False), 8):
                push("content_block_delta", {"type": "content_block_delta", "index": 1,
                                             "delta": {"type": "input_json_delta", "partial_json": piece}})
            push("content_block_stop", {"type": "content_block_stop", "index": 1})

        push("message_delta", {"type": "message_delta",
                               "delta": {"stop_reason": "tool_use" if want_tool else "end_turn"},
                               "usage": {"output_tokens": 64}})
        push("message_stop", {"type": "message_stop"})

    # ---- Gemini generateContent ----
    def _gemini(self, body: dict) -> None:
        contents = body.get("contents") or []
        last_user = ""
        has_tool_result = False
        for c in contents:
            for part in c.get("parts") or []:
                if isinstance(part, dict) and "functionResponse" in part:
                    has_tool_result = True
        for c in reversed(contents):
            if c.get("role") == "user":
                last_user = " ".join(
                    p.get("text", "") for p in (c.get("parts") or []) if isinstance(p, dict) and "text" in p
                )
                break

        if "FAIL" in last_user:
            self._json(500, {"error": {"code": 500, "status": "INTERNAL", "message": "mock upstream failure"}})
            return

        text = self._reply_for(last_user, has_tool_result)
        want_tool = "TOOL" in last_user and not has_tool_result

        self._begin_sse()

        def push(payload: dict) -> None:
            self.wfile.write(_sse(payload))
            self.wfile.flush()

        if "REASON" in last_user:
            # 原生思维链：thought=true 的片段与正式回答分开
            for piece in _slice("先确认问题边界，再给结论。", 8):
                push({"candidates": [{"content": {"role": "model",
                                                  "parts": [{"text": piece, "thought": True}]},
                                      "index": 0}]})

        for piece in _slice(text):
            push({"candidates": [{"content": {"role": "model", "parts": [{"text": piece}]}, "index": 0}],
                  "usageMetadata": {"promptTokenCount": 128, "candidatesTokenCount": 8, "totalTokenCount": 136}})
            time.sleep(0.01)

        if want_tool:
            push({"candidates": [{"content": {"role": "model",
                                              "parts": [{"functionCall": {"name": "echo",
                                                                          "args": {"text": last_user[:80]}}}]},
                                  "index": 0}]})

        push({"candidates": [{"content": {"role": "model", "parts": [{"text": ""}]},
                              "finishReason": "STOP", "index": 0}],
              "usageMetadata": {"promptTokenCount": 128, "candidatesTokenCount": 64, "totalTokenCount": 192}})

    def do_POST(self) -> None:  # noqa: N802
        # 原生协议：与 OpenAI 兼容层共用同一批触发词，便于同一套 e2e 覆盖三种传输
        if self.path.rstrip("/").endswith("/v1/messages"):
            self._anthropic(self._read_body())
            return
        if ":streamGenerateContent" in self.path or ":generateContent" in self.path:
            self._gemini(self._read_body())
            return
        if "/chat/completions" not in self.path:
            self._json(404, {"error": {"message": "not found"}})
            return

        body = self._read_body()
        model = body.get("model") or "mock-gpt"
        msgs = body.get("messages") or []
        stream = bool(body.get("stream"))

        last_user = ""
        for m in reversed(msgs):
            if m.get("role") == "user":
                c = m.get("content")
                if isinstance(c, str):
                    last_user = c
                elif isinstance(c, list):
                    last_user = " ".join(
                        p.get("text", "") for p in c if isinstance(p, dict)
                    )
                break

        has_tool_result = any(m.get("role") == "tool" for m in msgs)
        want_tool = "TOOL" in last_user and not has_tool_result
        want_reason = "REASON" in last_user
        want_fail = "FAIL" in last_user
        want_long = "LONG" in last_user

        if want_fail:
            self._json(
                500, {"error": {"message": "mock upstream failure", "type": "mock"}}
            )
            return

        if want_long:
            text = LONG_REPLY
        elif has_tool_result:
            text = "工具已返回结果，下面是总结：调用成功。"
        else:
            text = f"收到：{last_user.strip() or '（空消息）'}\n\n这是 mock provider 的回复。"

        cid = "chatcmpl-" + uuid.uuid4().hex[:20]

        if not stream:
            message: dict = {"role": "assistant", "content": text}
            if want_tool:
                message = {
                    "role": "assistant",
                    "content": None,
                    "tool_calls": [
                        {
                            "id": "call_" + uuid.uuid4().hex[:16],
                            "type": "function",
                            "function": {
                                "name": "echo",
                                "arguments": json.dumps({"text": last_user[:80]}),
                            },
                        }
                    ],
                }
            self._json(
                200,
                {
                    "id": cid,
                    "object": "chat.completion",
                    "created": int(time.time()),
                    "model": model,
                    "choices": [
                        {"index": 0, "message": message, "finish_reason": "stop"}
                    ],
                    "usage": {
                        "prompt_tokens": 128,
                        "completion_tokens": 64,
                        "total_tokens": 192,
                    },
                },
            )
            return

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "keep-alive")
        self.end_headers()

        def push(payload: dict) -> None:
            self.wfile.write(_sse(payload))
            self.wfile.flush()

        push(_chunk(cid, model, {"role": "assistant", "content": ""}))

        if want_reason:
            for piece in ["先分析一下问题…", "需要考虑边界情况。", "得出结论。"]:
                push(_chunk(cid, model, {"reasoning_content": piece}))
                time.sleep(0.12)

        if want_tool:
            push(
                _chunk(
                    cid,
                    model,
                    {
                        "tool_calls": [
                            {
                                "index": 0,
                                "id": "call_" + uuid.uuid4().hex[:16],
                                "type": "function",
                                "function": {
                                    "name": "echo",
                                    "arguments": json.dumps({"text": last_user[:80]}),
                                },
                            }
                        ]
                    },
                )
            )
            push(_chunk(cid, model, {}, finish="tool_calls"))
        else:
            for piece in re.findall(r".{1,18}", text, flags=re.S):
                push(_chunk(cid, model, {"content": piece}))
                time.sleep(0.05)
            push(_chunk(cid, model, {}, finish="stop"))

        push(
            {
                "id": cid,
                "object": "chat.completion.chunk",
                "created": int(time.time()),
                "model": model,
                "choices": [],
                "usage": {
                    "prompt_tokens": 128,
                    "completion_tokens": 64,
                    "total_tokens": 192,
                },
            }
        )
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8799)
    args = ap.parse_args()
    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"mock openai listening on http://127.0.0.1:{args.port}/v1", flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
