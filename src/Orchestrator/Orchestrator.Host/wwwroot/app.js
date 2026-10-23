"use strict";

const state = { scenarios: [], runs: [], approvers: [], runId: null, run: null, source: null, reloadTimer: null, seen: new Set() };
const $ = (id) => document.getElementById(id);
const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const human = (value) => String(value ?? "").replace(/([a-z])([A-Z])/g, "$1 $2");
const stageOrder = ["Requirements", "Clarification", "Design", "Planning", "Implementation", "Verification", "Testing", "Documentation", "ReleaseReadiness", "Release"];

async function api(path, options = {}) {
  const headers = { "Content-Type": "application/json", ...(options.actor ? { "X-Actor": options.actor } : {}) };
  const response = await fetch(path, { method: options.method || "GET", headers, body: options.body ? JSON.stringify(options.body) : undefined });
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) {
    throw new Error((data && (data.title || data.message)) || `${response.status} ${response.statusText}`);
  }
  return data;
}

function toast(message, kind = "") {
  const node = document.createElement("div");
  node.className = `toast ${kind}`;
  node.textContent = message;
  $("toasts").appendChild(node);
  setTimeout(() => node.remove(), 4200);
}

function relative(iso) {
  if (!iso) return "";
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return `${Math.floor(seconds)}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  return `${Math.floor(seconds / 3600)}h ago`;
}

function storage(key, value) {
  try {
    if (value === undefined) return localStorage.getItem(key);
    localStorage.setItem(key, value);
  } catch {
    return null;
  }
  return null;
}

async function loadScenarios() {
  state.scenarios = await api("/api/scenarios");
  $("scenarios").innerHTML = state.scenarios.map((s) => `
    <div class="scenario">
      <div class="row"><span class="kind ${esc(s.kind)}">${esc(s.kind)}</span></div>
      <h3>${esc(s.title)}</h3>
      <p>${esc(s.description)}</p>
      <button class="btn primary" data-launch="${esc(s.id)}" type="button">Launch</button>
    </div>`).join("");
}

async function launch(id) {
  try {
    const run = await api("/api/runs", { method: "POST", body: { scenarioId: id, execution: $("simulate").checked ? "simulated" : null } });
    toast(`Launched ${run.title}`, "ok");
    await loadRuns();
    selectRun(run.id);
  } catch (error) {
    toast(error.message, "bad");
  }
}

async function loadRuns() {
  state.runs = await api("/api/runs");
  $("runCount").textContent = `${state.runs.length} total`;
  $("runs").innerHTML = state.runs.map((r) => `
    <button class="run-item ${r.id === state.runId ? "active" : ""}" data-run="${esc(r.id)}" type="button">
      <span class="t">${esc(r.title)}</span>
      <span class="s"><span class="status-pill ${esc(r.status)}">${esc(human(r.status))}</span>${r.pendingApprovals ? `<span>⚑ ${r.pendingApprovals}</span>` : ""}<span>${relative(r.createdAt)}</span></span>
      <div class="progress"><i data-width="${Math.round((r.nodesSucceeded / Math.max(1, r.nodes)) * 100)}"></i></div>
    </button>`).join("");
  document.querySelectorAll(".progress i").forEach((bar) => { bar.style.width = `${bar.dataset.width}%`; });
}

async function loadMetrics() {
  const m = await api("/api/metrics");
  const items = [
    ["Run success", `${m.runSuccessRate}%`],
    ["Node success", `${m.nodeSuccessRate}%`],
    ["Retries", m.retries],
    ["Rollbacks", m.rollbacks],
    ["MTTR", `${m.meanTimeToRecoverSeconds}s`],
    ["E2E p95", `${m.p95EndToEndSeconds}s`],
  ];
  $("metrics").innerHTML = items.map(([label, value]) => `<div class="metric"><b>${esc(value)}</b><span>${esc(label)}</span></div>`).join("");
  const kill = await api("/api/control/kill-switch");
  $("killSwitch").className = `chip ${kill.engaged ? "danger" : ""}`;
  $("killSwitch").textContent = kill.engaged ? "Kill switch ENGAGED" : "Kill switch";
  $("killSwitch").dataset.engaged = kill.engaged;
}

function selectRun(id) {
  state.runId = id;
  state.seen.clear();
  $("timeline").innerHTML = "";
  document.querySelectorAll(".run-item").forEach((el) => el.classList.toggle("active", el.dataset.run === id));
  if (state.source) state.source.close();
  state.source = new EventSource(`/api/runs/${encodeURIComponent(id)}/events`);
  state.source.onopen = () => $("liveDot").classList.add("on");
  state.source.onerror = () => $("liveDot").classList.remove("on");
  state.source.onmessage = (message) => {
    appendEvent(JSON.parse(message.data));
    scheduleReload();
  };
  api(`/api/runs/${encodeURIComponent(id)}/audit`).then((events) => events.forEach(appendEvent));
  loadRun();
}

function scheduleReload() {
  clearTimeout(state.reloadTimer);
  state.reloadTimer = setTimeout(() => { loadRun(); loadRuns(); loadMetrics(); }, 250);
}

async function loadRun() {
  if (!state.runId) return;
  const view = await api(`/api/runs/${encodeURIComponent(state.runId)}`);
  state.run = view;
  renderRun(view);
}

function renderRun(view) {
  const run = view.run;
  $("empty").classList.add("hidden");
  $("runView").classList.remove("hidden");
  $("runKind").textContent = `${run.kind} · ${run.id}`;
  $("runTitle").textContent = run.title;
  $("runRequirement").textContent = run.requirement;
  $("runStatus").className = `status-pill ${run.status}`;
  $("runStatus").textContent = human(run.status);
  $("runPlan").textContent = `Plan v${run.planVersion} · ${run.decisions.length} decisions`;
  $("haltBtn").disabled = !view.executing;
  $("resumeBtn").disabled = run.status !== "Halted";
  $("amendBtn").disabled = run.status === "Succeeded";
  renderPreview(run);
  renderGraph(view);
  renderActions(run);
  renderArtifacts(view.artifacts);
  renderDecisions(run.decisions);
}

function renderGraph(view) {
  const run = view.run;
  const nodes = Object.fromEntries(run.nodes.map((n) => [n.id, n]));
  const depth = {};
  view.order.forEach((id) => { depth[id] = Math.max(0, ...nodes[id].dependsOn.map((d) => (depth[d] ?? 0) + 1)); });
  const columns = [];
  view.order.forEach((id) => { (columns[depth[id]] ||= []).push(id); });

  const w = 196, h = 54, gx = 22, gy = 30, label = 128, pad = 16;
  const pos = {};
  const widest = Math.max(...columns.map((c) => c.length));
  const lane = widest * w + (widest - 1) * gx;
  columns.forEach((row, ri) => {
    const rowWidth = row.length * w + (row.length - 1) * gx;
    const start = label + pad + (lane - rowWidth) / 2;
    row.forEach((id, ci) => { pos[id] = { x: start + ci * (w + gx), y: pad + ri * (h + gy) }; });
  });
  const width = label + pad * 2 + lane;
  const height = pad * 2 + columns.length * h + (columns.length - 1) * gy;
  const status = (id) => (run.nodeStates[id] || {}).status || "Pending";

  const edges = run.nodes.flatMap((n) => n.dependsOn.map((d) => {
    const a = pos[d], b = pos[n.id];
    const x1 = a.x + w / 2, y1 = a.y + h, x2 = b.x + w / 2, y2 = b.y, my = (y1 + y2) / 2;
    const cls = status(d) === "Succeeded" && status(n.id) === "Running" ? "live" : status(d) === "Succeeded" && status(n.id) !== "Pending" ? "done" : "";
    return `<path class="edge ${cls}" d="M${x1},${y1} C${x1},${my} ${x2},${my} ${x2},${y2}" marker-end="url(#arrow)"></path>`;
  })).join("");

  const labels = columns.map((row, ri) => `<text class="stage-label" x="${pad}" y="${pad + ri * (h + gy) + h / 2 + 4}">${esc(human(nodes[row[0]].stage))}</text>`).join("");

  const boxes = view.order.map((id, i) => {
    const n = nodes[id], s = run.nodeStates[id] || {}, p = pos[id];
    const st = s.status || "Pending";
    const badge = n.requiresApproval ? "⚑" : n.requiresHumanInput ? "✎" : "";
    const attempts = s.attempts > 1 ? ` · ${s.attempts} attempts` : "";
    const by = s.executedBy && s.executedBy !== n.agent ? ` · ${s.executedBy}` : "";
    return `<g class="node ${esc(st)} ${n.id === "clarification" ? "inserted" : ""}" transform="translate(${p.x},${p.y})">
      <rect width="${w}" height="${h}" rx="12"></rect>
      <rect class="bar" width="5" height="${h - 20}" x="8" y="10" rx="2.5"></rect>
      <text x="22" y="24">${esc(n.title)} ${badge}</text>
      <text class="sub" x="22" y="42">${esc(human(st))}${esc(attempts)}${esc(by)}</text>
      <title>${esc(n.agent)}${n.fallbackAgent ? " → fallback " + esc(n.fallbackAgent) : ""}\nentry: ${esc(n.entryGates.join(", ") || "none")}\nexit: ${esc(n.exitGates.join(", ") || "none")}${s.lastError ? "\nerror: " + esc(s.lastError) : ""}</title>
    </g>`;
  }).join("");

  const svg = $("graph");
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  svg.style.maxWidth = `${width}px`;
  svg.removeAttribute("width");
  svg.removeAttribute("height");
  svg.innerHTML = `<defs>
      <linearGradient id="flow" x1="0" x2="1"><stop offset="0" stop-color="#7c5cff"></stop><stop offset="1" stop-color="#22d3ee"></stop></linearGradient>
      <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="#7c86b5"></path></marker>
    </defs>${labels}${edges}${boxes}`;
}

async function renderPreview(run) {
  const banner = $("previewBanner");
  const done = (run.nodeStates.preview || {}).status === "Succeeded";
  if (!done) { banner.classList.add("hidden"); return; }
  try {
    const preview = await api(`/api/runs/${encodeURIComponent(run.id)}/preview`);
    $("previewLink").href = preview.appUrl;
    $("previewLink").textContent = preview.appUrl;
    $("previewOpen").href = preview.appUrl;
    $("previewSwagger").href = preview.swaggerUrl;
    $("previewOpen").classList.remove("hidden");
    $("previewSwagger").classList.remove("hidden");
    $("previewBase").textContent = `The application the agents just delivered · short links served from ${preview.baseUrl} · isolated SQLite database`;
    $("previewState").textContent = "Live";
    $("previewState").className = "chip live-chip";
    $("previewStop").disabled = false;
    $("previewLink").classList.remove("dead");
  } catch {
    $("previewLink").removeAttribute("href");
    $("previewLink").textContent = "Preview stopped";
    $("previewLink").classList.add("dead");
    $("previewBase").textContent = "Re-run the scenario to deploy a new preview.";
    $("previewOpen").classList.add("hidden");
    $("previewSwagger").classList.add("hidden");
    $("previewState").textContent = "Stopped";
    $("previewState").className = "chip ghost";
    $("previewStop").disabled = true;
  }
  banner.classList.remove("hidden");
}

function renderActions(run) {
  const pending = run.approvals.filter((a) => a.status === "Pending");
  const decided = run.approvals.filter((a) => a.status !== "Pending");
  const approverOptions = state.approvers.map((a) => `<option value="${esc(a.name)}">${esc(a.name)} — ${esc(a.role)} (≤ ${esc(a.maxRisk)})</option>`).join("");
  let html = "";

  if (run.status === "AwaitingInput" && run.openQuestions.length) {
    html += `<form class="card" id="clarifyForm"><h4>✎ Clarification needed</h4><p>The planner inserted a human clarification stage because the requirement is ambiguous.</p>
      ${run.openQuestions.map((q) => `<label class="q" for="q-${esc(q.id)}"><b>${esc(q.id)}</b> · ${esc(q.question)}</label>
        <input id="q-${esc(q.id)}" data-question="${esc(q.id)}" placeholder="${esc(q.suggestedDefault || "")}" maxlength="300">`).join("")}
      <div class="row end"><button class="btn primary" type="submit">Submit answers</button></div></form>`;
  }

  html += pending.map((a) => `
    <div class="card" data-approval="${esc(a.id)}">
      <h4>⚑ ${esc(a.id)} · ${esc(nodeTitle(run, a.nodeId))} <span class="risk ${esc(a.risk)}">${esc(a.risk)}</span></h4>
      <p>${esc(a.reason)}</p>
      <div class="evidence">${a.evidence.map((e) => `<span>${esc(e.length > 90 ? e.slice(0, 90) + "…" : e)}</span>`).join("")}</div>
      <div class="row"><select data-approver>${approverOptions}</select></div>
      <input data-comment placeholder="Comment (optional)" maxlength="300">
      <div class="row end"><button class="btn bad" data-decide="reject" type="button">Reject</button><button class="btn ok" data-decide="approve" type="button">Approve</button></div>
    </div>`).join("");

  if (!html) html = `<p class="muted">No human action required right now.</p>`;
  html += decided.map((a) => `<div class="decided">${a.status === "Approved" ? "✅" : "⛔"} ${esc(a.id)} ${esc(nodeTitle(run, a.nodeId))} — ${esc(a.status)} by <b>${esc(a.decidedBy)}</b>${a.comment ? ` · “${esc(a.comment)}”` : ""}</div>`).join("");
  $("actions").innerHTML = html;
}

function nodeTitle(run, id) {
  const node = run.nodes.find((n) => n.id === id);
  return node ? node.title : id;
}

function renderArtifacts(artifacts) {
  $("artifacts").innerHTML = artifacts.map((a) => `
    <button class="artifact" data-artifact="${esc(a.key)}" type="button"><b>${esc(a.key)}</b>
      <span>v${a.version} · ${esc(a.hash)} · ${esc(a.producedBy)}</span></button>`).join("") || `<p class="muted">No artifacts yet.</p>`;
}

function renderDecisions(decisions) {
  $("decisions").innerHTML = decisions.slice().reverse().map((d) => `
    <li><div class="d-head">${esc(d.summary)}</div>
      <div class="d-meta">${esc(d.id)} · ${esc(d.nodeId)} · ${esc(d.actor)} · plan v${d.planVersion}</div>
      ${d.rationale ? `<div class="d-why">${esc(d.rationale)}</div>` : ""}
      ${d.alternativesConsidered && d.alternativesConsidered.length ? `<div class="d-why">Rejected: ${esc(d.alternativesConsidered.join("; "))}</div>` : ""}</li>`).join("");
}

const pipFor = (type) => type.endsWith("succeeded") || type === "approval.granted" || type === "gate.passed" || type === "node.recovered" ? "ok"
  : type.endsWith("failed") || type === "approval.rejected" || type === "gate.failed" || type === "fault.injected" ? "bad"
  : type.includes("rolled") || type === "compensation.failed" ? "rb"
  : type.startsWith("approval") || type.includes("awaiting") || type === "run.paused" || type.includes("retry") || type.includes("fallback") || type === "policy.verdict" ? "warn"
  : type.startsWith("plan") || type.startsWith("decision") ? "plan" : "info";

function appendEvent(e) {
  if (state.seen.has(e.sequence) || e.runId !== state.runId) return;
  state.seen.add(e.sequence);
  const li = document.createElement("li");
  li.innerHTML = `<time>${esc(new Date(e.at).toLocaleTimeString())}</time><span class="pip ${pipFor(e.type)}"></span>
    <div class="msg"><b>${esc(e.type)}${e.nodeId ? " · " + esc(e.nodeId) : ""}</b>${esc(e.message)}</div>`;
  $("timeline").prepend(li);
}

function markdown(source) {
  const lines = esc(String(source).replace(/\r\n?/g, "\n")).split("\n");
  const out = [];
  let i = 0;
  const inline = (text) => text
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
    .replace(/(^|\s)_([^_]+)_(?=\s|$|[.,;])/g, "$1<em>$2</em>");
  while (i < lines.length) {
    const line = lines[i];
    if (line.startsWith("```")) {
      const lang = line.slice(3).trim();
      const body = [];
      i++;
      while (i < lines.length && !lines[i].startsWith("```")) body.push(lines[i++]);
      i++;
      const rendered = lang === "diff"
        ? body.map((l) => l.startsWith("+") ? `<span class="add">${l}</span>` : l.startsWith("-") ? `<span class="del">${l}</span>` : l.startsWith("@@") ? `<span class="hunk">${l}</span>` : l).join("\n")
        : body.join("\n");
      out.push(`<pre data-lang="${lang}"><code>${rendered}</code></pre>`);
      continue;
    }
    if (line.startsWith("|")) {
      const rows = [];
      while (i < lines.length && lines[i].startsWith("|")) rows.push(lines[i++]);
      const cells = (row) => row.replace(/^\||\|$/g, "").split("|").map((c) => inline(c.trim()));
      const body = rows.filter((r, idx) => idx !== 1 || !/^\|[\s\-:|]+\|$/.test(r));
      out.push(`<table><thead><tr>${cells(body[0]).map((c) => `<th>${c}</th>`).join("")}</tr></thead><tbody>${body.slice(1).map((r) => `<tr>${cells(r).map((c) => `<td>${c}</td>`).join("")}</tr>`).join("")}</tbody></table>`);
      continue;
    }
    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    if (heading) { out.push(`<h${heading[1].length}>${inline(heading[2])}</h${heading[1].length}>`); i++; continue; }
    if (/^\s*(-|\d+\.)\s+/.test(line)) {
      const ordered = /^\s*\d+\./.test(line);
      const items = [];
      while (i < lines.length && /^\s*(-|\d+\.)\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*(-|\d+\.)\s+/, ""));
      out.push(`<${ordered ? "ol" : "ul"}>${items.map((it) => `<li>${inline(it)}</li>`).join("")}</${ordered ? "ol" : "ul"}>`);
      continue;
    }
    if (line.startsWith("&gt; ")) { out.push(`<blockquote>${inline(line.slice(5))}</blockquote>`); i++; continue; }
    if (line.trim()) out.push(`<p>${inline(line)}</p>`);
    i++;
  }
  return out.join("\n");
}

