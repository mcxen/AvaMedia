// Explicit, real-media browser acceptance. Requires Playwright + Chrome and a running AvaMedia host.
// node scripts/Verify-WebConsole.cjs --url http://127.0.0.1:18921 --video /absolute/video.mp4 --output /absolute/output --evidence /absolute/evidence
// --existing-nsfw-task UUID reuses a task already submitted through the browser in this acceptance session.
const { chromium, expect } = require('playwright/test');
const fs = require('node:fs/promises'), path = require('node:path'), crypto = require('node:crypto'), assert = require('node:assert/strict');
const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) => index % 2 ? pairs : [...pairs, [value.replace(/^--/, ''), all[index + 1]]], []));
for (const name of ['url', 'video', 'output', 'evidence']) if (!args[name]) throw Error(`Missing --${name}`);
const report = { startedUtc: new Date().toISOString(), url: args.url, checks: [], tasks: [], browserErrors: [] };
const digest = async file => crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex');
async function decode(response) {
  const body = await response.text();
  const rpc = body.startsWith('{') ? JSON.parse(body) : body.split(/\r?\n\r?\n/).flatMap(event => {
    const data = event.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n');
    return data ? [JSON.parse(data)] : [];
  }).find(message => message.result || message.error);
  assert(!rpc.error, JSON.stringify(rpc.error)); assert(!rpc.result.isError, JSON.stringify(rpc.result.content));
  return rpc.result.structuredContent || JSON.parse(rpc.result.content.find(item => item.type === 'text').text);
}
(async () => {
  await fs.mkdir(args.evidence, { recursive: true });
  report.sourceHashBefore = await digest(args.video);
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'chrome', headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
  const page = await context.newPage(); page.on('pageerror', error => report.browserErrors.push(error.message));
  page.setDefaultTimeout(15000);
  const row = id => page.locator(`#tasks tr[data-task-id="${id}"]`);
  async function create() {
    const before = await page.locator('#tasks tr').evaluateAll(rows => rows.map(row => row.dataset.taskId));
    const featureId = await page.locator('#feature').inputValue();
    await page.getByRole('button', { name: '创建任务', exact: true }).click();
    await expect.poll(async () => (await page.locator('#tasks tr').evaluateAll(rows => rows.map(row => row.dataset.taskId))).filter(id => !before.includes(id)).length).toBe(1);
    const id = (await page.locator('#tasks tr').evaluateAll(rows => rows.map(row => row.dataset.taskId))).find(id => !before.includes(id));
    report.tasks.push({ id, featureId }); await expect(page.locator('#pending')).toBeHidden(); return id;
  }
  async function detail(id) {
    await row(id).getByRole('button', { name: '详情', exact: true }).click();
    await expect(page.locator('#detail-results')).not.toHaveText('读取中…');
    return JSON.parse(await page.locator('#detail-content').textContent());
  }
  async function close() { await page.getByRole('button', { name: '关闭', exact: true }).click(); }
  async function selectVideo() {
    await page.locator('#address').fill(path.dirname(args.video));
    await page.getByRole('button', { name: '前往', exact: true }).click();
    await page.getByRole('checkbox', { name: `选择 ${path.basename(args.video)}`, exact: true }).check();
  }
  try {
    await page.goto(args.url); await expect(page.locator('#connection')).toContainText('已连接');
    await expect(page.locator('#locations').getByRole('button', { name: '下载', exact: true })).toBeVisible();
    await selectVideo(); report.checks.push('Browse server Downloads and select the real video');
    await page.locator('#search').fill(path.basename(args.video)); await page.getByRole('button', { name: '筛选', exact: true }).click();
    await expect(page.locator('#files tr')).toHaveCount(1);
    await page.locator('#search').fill(''); await page.getByRole('button', { name: '筛选', exact: true }).click();
    await page.locator('#feature').selectOption('media-ai');
    await expect(page.locator('#recognition')).toBeVisible(); await page.locator('#recognize-nsfw').check();
    await page.locator('#sample-frames').fill('8');
    const nsfwId = args['existing-nsfw-task'] || await create();
    await expect(row(nsfwId)).toContainText('已完成 · 100.0%', { timeout: 300000 });
    const nsfw = await detail(nsfwId);
    assert.equal(nsfw.task.featureId, 'media-ai'); assert.equal(nsfw.result.nsfw.frames.length, 8);
    assert(nsfw.result.nsfw.maximum >= 0 && nsfw.result.nsfw.maximum <= 1);
    await expect(page.getByRole('heading', { name: 'NSFW 内容识别', exact: true })).toBeVisible();
    await expect(page.locator('.nsfw-result')).toContainText(`${(nsfw.result.nsfw.maximum * 100).toFixed(1)}%`);
    await fs.writeFile(path.join(args.evidence, 'nsfw-result.json'), JSON.stringify(nsfw, null, 2));
    await page.screenshot({ path: path.join(args.evidence, 'nsfw-result.png') });
    report.checks.push('NSFW result: real model score, backend, eight timestamps and content labels'); await close();
    await selectVideo(); await page.locator('#feature').selectOption('mp4');
    await page.locator('#output').fill(args.output); await page.getByText('更多参数', { exact: true }).click();
    await page.locator('#options').fill(JSON.stringify({ end: 2, width: 320 }));
    await page.locator('#start').uncheck(); const conversion = await create();
    await expect(row(conversion)).toContainText('等待中');
    await row(conversion).getByRole('button', { name: '暂停', exact: true }).click(); await expect(row(conversion)).toContainText('已暂停');
    await row(conversion).getByRole('button', { name: '恢复', exact: true }).click();
    await expect(row(conversion)).toContainText('已完成 · 100.0%', { timeout: 120000 });
    const converted = await detail(conversion); assert(converted.task.output.startsWith(args.output));
    await page.locator('#detail-files').getByRole('button', { name: '预览', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('#detail-files video')?.readyState >= 1);
    report.convertedMedia = await page.locator('#detail-files video').evaluate(video => ({ duration: video.duration, width: video.videoWidth, height: video.videoHeight }));
    assert(report.convertedMedia.duration > 0 && report.convertedMedia.duration < 4); assert.equal(report.convertedMedia.width, 320);
    const downloadEvent = page.waitForEvent('download'); await page.locator('#detail-files').getByRole('link', { name: '下载', exact: true }).click();
    const downloaded = path.join(args.evidence, 'converted.mp4'); await (await downloadEvent).saveAs(downloaded);
    assert.equal(await digest(downloaded), await digest(converted.task.output));
    report.checks.push('Queued conversion: pause, resume, completion, browser video decode and exact file download'); await close();
    // Lose the response after the actual host accepted a task, then recover the same request through the UI.
    await selectVideo(); let interrupted, accepted;
    await page.route('**/mcp', async route => {
      const request = route.request().postDataJSON();
      if (!interrupted && request?.params?.name === 'avamedia_create_task') {
        interrupted = request.params.arguments.requestId; accepted = await decode(await route.fetch()); await route.abort('failed');
      } else await route.continue();
    });
    await page.getByRole('button', { name: '创建任务', exact: true }).click();
    await expect(page.locator('#pending')).toBeVisible(); await expect(page.locator('#recover')).toBeEnabled();
    await page.unroute('**/mcp'); await page.reload(); await expect(page.locator('#connection')).toContainText('已连接');
    await expect(page.locator('#pending')).toBeVisible();
    assert.equal(accepted.requestId.replaceAll('-', ''), interrupted.replaceAll('-', ''));
    await page.getByRole('button', { name: '恢复提交', exact: true }).click();
    const recoveredId = accepted.tasks[0].id; await expect(page.locator('#pending')).toBeHidden(); await expect(row(recoveredId)).toHaveCount(1);
    await row(recoveredId).getByRole('button', { name: '停止', exact: true }).click(); await expect(row(recoveredId)).toContainText('已停止');
    await row(recoveredId).getByRole('button', { name: '重试', exact: true }).click(); await expect(row(recoveredId)).toContainText('已完成 · 100.0%', { timeout: 120000 });
    report.checks.push('Lost-response recovery survives reload; same task ID; stop and retry');
    // Inspect every actual record visible in this isolated queue, including imported completed desktop tasks.
    report.viewedTasks = [];
    const ids = await page.locator('#tasks tr').evaluateAll(rows => rows.map(row => row.dataset.taskId));
    for (const id of ids) {
      const payload = await detail(id); assert.equal(payload.task.id, id);
      await expect(page.locator('#detail-results')).not.toContainText('读取失败');
      report.viewedTasks.push({ id, featureId: payload.task.featureId, state: payload.task.state, hasResult: payload.result != null });
      await page.getByRole('button', { name: '读取日志', exact: true }).click(); await expect(page.locator('#detail-results pre')).toBeVisible(); await close();
    }
    await page.locator('#task-state').selectOption('Completed'); await expect(page.locator('#tasks tr')).toHaveCount(report.viewedTasks.filter(task=>task.state==='Completed').length);
    await page.locator('#address').fill(path.join(path.dirname(args.video), '__avamedia_missing_directory__'));
    await page.getByRole('button', { name: '前往', exact: true }).click(); await expect(page.locator('#file-status')).not.toHaveText('读取中…');
    await expect(page.locator('#file-status')).toContainText(/不存在|找不到/);
    report.checks.push('All queue records open results and logs; completed filter and missing-directory error');
    report.sourceHashAfter = await digest(args.video); assert.equal(report.sourceHashBefore, report.sourceHashAfter);
    assert.deepEqual(report.browserErrors, []); report.passed = true;
    console.log(JSON.stringify({ passed: true, checks: report.checks, viewedTasks: report.viewedTasks, media: report.convertedMedia }, null, 2));
  } catch (error) {
    report.passed = false; report.error = error.stack; await page.screenshot({ path: path.join(args.evidence, 'failure.png') }); throw error;
  } finally {
    report.finishedUtc = new Date().toISOString(); await fs.writeFile(path.join(args.evidence, 'acceptance.json'), JSON.stringify(report, null, 2)); await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
