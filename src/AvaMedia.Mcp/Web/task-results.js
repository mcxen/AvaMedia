const labels = { output:"输出", path:"文件", caption:"画面描述", captionModel:"描述模型", captionBackend:"描述后端",
  sampledFrames:"采样帧数", inferredFrames:"推理帧数", backend:"推理后端", fallbackReason:"后端回退原因", sceneError:"场景识别提示",
  captionError:"画面描述提示", durationSeconds:"时长（秒）", isPartial:"部分结果", labels:"内容标签", label:"标签", category:"分类",
  score:"分数", scoreKind:"分数类型", model:"模型", tags:"标签依据", captionObservations:"画面观察", seconds:"时间（秒）",
  files:"文件结果", decisions:"分类结果", error:"错误", pending:"待确认", completed:"完成数量", failed:"失败数量", total:"总数",
  sections:"总结", title:"标题", text:"内容", chapters:"章节", highlights:"要点", keywords:"关键词", limitations:"限制",
  transcript:"转录", cues:"字幕", start:"开始", end:"结束", segments:"片段", people:"人物", plan:"执行计划", applied:"已应用" };
function el(tag, text, className) {
  const item=document.createElement(tag); if(text!==undefined) item.textContent=text; if(className)item.className=className; return item;
}
function action(text, click) { const item=el("button",text);item.type="button";item.onclick=click;return item; }
function text(value) { return value == null ? "—" : typeof value==="boolean" ? value ? "是" : "否" : String(value); }
function fileUrl(task, source, index=0) { return `/web/tasks/${encodeURIComponent(task.id)}/file?source=${source}&index=${index}`; }

export function renderFiles(container, payload, browseFolder) {
  container.replaceChildren(); const task=payload.task;
  const list=el("div",undefined,"task-files"), preview=el("div");
  function add(path,source,index) {
    const row=el("div",undefined,"file-result"), title=el("span",path);row.append(title);
    if(source==="output" && payload.outputIsDirectory) row.append(action("查看目录",()=>browseFolder(path)));
    else {
      const url=fileUrl(task,source,index), link=el("a","下载");link.href=url+"&download=true";link.download="";row.append(link);
      const extension=path.split(".").pop().toLowerCase();
      const kind=/^(mp4|mov|mkv|webm|m4v|avi|ogv)$/.test(extension)?"video":/^(mp3|m4a|wav|flac|ogg|aac|opus)$/.test(extension)?"audio":/^(png|jpg|jpeg|gif|webp|bmp|avif)$/.test(extension)?"img":null;
      if(kind) row.append(action("预览",()=>{
        preview.replaceChildren();const media=el(kind);media.src=url;
        if(kind==="img") media.alt="任务文件预览";else {media.controls=true;media.preload="metadata";}
        media.addEventListener("error",()=>preview.append(el("p","浏览器无法解码此格式，可下载后查看。","error")),{once:true});
        preview.append(media);
      }));
    }
    list.append(row);
  }
  const inputs=el("details");inputs.append(el("summary",`输入文件（${task.inputs.length}）`));
  task.inputs.forEach((path,index)=>add(path,"input",index));inputs.append(list);container.append(inputs);
  if(task.output && ["Completed","Failed","Cancelled"].includes(task.state)) {
    const output=el("div",task.outputIsPartial?"输出（部分结果）":"输出","output-title");container.append(output);
    const previous=list.lastChild;add(task.output,"output",0);if(list.lastChild!==previous)container.append(list.lastChild);
  }
  container.append(preview);
}

export function renderResult(container, payload) {
  container.replaceChildren();const result=payload.result;
  if(result==null) {container.append(el("p",payload.task.error || (payload.task.state==="Completed"?"任务已完成，结果见输出文件。":"结果尚未生成。")));return;}
  if(payload.task.outputIsPartial || result.isPartial) container.append(el("p","当前为部分结果。","partial-note"));
  if(result.nsfw) {
    const nsfw=result.nsfw, section=el("section",undefined,"nsfw-result");section.append(el("h3","NSFW 内容识别"));
    section.append(el("p",nsfw.suspected?"检测到疑似 NSFW 内容":"采样中未达到 NSFW 阈值"));
    const metrics=el("div",undefined,"metrics");
    for(const [name,value] of [["平均分",nsfw.average],["最高分",nsfw.maximum],["判定阈值",nsfw.threshold]])metrics.append(el("div",`${name} ${(value*100).toFixed(1)}%`));
    section.append(metrics,el("small",`${nsfw.model} · ${nsfw.backend} · 分数来自独立二分类模型，与内容标签分数分别展示。`));
    if(nsfw.fallbackReason)section.append(el("small",nsfw.fallbackReason));
    const table=el("table"),header=el("tr");header.append(el("th","采样时间"),el("th","NSFW 分数"));const head=el("thead");head.append(header);table.append(head);
    const body=el("tbody");for(const frame of nsfw.frames || []) {
      const row=el("tr"),score=el("td",`${(frame.score*100).toFixed(1)}%`),meter=el("meter");meter.min=0;meter.max=1;meter.value=frame.score;meter.setAttribute("aria-label",`${frame.seconds.toFixed(1)} 秒 NSFW 分数`);
      score.append(meter);row.append(el("td",`${frame.seconds.toFixed(1)} 秒`),score);body.append(row);
    }table.append(body);section.append(table);container.append(section);
  }
  // A recursive, read-only renderer keeps new and desktop-created task types inspectable.
  let budget=2500;
  function render(value,depth=0) {
    if(--budget<0 || depth>10)return el("small","更多内容见原始数据或下一页。");
    if(value==null || typeof value!=="object")return el("span",text(value));
    if(Array.isArray(value)) {
      if(!value.length)return el("span","—");
      if(value.every(item=>item==null || typeof item!=="object"))return el("p",value.map(text).join(" · "));
      const list=el("div",undefined,"result-items");for(const item of value){if(budget<0)break;const card=el("div",undefined,"result-item");card.append(render(item,depth+1));list.append(card);}return list;
    }
    const list=el("dl");for(const [key,item]of Object.entries(value)){if(item==null || item==="" || Array.isArray(item)&&!item.length)continue;if(budget<0)break;list.append(el("dt",labels[key] || key));const detail=el("dd");detail.append(render(item,depth+1));list.append(detail);}return list;
  }
  const {nsfw,labels:contentLabels,...rest}=typeof result==="object" && !Array.isArray(result)?result:{value:result};
  container.append(render(rest));
  if(Array.isArray(contentLabels) && contentLabels.length) {
    container.append(el("h3",`内容标签（${contentLabels.length}）`));
    const table=el("table"),head=el("thead"),row=el("tr");for(const name of ["标签","分类","标签分数","模型"])row.append(el("th",name));head.append(row);table.append(head);
    const body=el("tbody");for(const item of contentLabels){const row=el("tr");row.append(el("td",item.label),el("td",item.category),el("td",Number(item.score).toFixed(3)),el("td",item.model));body.append(row);}table.append(body);container.append(table);
  } else if(contentLabels!=null) container.append(render({labels:contentLabels}));
}