async function openArtifact(key) {
  const artifact = await api(`/api/runs/${encodeURIComponent(state.runId)}/artifacts/${encodeURIComponent(key)}`);
  $("viewerTitle").textContent = artifact.key;
  $("viewerMeta").textContent = `${artifact.kind} · v${artifact.version} · ${artifact.hash} · by ${artifact.producedBy}`;
  $("viewerBody").innerHTML = artifact.mediaType === "application/json"
    ? `<pre><code>${esc(JSON.stringify(JSON.parse(artifact.content), null, 2))}</code></pre>`
    : markdown(artifact.content);
  $("viewer").showModal();
}

document.addEventListener("click", async (event) => {
  const target = event.target.closest("[data-launch],[data-run],[data-artifact],[data-decide]");
  if (!target) return;
  if (target.dataset.launch) return launch(target.dataset.launch);
  if (target.dataset.run) return selectRun(target.dataset.run);
  if (target.dataset.artifact) return openArtifact(target.dataset.artifact);
  if (target.dataset.decide) {
    const card = target.closest("[data-approval]");
    const approver = card.querySelector("[data-approver]").value;
    try {
      const result = await api(`/api/runs/${encodeURIComponent(state.runId)}/approvals/${encodeURIComponent(card.dataset.approval)}`, {
        method: "POST", actor: approver, body: { approve: target.dataset.decide === "approve", comment: card.querySelector("[data-comment]").value || null },
      });
      toast(`${card.dataset.approval}: ${result.message}`, "ok");
    } catch (error) {
      toast(error.message, "bad");
    }
    scheduleReload();
  }
});

