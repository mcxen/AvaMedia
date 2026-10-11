// Same-origin client. The browser never rewrites paths using its own OS rules.
export class McpClient {
  constructor() { this.id = 0; this.protocol = "2026-07-28"; }

  async request(method, params) {
    const id = ++this.id;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 60000);
    try {
      const response = await fetch("/mcp", {
        method: "POST", signal: controller.signal,
        headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream", "MCP-Protocol-Version": this.protocol,
          "Mcp-Method": method, "Mcp-Name": params.name || "" },
        body: JSON.stringify({ jsonrpc: "2.0", id, method, params: { ...params, _meta: {
          "io.modelcontextprotocol/protocolVersion": this.protocol,
          "io.modelcontextprotocol/clientCapabilities": {},
          "io.modelcontextprotocol/clientInfo": { name: "AvaMedia Web", version: "1.0" }
        } } })
      });
      if (!response.ok) throw new Error(`服务器请求失败（${response.status}）`);
      let message;
      if (response.headers.get("Content-Type")?.includes("text/event-stream")) {
        const reader = response.body.getReader(), decoder = new TextDecoder();
        let buffer = "";
        try {
          while (!message) {
            const { value, done } = await reader.read();
            buffer += decoder.decode(value, { stream: !done });
            let boundary;
            while ((boundary = /\r?\n\r?\n/.exec(buffer))) {
              const event = buffer.slice(0, boundary.index);
              buffer = buffer.slice(boundary.index + boundary[0].length);
              const data = event.split(/\r?\n/).filter(line => line.startsWith("data:")).map(line => line.slice(5).trimStart()).join("\n");
              if (data) { const candidate = JSON.parse(data); if (candidate.id === id) message = candidate; }
            }
            if (done) break;
          }
        } finally { await reader.cancel(); }
      } else message = await response.json();
      if (!message || message.id !== id) throw new Error("服务器未返回请求结果，请重试。");
      if (message.error) throw new Error(message.error.message || "服务调用失败");
      return message.result;
    } catch (error) {
      if (error.name === "AbortError") throw new Error("请求超时；已接收的任务会继续执行。可恢复提交以确认任务。");
      throw error;
    } finally { clearTimeout(timeout); }
  }

  async call(name, args = {}) {
    const result = await this.request("tools/call", { name, arguments: args });
    if (result.isError) throw new Error((result.content || []).filter(item => item.type === "text").map(item => item.text).join("\n") || "任务操作失败");
    if (result.structuredContent) return result.structuredContent;
    const text = (result.content || []).find(item => item.type === "text")?.text;
    if (!text) throw new Error("服务器未返回结构化结果。");
    return JSON.parse(text);
  }
}
