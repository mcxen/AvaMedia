import { dotnet } from './_framework/dotnet.js';
import { McpClient } from './mcp-client.js';
const client = new McpClient();
const runtime = await dotnet.withDiagnosticTracing(false).create();
runtime.setModuleImports('console', {
  call: async (name, args) => JSON.stringify(await client.call(name, JSON.parse(args))),
  readPending: () => localStorage.getItem('avamedia.pending') || '',
  writePending: value => value ? localStorage.setItem('avamedia.pending', value) : localStorage.removeItem('avamedia.pending'),
  open: url => { const link = document.createElement('a'); link.href = url; link.target = '_blank'; link.rel = 'noopener'; link.click(); }
});
document.getElementById('out').replaceChildren();
try { await runtime.runMain(runtime.getConfig().mainAssemblyName, [location.href]); }
catch (error) { document.getElementById('out').textContent = `启动失败：${error.message}`; throw error; }
