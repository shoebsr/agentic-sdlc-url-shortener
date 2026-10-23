"use strict";

const STORE_KEY = "shortly.links";
const $ = (id) => document.getElementById(id);
const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const state = { selected: null, links: [] };

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

function relative(iso) {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  return `${Math.floor(seconds / 86400)} d ago`;
}

function hostOf(url) {
  try { return new URL(url).hostname.replace(/^www\./, ""); } catch { return url; }
}

function validateUrl() {
  const value = $("url").value.trim();
  let message = "";
  if (value && !/^https?:\/\//i.test(value)) message = "Start with http:// or https://";
  else if (value) { try { new URL(value); } catch { message = "That doesn't look like a valid URL"; } }
  $("urlHint").textContent = message;
  $("url").setAttribute("aria-invalid", message ? "true" : "false");
  return !message;
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
    `<span class="pill">Created ${esc(relative(link.createdAt))}</span>`,
  ];
  if (link.expiresAt) pills.push(`<span class="pill">Expires ${esc(new Date(link.expiresAt).toLocaleDateString())}</span>`);
  return pills.join("");
}

async function create(event) {
  event.preventDefault();
  if (!$("url").value.trim()) {
    $("urlHint").textContent = "Paste a URL to shorten";
    $("url").focus();
    return;
  }

  if (!validateUrl()) return;
  const button = $("createBtn");
  button.disabled = true;
  button.classList.add("loading");
  try {
    const created = await api("/api/v1/links", { method: "POST", body: buildRequest() });
    persist([{ code: created.link.code, token: created.managementToken }, ...saved().filter((e) => e.code !== created.link.code)]);
    $("resultLink").textContent = created.link.shortUrl.replace(/^https?:\/\//, "");
    $("resultLink").href = created.link.shortUrl;
    $("resultTarget").textContent = `→ ${created.link.targetUrl}`;
    $("openBtn").href = created.link.shortUrl;
    $("copyBtn").dataset.url = created.link.shortUrl;
    $("result").classList.remove("hidden");
    $("createForm").reset();
    $("options").open = false;
    toast("Short link created", "ok");
    await loadLinks();
  } catch (error) {
    $("urlHint").textContent = error.message;
    toast(error.message, "bad");
  } finally {
    button.disabled = false;
    button.classList.remove("loading");
  }
}

async function loadLinks() {
  const entries = saved();
  const results = await Promise.all(entries.map(async (entry) => {
    try { return { entry, link: await api(`/api/v1/links/${encodeURIComponent(entry.code)}`, { token: entry.token }) }; }
    catch (error) { return { entry, link: null, gone: error.status === 404 || error.status === 403 }; }
  }));
  const kept = results.filter((r) => !r.gone);
  if (kept.length !== entries.length) persist(kept.map((r) => r.entry));
  state.links = kept;
  renderLinks();
}

function renderLinks() {
  const filter = $("filter").value.trim().toLowerCase();
  const loaded = state.links.filter((r) => r.link);
  $("sumLinks").textContent = loaded.length;
  $("sumClicks").textContent = loaded.reduce((sum, r) => sum + r.link.clickCount, 0);
  $("sumActive").textContent = loaded.filter((r) => r.link.isActive).length;

  const visible = state.links.filter(({ entry, link }) => !filter
    || entry.code.toLowerCase().includes(filter)
    || (link && link.targetUrl.toLowerCase().includes(filter)));

  if (state.links.length === 0) {
    $("links").innerHTML = `<div class="empty"><span class="big" aria-hidden="true">✨</span>No links yet. Paste a URL above to create your first one.</div>`;
    return;
  }

  if (visible.length === 0) {
    $("links").innerHTML = `<div class="empty">No links match “${esc(filter)}”.</div>`;
    return;
  }

  $("links").innerHTML = visible.map(({ entry, link }) => link ? `
    <article class="link-item ${link.isActive ? "" : "inactive"}">
      <div class="avatar" aria-hidden="true">${esc(hostOf(link.targetUrl).charAt(0).toUpperCase())}</div>
      <div class="link-body">
        <a class="short" href="${esc(link.shortUrl)}" target="_blank" rel="noopener">${esc(link.shortUrl.replace(/^https?:\/\//, ""))}</a>
        <div class="target" title="${esc(link.targetUrl)}">${esc(link.targetUrl)}</div>
        <div class="meta">${linkMeta(link)}</div>
      </div>
      <div class="actions">
        <button class="btn" data-analytics="${esc(entry.code)}" type="button">Stats</button>
        <button class="btn" data-copy="${esc(link.shortUrl)}" type="button">Copy</button>
        <button class="btn danger" data-disable="${esc(entry.code)}" type="button" ${link.isActive ? "" : "disabled"}>Disable</button>
      </div>
    </article>` : `
    <article class="link-item inactive"><div class="avatar" aria-hidden="true">?</div>
      <div class="link-body"><span class="short">${esc(entry.code)}</span><div class="target">Temporarily unavailable — retrying</div></div></article>`).join("");
}

async function showAnalytics(code) {
  const entry = saved().find((e) => e.code === code);
  const current = state.links.find((r) => r.entry.code === code);
  if (!entry) return;
  state.selected = code;
  try {
    const a = await api(`/api/v1/links/${encodeURIComponent(code)}/analytics?days=${$("window").value}`, { token: entry.token });
    $("aTitle").textContent = current && current.link ? current.link.shortUrl.replace(/^https?:\/\//, "") : a.code;
    $("aTarget").textContent = current && current.link ? current.link.targetUrl : "";
    $("aTarget").href = current && current.link ? current.link.targetUrl : "#";
    $("stats").innerHTML = [["Total clicks", a.totalClicks], ["In window", a.windowClicks], ["Unique visitors", a.uniqueVisitors], ["Best day", Math.max(0, ...a.daily.map((d) => d.clicks))]]
      .map(([label, value]) => `<div class="stat"><b>${esc(value)}</b><span>${esc(label)}</span></div>`).join("");
    drawChart(a.daily);
    const top = Math.max(1, ...a.topReferrers.map((r) => r.clicks));
    $("referrers").innerHTML = a.topReferrers.length
      ? a.topReferrers.map((r) => `<li><span>${esc(r.referrer)}</span><b>${esc(r.clicks)}</b><span class="track"><i data-w="${Math.round((r.clicks / top) * 100)}"></i></span></li>`).join("")
      : `<li class="muted">No referrers yet — visits so far were direct.</li>`;
    document.querySelectorAll(".track i").forEach((bar) => { bar.style.width = `${bar.dataset.w}%`; });
    if (!$("analytics").open) $("analytics").showModal();
  } catch (error) {
    toast(error.message, "bad");
  }
}

function drawChart(daily) {
  const width = 720, height = 240, left = 34, bottom = 28, top = 14;
  const max = Math.max(1, ...daily.map((d) => d.clicks));
  const step = (width - left - 8) / daily.length;
  const every = Math.ceil(daily.length / 8);
  const grid = [0, 0.5, 1].map((f) => {
    const y = top + (height - top - bottom) * (1 - f);
    return `<line x1="${left}" x2="${width}" y1="${y}" y2="${y}" stroke="currentColor" opacity="0.12"></line><text class="axis" x="${left - 8}" y="${y + 4}" text-anchor="end">${Math.round(max * f)}</text>`;
  }).join("");
  const bars = daily.map((d, i) => {
    const h = ((height - top - bottom) * d.clicks) / max;
    const x = left + i * step + step * 0.18;
    const label = i % every === 0 ? `<text class="axis" x="${x + step * 0.32}" y="${height - 8}" text-anchor="middle">${esc(d.date.slice(5))}</text>` : "";
    return `<rect class="bar-rect" x="${x}" y="${height - bottom - h}" width="${step * 0.64}" height="${d.clicks ? Math.max(h, 3) : 0}" rx="5"><title>${esc(d.date)}: ${d.clicks} click(s)</title></rect>${label}`;
  }).join("");
  const svg = $("chart");
  svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  svg.innerHTML = `<defs><linearGradient id="bars" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#22d3ee"></stop><stop offset="1" stop-color="#7c5cff"></stop></linearGradient></defs>${grid}${bars}`;
}

async function copy(url, button) {
  try {
    await navigator.clipboard.writeText(url);
    if (button) {
      const label = button.textContent;
      button.textContent = "Copied ✓";
      button.classList.add("done");
      setTimeout(() => { button.textContent = label; button.classList.remove("done"); }, 1600);
    } else {
      toast("Copied", "ok");
    }
  } catch {
    toast(url);
  }
}

document.addEventListener("click", async (event) => {
  const target = event.target.closest("[data-analytics],[data-copy],[data-disable]");
  if (!target) return;
  if (target.dataset.analytics) return showAnalytics(target.dataset.analytics);
  if (target.dataset.copy) return copy(target.dataset.copy, target);
  if (target.dataset.disable && confirm("Disable this link? Visitors will see 410 Gone. Its history is kept.")) {
    const entry = saved().find((e) => e.code === target.dataset.disable);
    try {
      await api(`/api/v1/links/${encodeURIComponent(entry.code)}`, { method: "DELETE", token: entry.token });
      toast("Link disabled", "ok");
      await loadLinks();
    } catch (error) {
      toast(error.message, "bad");
    }
  }
});

$("createForm").addEventListener("submit", create);
$("url").addEventListener("input", () => { if ($("urlHint").textContent) validateUrl(); });
$("url").addEventListener("blur", validateUrl);
$("filter").addEventListener("input", renderLinks);
$("copyBtn").addEventListener("click", () => copy($("copyBtn").dataset.url, $("copyBtn")));
$("closeSheet").addEventListener("click", () => $("analytics").close());
$("analytics").addEventListener("click", (event) => { if (event.target === $("analytics")) $("analytics").close(); });
$("analytics").addEventListener("close", () => { state.selected = null; });
$("window").addEventListener("change", () => state.selected && showAnalytics(state.selected));
$("theme").addEventListener("click", () => {
  const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  try { localStorage.setItem("shortly.theme", next); } catch { toast("Theme preference could not be saved"); }
});

(function init() {
  try {
    const theme = localStorage.getItem("shortly.theme");
    if (theme) document.documentElement.dataset.theme = theme;
  } catch {
    document.documentElement.dataset.theme = "dark";
  }

  $("aliasPrefix").textContent = `${location.host}/`;
  loadLinks();
  setInterval(() => { loadLinks(); if (state.selected) showAnalytics(state.selected); }, 10000);
})();
