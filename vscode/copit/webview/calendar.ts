import { delegate, dur, esc, icon, mount, onState, send, usd } from "./ui";

interface Block { agentId: string; title: string; ticket?: string; project: string; start: number; end: number; aiMs: number; cost: number; ok?: boolean; running: boolean }
interface State { weekStart: number; blocks: Block[]; kw: number }

const root = document.getElementById("app")!;
let state: State | undefined;
const H0 = 6, H1 = 23, PX = 44; // Stunden-Bereich und Pixel pro Stunde

const PALETTE = ["#E8743B", "#7FA6C9", "#7BC47F", "#D9A441", "#D88AB0", "#7FC2B8", "#B59AE0", "#C9A27F"];
function color(p: string) { let h = 0; for (const c of p) h = (h * 31 + c.charCodeAt(0)) | 0; return PALETTE[Math.abs(h) % PALETTE.length]; }

/** Ãœberlappende LÃ¤ufe nebeneinander in Spuren anordnen (mindestens 16px HÃ¶he berÃ¼cksichtigt). */
function lanes(blocks: Block[]): { b: Block; lane: number; of: number }[] {
  const minEnd = (b: Block) => Math.max(b.end, b.start + (16 / PX) * 3600000);
  const sorted = [...blocks].sort((a, b) => a.start - b.start);
  const out: { b: Block; lane: number; of: number }[] = [];
  let cluster: { b: Block; lane: number; of: number }[] = [];
  let laneEnds: number[] = [];
  let clusterEnd = 0;
  const flush = () => { const n = Math.max(1, laneEnds.length); cluster.forEach((c) => (c.of = n)); out.push(...cluster); cluster = []; laneEnds = []; };
  for (const b of sorted) {
    if (cluster.length && b.start >= clusterEnd) flush();
    let lane = laneEnds.findIndex((e) => e <= b.start);
    if (lane < 0) { lane = laneEnds.length; laneEnds.push(0); }
    laneEnds[lane] = minEnd(b);
    clusterEnd = Math.max(clusterEnd, minEnd(b));
    cluster.push({ b, lane, of: 1 });
  }
  flush();
  return out;
}