document.addEventListener("submit", async (event) => {
  if (event.target.id === "clarifyForm") {
    event.preventDefault();
    const answers = {};
    event.target.querySelectorAll("[data-question]").forEach((input) => { if (input.value.trim()) answers[input.dataset.question] = input.value.trim(); });
    try {
      await api(`/api/runs/${encodeURIComponent(state.runId)}/clarifications`, { method: "POST", actor: "requester", body: answers });
      toast("Clarifications recorded — execution resumes", "ok");
    } catch (error) {
      toast(error.message, "bad");
    }
    scheduleReload();
  }
});

$("viewerClose").addEventListener("click", () => $("viewer").close());
$("themeToggle").addEventListener("click", () => {
  const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  storage("theme", next);
});
$("killSwitch").addEventListener("click", async () => {
  const engaged = $("killSwitch").dataset.engaged === "true";
  if (!engaged && !confirm("Engage the global kill switch? All running workflows will safe-stop.")) return;
  await api("/api/control/kill-switch", { method: "POST", actor: "operator", body: { engage: !engaged, reason: "Dashboard operator action" } });
  toast(engaged ? "Kill switch released" : "Kill switch engaged — runs are halting", engaged ? "ok" : "bad");
  loadMetrics();
});
$("haltBtn").addEventListener("click", async () => {
  try { await api(`/api/runs/${encodeURIComponent(state.runId)}/halt`, { method: "POST", actor: "operator" }); toast("Safe-stop requested"); } catch (e) { toast(e.message, "bad"); }
});
$("resumeBtn").addEventListener("click", async () => {
  try { await api(`/api/runs/${encodeURIComponent(state.runId)}/resume`, { method: "POST" }); toast("Run resumed", "ok"); } catch (e) { toast(e.message, "bad"); }
});
$("previewStop").addEventListener("click", async () => {
  try { await api(`/api/runs/${encodeURIComponent(state.runId)}/preview`, { method: "DELETE" }); toast("Preview stopped", "ok"); } catch (e) { toast(e.message, "bad"); }
  loadRun();
});
$("amendBtn").addEventListener("click", () => {
  $("amendText").value = state.run ? state.run.run.requirement : "";
  $("amendDialog").showModal();
});
$("amendCancel").addEventListener("click", () => $("amendDialog").close());
$("amendForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    const result = await api(`/api/runs/${encodeURIComponent(state.runId)}/requirement`, {
      method: "POST", actor: "product-owner", body: { requirement: $("amendText").value, reason: $("amendReason").value || null },
    });
    toast(result.message, "ok");
    $("amendDialog").close();
  } catch (error) {
    toast(error.message, "bad");
  }
});

(async function init() {
  const theme = storage("theme");
  if (theme) document.documentElement.dataset.theme = theme;
  state.approvers = await api("/api/approvers");
  await Promise.all([loadScenarios(), loadRuns(), loadMetrics()]);
  setInterval(() => { loadRuns(); loadMetrics(); }, 5000);
  if (state.runs.length) selectRun(state.runs[0].id);
})();
