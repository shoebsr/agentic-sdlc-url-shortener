"use strict";

const STORE_KEY = "shortly.links";
const $ = (id) => document.getElementById(id);
const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const state = { selected: null };

function saved() {
  try { return JSON.parse(localStorage.getItem(STORE_KEY) || "[]"); } catch { return []; }
}

function persist(links) {
  try { localStorage.setItem(STORE_KEY, JSON.stringify(links)); } catch { toast("Browser storage is unavailable; links will not be remembered.", "bad"); }
}

function toast(message, kind = "") {
  const node = document.createElement("div");
  node.className = `toast ${kind}`;
  node.textContent = message;
  $("toasts").appendChild(node);
  setTimeout(() => node.remove(), 4500);
}

async function api(path, { method = "GET", body, token } = {}) {
  const headers = { "Content-Type": "application/json" };
  if (token) headers["X-Management-Token"] = token;
  const response = await fetch(path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  if (response.status === 429) throw new Error(`Too many requests — try again in ${response.headers.get("Retry-After") || "a few"} seconds.`);
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) throw Object.assign(new Error((data && data.title) || `Request failed (${response.status})`), { status: response.status });
  return data;
}

function buildRequest() {
  const expires = $("expires").value;
  return {
    url: $("url").value.trim(),
    customAlias: $("alias").value.trim() || null,
    expiresAt: expires ? new Date(expires).toISOString() : null,
  };
}

function linkMeta(link) {
  const pills = [
    link.isActive ? `<span class="pill on">Active</span>` : `<span class="pill off">Inactive</span>`,
    `<span class="pill">${link.clickCount} click${link.clickCount === 1 ? "" : "s"}</span>`,
  ];
  if (link.expiresAt) pills.push(`<span class="pill">Expires ${esc(new Date(link.expiresAt).toLocaleString())}</span>`);
  return pills.join("");
}

async function create(event) {
  event.preventDefault();
  $("createBtn").disabled = true;
  try {
    const created = await api("/api/v1/links", { method: "POST", body: buildRequest() });
    persist([{ code: created.link.code, token: created.managementToken }, ...saved()]);
    $("resultLink").textContent = created.link.shortUrl;
    $("resultLink").href = created.link.shortUrl;
    $("openBtn").href = created.link.shortUrl;
    $("result").classList.remove("hidden");
    $("createForm").reset();
    toast("Short link created", "ok");
    await renderLinks();
  } catch (error) {
    toast(error.message, "bad");
  } finally {
    $("createBtn").disabled = false;
  }
}

async function renderLinks() {
  const entries = saved();
  const results = await Promise.all(entries.map(async (entry) => {
    try { return { entry, link: await api(`/api/v1/links/${encodeURIComponent(entry.code)}`, { token: entry.token }) }; }
    catch (error) { return { entry, link: null, gone: error.status === 404 || error.status === 403 }; }
  }));
  const live = results.filter((r) => !r.gone);
  if (live.length !== entries.length) persist(live.map((r) => r.entry));

  $("count").textContent = `${live.length} link${live.length === 1 ? "" : "s"}`;
  $("links").innerHTML = live.length === 0
    ? `<div class="empty">No links yet — shorten your first URL above.</div>`
    : live.map(({ entry, link }) => link ? `
      <div class="item">
        <div>
          <a class="code" href="${esc(link.shortUrl)}" target="_blank" rel="noopener">${esc(link.shortUrl)}</a>
          <div class="target">→ ${esc(link.targetUrl)}</div>
          <div class="meta">${linkMeta(link)}</div>
        </div>
        <div class="row">
          <button class="btn small" data-analytics="${esc(entry.code)}" type="button">Analytics</button>
          <button class="btn small" data-copy="${esc(link.shortUrl)}" type="button">Copy</button>
          <button class="btn small danger" data-disable="${esc(entry.code)}" type="button" ${link.isActive ? "" : "disabled"}>Disable</button>
        </div>
      </div>` : `<div class="item"><div class="target">${esc(entry.code)} — temporarily unavailable</div></div>`).join("");
}

