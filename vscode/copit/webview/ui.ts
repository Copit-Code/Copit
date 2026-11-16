// Gemeinsame Bausteine für alle Copit-Webviews (state-getrieben, Event-Delegation).

declare function acquireVsCodeApi(): { postMessage(m: unknown): void; getState(): any; setState(s: any): void };
export const vscode = acquireVsCodeApi();

export const esc = (s: unknown) =>
  String(s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

export function send(type: string, payload: Record<string, unknown> = {}) {
  vscode.postMessage({ type, ...payload });
}

// ------------------------------------------------------------ Icons (SVG-Linien, 24er ViewBox)
const P: Record<string, string> = {
  plus: "M12 5V19M5 12H19", x: "M6 6L18 18M18 6L6 18", check: "M5 12.5L10 17.5L19 7",
  play: "M7 4L19 12L7 20Z", stop: "M6 6H18V18H6Z", send: "M12 20V5M5 12L12 5L19 12",
  user: "M4 21C4 16.5 7.5 15 12 15C16.5 15 20 16.5 20 21|c12,8,4", bot: "M5 8H19V19H5ZM12 4V8M9.5 12.5V14.5M14.5 12.5V14.5M2 13H5M19 13H22",
  flow: "M9 6H15M6 9V15M9 18H15|c6,6,3|c18,6,3|c6,18,3", branch: "M6 3V15M18 9C18 14 14 18 9 18|c18,6,3|c6,18,3",
  folder: "M3 5H9L11 7H21V19H3Z", clock: "M12 7V12L15.5 14|c12,12,9", file: "M6 3H14L19 8V21H6ZM14 3V8H19",
  pencil: "M4 20H8L19 9L15 5L4 16ZM13 7L17 11", terminal: "M4 17L10 11L4 5M12 19H20", search: "M16 16L21 21|c11,11,7",
  link: "M10 14L14 10M11 6.5L13 4.5C15 2.5 18.5 2.5 19.8 4.2C21.5 5.5 21.5 9 19.5 11L17.5 13M12.5 17.5L10.5 19.5C8.5 21.5 5 21.5 3.7 19.8C2 18.5 2 15 4 13L6 11",
  list: "M8 6H21M8 12H21M8 18H21M3 6H4.5M3 12H4.5M3 18H4.5", zap: "M13 2L4 14H12L11 22L20 10H12Z",
  "check-circle": "M8 12.2L11 15L16.2 9.4|c12,12,9", "x-circle": "M9 9L15 15M15 9L9 15|c12,12,9", circle: "|c12,12,9",
  loader: "M12 3V6M12 18V21M3 12H6M18 12H21M5.6 5.6L7.8 7.8M16.2 16.2L18.4 18.4M5.6 18.4L7.8 16.2M16.2 7.8L18.4 5.6",
  alert: "M12 3L22 20H2ZM12 9.5V14M12 16.5V17.5", lock: "M5 11H19V21H5ZM8 11V7C8 4.8 9.8 3 12 3C14.2 3 16 4.8 16 7V11",
  ban: "M5.6 5.6L18.4 18.4|c12,12,9", question: "M9.5 9.5C9.5 8 10.6 7 12 7C13.4 7 14.5 8 14.5 9.3C14.5 11 12 11.2 12 13.2M12 16.4V17|c12,12,9",
  chevl: "M15 5L8 12L15 19", chevr: "M9 5L16 12L9 19", chevd: "M5 9L12 16L19 9", chevu: "M5 15L12 8L19 15",
  dots: "|d5,12|d12,12|d19,12", trash: "M4 7H20M10 11V17M14 11V17M6 7L7 20H17L18 7M9 7V4H15V7",
  calendar: "M3 5H21V21H3ZM3 10H21M8 3V7M16 3V7", grid: "M4 4H10V10H4ZM14 4H20V10H14ZM4 14H10V20H4ZM14 14H20V20H14Z",
  ticket: "M3 6H21V10C19.9 10 19 10.9 19 12C19 13.1 19.9 14 21 14V18H3V14C4.1 14 5 13.1 5 12C5 10.9 4.1 10 3 10ZM14 6V18",
  shield: "M12 3L20 6V12C20 16.5 16.5 20 12 21C7.5 20 4 16.5 4 12V6Z", wallet: "M3 6H19V20H3ZM3 6L16 3V6M15 12H21V15H15Z",
  sparkle: "M12 3V7M12 17V21M3 12H7M17 12H21", refresh: "M20 12C20 16.4 16.4 20 12 20C7.6 20 4 16.4 4 12C4 7.6 7.6 4 12 4C14.6 4 16.9 5.2 18.4 7.2M19 3V7.5H14.5",
  diff: "M6 3H14L19 8V21H6ZM14 3V8H19M9.5 12H15.5M12.5 9V15M9.5 18H15.5", cpu: "M7 7H17V17H7ZM10 10H14V14H10ZM10 3V7M14 3V7M10 17V21M14 17V21M3 10H7M3 14H7M17 10H21M17 14H21",
  history: "M4 12C4 16.4 7.6 20 12 20C16.4 20 20 16.4 20 12C20 7.6 16.4 4 12 4C9.3 4 7 5.3 5.5 7.4M4 3V7.5H8.5M12 8V12L15 14",
  drag: "|d9,6|d15,6|d9,12|d15,12|d9,18|d15,18", up: "M12 19V5M6 11L12 5L18 11", down: "M12 5V19M6 13L12 19L18 13",
};

export function icon(name: string, size = 14, cls = "") {
  const def = P[name] ?? P.circle;
  const [path, ...extra] = def.split("|");
  let inner = path ? `<path d="${path}"/>` : "";
  for (const e of extra) {
    const [x, y, r] = e.slice(1).split(",").map(Number);
    inner += e[0] === "c" ? `<circle cx="${x}" cy="${y}" r="${r}"/>` : `<rect x="${x - 1.4}" y="${y - 1.4}" width="2.8" height="2.8" fill="currentColor" stroke="none"/>`;
  }
  return `<svg class="ic ${cls}" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="square" stroke-linejoin="miter">${inner}</svg>`;
}

// ------------------------------------------------------------ Format
export function dur(ms: number): string {
  if (!ms || ms < 1000) return ms ? `${Math.round(ms)}ms` : "0s";
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

export function ago(ts: number): string {
  const d = (Date.now() - ts) / 1000;
  if (d < 90) return "JETZT";
  if (d < 3600) return `${Math.round(d / 60)}M`;
  if (d < 86400) return `${Math.round(d / 3600)}H`;
  return `${Math.round(d / 86400)}D`;
}

export const usd = (n: number) => `$${(n ?? 0).toFixed(2)}`;
export const tokens = (n: number) => n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n ?? 0);

/** Mini-Markdown: Codeblöcke, `code`, **fett**, Überschriften, Listen. */
export function md(text: string): string {
  const parts = (text ?? "").split(/```(\w*)\n?([\s\S]*?)```/g);
  let out = "";
  for (let i = 0; i < parts.length; i++) {
    if (i % 3 === 1) continue;
    if (i % 3 === 2) { out += `<pre class="code">${esc(parts[i])}</pre>`; continue; }
    out += parts[i].split("\n").map((l) => {
      let s = esc(l).replace(/`([^`]+)`/g, "<code>$1</code>").replace(/\*\*(.+?)\*\*/g, "<b>$1</b>");
      if (/^#{1,4} /.test(l)) return `<div class="md-h">${s.replace(/^#+ /, "")}</div>`;
      if (/^\s*[-*] /.test(l)) return `<div class="md-li"><span class="sq"></span>${s.replace(/^\s*[-*] /, "")}</div>`;
      if (/^\s*\d+\. /.test(l)) return `<div class="md-li num">${s}</div>`;
      return s ? `<div>${s}</div>` : `<div class="md-gap"></div>`;
    }).join("");
  }
  return out;
}

// ------------------------------------------------------------ Mount mit Fokus-/Wert-Erhalt
export function mount(root: HTMLElement, html: string) {
  const active = document.activeElement as HTMLInputElement | null;
  const focusId = active?.id;
  const sel = focusId && "selectionStart" in active! ? [active!.selectionStart, active!.selectionEnd] : undefined;
  const values = new Map<string, string>();
  root.querySelectorAll<HTMLInputElement>("[data-keep][id]").forEach((el) => values.set(el.id, el.value));
  const scrolls = new Map<string, number>();
  root.querySelectorAll<HTMLElement>("[data-scroll][id]").forEach((el) => scrolls.set(el.id, el.scrollTop));
  root.innerHTML = html;
  values.forEach((v, id) => { const el = document.getElementById(id) as HTMLInputElement | null; if (el) el.value = v; });
  scrolls.forEach((v, id) => { const el = document.getElementById(id); if (el) el.scrollTop = v; });
  if (focusId) {
    const el = document.getElementById(focusId) as HTMLInputElement | null;
    if (el) { el.focus(); if (sel && el.setSelectionRange) try { el.setSelectionRange(sel[0]!, sel[1]!); } catch { /* */ } }
  }
}

/** Klicks auf [data-act] → Handler (lokal) oder Nachricht an die Extension. */
export function delegate(handlers: Record<string, (el: HTMLElement, ev: Event) => void> = {}) {
  document.addEventListener("click", (ev) => {
    const el = (ev.target as HTMLElement).closest<HTMLElement>("[data-act]");
    if (!el) return;
    const act = el.dataset.act!;
    if (handlers[act]) handlers[act](el, ev);
    else send("act", { act, arg: el.dataset.arg, value: el.dataset.value });
  });
}

export function onState<T>(fn: (s: T) => void) {
  window.addEventListener("message", (e) => {
    if (e.data?.type === "state") fn(e.data.state as T);
    else if (e.data?.type) window.dispatchEvent(new CustomEvent("copit:" + e.data.type, { detail: e.data }));
  });
  send("ready");
}

export const COLORS: Record<string, string> = {
  inbox: "#8B7CF6", in_progress: "#4FB3D9", waiting_subagent: "#D9A441", need_input: "#E8743B", review: "#D96BA0", done: "#7BC47F", failed: "#E0604F",
};

export const STATUS_LABEL: Record<string, string> = {
  inbox: "Inbox", in_progress: "In Progress", waiting_subagent: "Waiting for Subagent", need_input: "Need Input", review: "Review", done: "Done", failed: "Failed",
};

export function seg(options: { value: string; label: string }[], current: string, act: string, arg = "") {
  return `<div class="seg">${options.map((o) =>
    `<button class="${o.value === current ? "on" : ""}" data-act="${act}" data-arg="${esc(arg)}" data-value="${esc(o.value)}">${esc(o.label)}</button>`).join("")}</div>`;
}