function render() {
  if (!state) return;
  const days = [...Array(7)].map((_, i) => new Date(state!.weekStart + i * 86400000));
  const today = new Date().toDateString();
  const projects = [...new Set(state.blocks.map((b) => b.project))];
  const hours = [...Array(H1 - H0)].map((_, i) => H0 + i);
  const totals = days.map((d) => {
    const s = d.getTime(), e = s + 86400000;
    const bs = state!.blocks.filter((b) => b.start >= s && b.start < e);
    return { agent: bs.reduce((x, b) => x + (b.end - b.start), 0), ai: bs.reduce((x, b) => x + b.aiMs, 0), cost: bs.reduce((x, b) => x + b.cost, 0), n: bs.length };
  });
  const week = totals.reduce((a, t) => ({ agent: a.agent + t.agent, ai: a.ai + t.ai, cost: a.cost + t.cost }), { agent: 0, ai: 0, cost: 0 });

  mount(root, `
  <div class="hdr">
    <span class="title">Agent-Kalender</span><span class="sub">KW ${state.kw}</span>
    <button class="icon" data-act="nav" data-value="-1">${icon("chevl")}</button>
    <button data-act="nav" data-value="0">Heute</button>
    <button class="icon" data-act="nav" data-value="1">${icon("chevr")}</button>
    <span class="spacer"></span>
    <span class="cap">Agent ${dur(week.agent)}</span><span class="cap">AI ${dur(week.ai)}</span><span class="cap">${usd(week.cost)}</span>
  </div>
  <div class="legend">${projects.map((p) => `<span class="row"><span class="sq" style="background:${color(p)}"></span><span class="cap">${esc(p)}</span></span>`).join("") || `<span class="cap">Keine LÃ¤ufe in dieser Woche</span>`}</div>
  <div class="cal scroll" id="cal" data-scroll>
    <div class="grid">
      <div class="times"><div class="dh"></div>${hours.map((h) => `<div class="hr mono">${String(h).padStart(2, "0")}:00</div>`).join("")}</div>
      ${days.map((d, i) => {
        const s = d.getTime();
        const blocks = state!.blocks.filter((b) => b.start >= s && b.start < s + 86400000);
        return `<div class="day ${d.toDateString() === today ? "today" : ""}">
          <div class="dh"><div class="row"><span class="cap">${d.toLocaleDateString("de-DE", { weekday: "short" })}</span><b>${d.getDate()}.</b></div>
            <div class="mono muted small">${totals[i].n ? `${dur(totals[i].agent)} Â· AI ${dur(totals[i].ai)} Â· ${usd(totals[i].cost)}` : "â€”"}</div></div>
          <div class="slots" style="height:${(H1 - H0) * PX}px">
            ${hours.map(() => `<div class="slot"></div>`).join("")}
            ${lanes(blocks).map(({ b, lane, of }) => {
              const st = new Date(b.start);
              const top = Math.max(0, (st.getHours() + st.getMinutes() / 60 - H0) * PX);
              const h = Math.max(16, ((b.end - b.start) / 3600000) * PX);
              const c = color(b.project);
              return `<div class="blk ${b.running ? "live" : ""}" style="top:${top}px;height:${h}px;left:calc(${(lane / of) * 100}% + 3px);width:calc(${100 / of}% - 6px);border-left-color:${c};background:${c}22" data-act="open" data-arg="${b.agentId}"
                title="${esc(b.title)}\n${st.toLocaleTimeString("de-DE")} â€“ ${new Date(b.end).toLocaleTimeString("de-DE")}\nAgent ${dur(b.end - b.start)} Â· AI ${dur(b.aiMs)} Â· ${usd(b.cost)}">
                <div class="trunc">${b.ticket ? `<span class="key">${esc(b.ticket)}</span> ` : ""}${esc(b.title)}</div>
                ${h > 30 ? `<div class="mono muted small">${dur(b.end - b.start)}${b.ok === false ? " Â· Fehler" : ""}</div>` : ""}
              </div>`;
            }).join("")}
            ${d.toDateString() === today ? (() => { const n = new Date(); const t = (n.getHours() + n.getMinutes() / 60 - H0) * PX; return t > 0 ? `<div class="now" style="top:${t}px"></div>` : ""; })() : ""}
          </div>
        </div>`;
      }).join("")}
    </div>
  </div>`);
}

let first = true;
onState<State>((s) => {
  state = s; render();
  if (first) { first = false; const c = document.getElementById("cal"); if (c) c.scrollTop = Math.max(0, (new Date().getHours() - H0 - 2) * PX); }
});
delegate();
setInterval(() => { if (state?.blocks.some((b) => b.running)) send("refresh"); }, 30000);

const css = document.createElement("style");
css.textContent = `
.legend { display: flex; gap: 16px; padding: 8px 16px; border-bottom: 1px solid var(--line-soft); flex-wrap: wrap; }
.cal { flex: 1; }
.grid { display: grid; grid-template-columns: 56px repeat(7, 1fr); min-width: 900px; }
.times .hr { height: ${PX}px; font-size: 10px; color: var(--muted); padding: 0 8px; transform: translateY(-6px); text-align: right; }
.dh { height: 52px; padding: 8px 10px; border-bottom: 1px solid var(--line-soft); position: sticky; top: 0; background: var(--bg); z-index: 2; }
.small { font-size: 10px; }
.day { border-left: 1px solid var(--line-soft); }
.day.today .dh { box-shadow: inset 0 2px 0 var(--accent); }
.slots { position: relative; }
.slot { height: ${PX}px; border-bottom: 1px solid #1E1916; }
.blk { position: absolute; border-left: 2px solid; padding: 3px 6px; font-size: 11.5px; overflow: hidden; cursor: pointer; }
.blk:hover { filter: brightness(1.25); }
.blk.live { outline: 1px dashed var(--accent); }
.now { position: absolute; left: 0; right: 0; height: 1px; background: var(--accent); }
`;
document.head.appendChild(css);