async function showAnalytics(code, scroll = false) {
  const entry = saved().find((e) => e.code === code);
  if (!entry) return;
  state.selected = code;
  try {
    const a = await api(`/api/v1/links/${encodeURIComponent(code)}/analytics?days=${$("window").value}`, { token: entry.token });
    $("analytics").classList.remove("hidden");
    $("aTitle").textContent = `Analytics · ${a.code}`;
    $("stats").innerHTML = [["Total clicks", a.totalClicks], ["In window", a.windowClicks], ["Unique visitors", a.uniqueVisitors], ["Days", a.daily.length]]
      .map(([label, value]) => `<div class="stat"><b>${esc(value)}</b><span>${esc(label)}</span></div>`).join("");
    drawChart(a.daily);
    $("referrers").innerHTML = a.topReferrers.length
      ? a.topReferrers.map((r) => `<li><b>${esc(r.referrer)}</b> — ${esc(r.clicks)}</li>`).join("")
      : `<li>No referrers yet (direct visits)</li>`;
    if (scroll) $("analytics").scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (error) {
    toast(error.message, "bad");
  }
}

function drawChart(daily) {
  const svg = $("chart");
  const width = 900, height = 200, pad = 26;
  const max = Math.max(1, ...daily.map((d) => d.clicks));
  const step = (width - pad * 2) / daily.length;
  const bars = daily.map((d, i) => {
    const h = ((height - pad * 2) * d.clicks) / max;
    const x = pad + i * step + step * 0.15;
    const label = daily.length <= 14 || i % Math.ceil(daily.length / 10) === 0
      ? `<text class="axis" x="${x + step * 0.35}" y="${height - 8}" text-anchor="middle">${esc(d.date.slice(5))}</text>` : "";
    return `<rect class="bar-rect" x="${x}" y="${height - pad - h}" width="${step * 0.7}" height="${Math.max(h, d.clicks ? 2 : 0)}" rx="4"><title>${esc(d.date)}: ${d.clicks}</title></rect>${label}`;
  }).join("");
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  svg.innerHTML = `<defs><linearGradient id="bars" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#22d3ee"></stop><stop offset="1" stop-color="#7c5cff"></stop></linearGradient></defs>
    <text class="axis" x="4" y="${pad}">${max}</text><line x1="${pad}" x2="${width - pad}" y1="${height - pad}" y2="${height - pad}" stroke="currentColor" opacity="0.2"></line>${bars}`;
}

document.addEventListener("click", async (event) => {
  const target = event.target.closest("[data-analytics],[data-copy],[data-disable]");
  if (!target) return;
  if (target.dataset.analytics) return showAnalytics(target.dataset.analytics, true);
  if (target.dataset.copy) {
    try { await navigator.clipboard.writeText(target.dataset.copy); toast("Copied", "ok"); } catch { toast(target.dataset.copy); }
    return;
  }
  if (target.dataset.disable && confirm("Disable this link? Visitors will get 410 Gone. History is kept.")) {
    const entry = saved().find((e) => e.code === target.dataset.disable);
    try {
      await api(`/api/v1/links/${encodeURIComponent(entry.code)}`, { method: "DELETE", token: entry.token });
      toast("Link disabled", "ok");
      await renderLinks();
    } catch (error) {
      toast(error.message, "bad");
    }
  }
});

$("createForm").addEventListener("submit", create);
$("copyBtn").addEventListener("click", async () => {
  try { await navigator.clipboard.writeText($("resultLink").textContent); toast("Copied", "ok"); } catch { toast($("resultLink").textContent); }
});
$("window").addEventListener("change", () => state.selected && showAnalytics(state.selected));
$("theme").addEventListener("click", () => {
  const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  try { localStorage.setItem("shortly.theme", next); } catch { return; }
});

(function init() {
  try { const theme = localStorage.getItem("shortly.theme"); if (theme) document.documentElement.dataset.theme = theme; } catch { document.documentElement.dataset.theme = "dark"; }
  renderLinks();
  setInterval(() => { renderLinks(); if (state.selected) showAnalytics(state.selected); }, 10000);
})();
