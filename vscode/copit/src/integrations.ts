import * as https from "node:https";
import * as http from "node:http";

export interface HttpOptions { baseUrl: string; headers: Record<string, string>; ignoreCerts: boolean }

/** Kleiner JSON-HTTP-Client (unterstützt interne Zertifikate). */
export function request<T = any>(o: HttpOptions, method: string, path: string, body?: unknown): Promise<T> {
  const url = new URL(path.replace(/^\//, ""), o.baseUrl.replace(/\/?$/, "/"));
  const lib = url.protocol === "http:" ? http : https;
  const payload = body === undefined ? undefined : Buffer.from(JSON.stringify(body));
  return new Promise((resolve, reject) => {
    const req = lib.request(url, {
      method,
      headers: { Accept: "application/json", ...(payload ? { "Content-Type": "application/json", "Content-Length": payload.length } : {}), ...o.headers },
      rejectUnauthorized: !o.ignoreCerts,
      timeout: 30000,
    } as https.RequestOptions, (res) => {
      const chunks: Buffer[] = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => {
        const text = Buffer.concat(chunks).toString("utf8");
        if ((res.statusCode ?? 500) >= 400) return reject(new Error(`HTTP ${res.statusCode}: ${text.slice(0, 400)}`));
        if (!text) return resolve(undefined as T);
        try { resolve(JSON.parse(text)); } catch { resolve(text as unknown as T); }
      });
    });
    req.on("timeout", () => req.destroy(new Error("Zeitüberschreitung")));
    req.on("error", reject);
    if (payload) req.write(payload);
    req.end();
  });
}

// ------------------------------------------------------------------ Jira

export interface JiraIssueLite { key: string; summary: string; status: string; assignee: string; updated: string }

export class Jira {
  private o: HttpOptions;
  constructor(baseUrl: string, token: string, opts: { basicUser?: string; ignoreCerts: boolean }) {
    const auth = opts.basicUser ? "Basic " + Buffer.from(`${opts.basicUser}:${token}`).toString("base64") : "Bearer " + token;
    this.o = { baseUrl: baseUrl.replace(/\/$/, ""), headers: { Authorization: auth }, ignoreCerts: opts.ignoreCerts };
  }

  issueUrl(key: string) { return `${this.o.baseUrl}/browse/${key}`; }

  myself() { return request<any>(this.o, "GET", "rest/api/2/myself"); }

  async search(jql: string, max = 50): Promise<JiraIssueLite[]> {
    const r = await request<any>(this.o, "POST", "rest/api/2/search", { jql, maxResults: max, fields: ["summary", "status", "assignee", "updated"] });
    return (r?.issues ?? []).map((i: any) => ({
      key: i.key, summary: i.fields?.summary ?? "", status: i.fields?.status?.name ?? "",
      assignee: i.fields?.assignee?.displayName ?? "", updated: i.fields?.updated ?? "",
    }));
  }

  async issue(key: string): Promise<{ key: string; summary: string; description: string; status: string } | undefined> {
    try {
      const i = await request<any>(this.o, "GET", `rest/api/2/issue/${encodeURIComponent(key)}?fields=summary,description,status`);
      return { key: i.key, summary: i.fields?.summary ?? "", description: i.fields?.description ?? "", status: i.fields?.status?.name ?? "" };
    } catch { return undefined; }
  }

  comment(key: string, body: string) { return request(this.o, "POST", `rest/api/2/issue/${encodeURIComponent(key)}/comment`, { body }); }

  /** Übergang per Zielstatus- oder Transition-Name. */
  async transition(key: string, target: string) {
    const r = await request<any>(this.o, "GET", `rest/api/2/issue/${encodeURIComponent(key)}/transitions`);
    const t = (r?.transitions ?? []).find((x: any) =>
      x.name?.toLowerCase() === target.toLowerCase() || x.to?.name?.toLowerCase() === target.toLowerCase());
    if (!t) throw new Error(`Übergang "${target}" für ${key} nicht verfügbar (${(r?.transitions ?? []).map((x: any) => x.name).join(", ")})`);
    await request(this.o, "POST", `rest/api/2/issue/${encodeURIComponent(key)}/transitions`, { transition: { id: t.id } });
  }

  worklog(key: string, seconds: number, comment: string, start = new Date()) {
    return request(this.o, "POST", `rest/api/2/issue/${encodeURIComponent(key)}/worklog`, { comment, started: jiraDate(start), timeSpentSeconds: Math.max(60, Math.round(seconds)) });
  }

  // ---------------- Zeitbuchung ----------------

  private me?: { name: string; key: string; displayName: string };
  async meCached() {
    if (!this.me) { const m = await this.myself(); this.me = { name: m?.name ?? "", key: m?.key ?? "", displayName: m?.displayName ?? "" }; }
    return this.me;
  }

  /** Suche inkl. optionalem Account-Feld (Tempo o. ä.). */
  async searchFull(jql: string, max = 100, accountField?: string): Promise<JiraIssueFull[]> {
    const fields = ["summary", "status", "updated", "issuetype", ...(accountField ? [accountField] : [])];
    const out: JiraIssueFull[] = [];
    let startAt = 0;
    while (out.length < max) {
      const r = await request<any>(this.o, "POST", "rest/api/2/search", { jql, startAt, maxResults: Math.min(100, max - out.length), fields });
      const issues = r?.issues ?? [];
      for (const i of issues) out.push({
        key: i.key, summary: i.fields?.summary ?? "", status: i.fields?.status?.name ?? "", statusCategory: i.fields?.status?.statusCategory?.key ?? "",
        updated: i.fields?.updated ? Date.parse(i.fields.updated) : 0, account: accountField ? readAccount(i.fields?.[accountField]) : undefined,
      });
      startAt += issues.length;
      if (!issues.length || startAt >= (r?.total ?? 0)) break;
    }
    return out;
  }

  async issueFull(key: string, accountField?: string): Promise<JiraIssueFull | undefined> {
    try { return (await this.searchFull(`key = "${key}"`, 1, accountField))[0]; } catch { return undefined; }
  }

  /** Eigene Worklogs in einem Zeitraum. */
  async myWorklogs(from: Date, to: Date): Promise<JiraWorklog[]> {
    const me = await this.meCached();
    const f = ymd(from), t = ymd(to);
    const issues = await this.searchFull(`worklogAuthor = currentUser() AND worklogDate >= "${f}" AND worklogDate <= "${t}"`, 300);
    const out: JiraWorklog[] = [];
    await Promise.all(issues.map(async (i) => {
      const r = await request<any>(this.o, "GET", `rest/api/2/issue/${encodeURIComponent(i.key)}/worklog`).catch(() => undefined);
      for (const w of r?.worklogs ?? []) {
        const a = w.author ?? {};
        if (a.name !== me.name && a.key !== me.key) continue;
        const started = Date.parse(w.started);
        const d = ymd(new Date(started));
        if (d < f || d > t) continue;
        out.push({ id: String(w.id), issueKey: i.key, issueSummary: i.summary, started, seconds: Number(w.timeSpentSeconds ?? 0), comment: typeof w.comment === "string" ? w.comment : "" });
      }
    }));
    return out.sort((a, b) => a.started - b.started);
  }

  /** Buchen – über Tempo (inkl. Account-Attribut) oder Standard-Worklog. */
  async book(key: string, start: Date, seconds: number, comment: string, opts: { tempo: boolean; account?: string; accountAttribute: string }) {
    if (opts.tempo) {
      const me = await this.meCached();
      const attributes: Record<string, unknown> = {};
      if (opts.account) attributes[opts.accountAttribute] = { value: opts.account };
      const pad = (n: number) => String(n).padStart(2, "0");
      return request(this.o, "POST", "rest/tempo-timesheets/4/worklogs/", {
        worker: me.key || me.name, originTaskId: key, timeSpentSeconds: seconds, billableSeconds: seconds, comment, attributes,
        started: `${ymd(start)}T${pad(start.getHours())}:${pad(start.getMinutes())}:00.000`,
      });
    }
    return this.worklog(key, seconds, comment, start);
  }

  async tempoAccounts(): Promise<{ key: string; name: string }[]> {
    try {
      const r = await request<any[]>(this.o, "GET", "rest/tempo-accounts/1/account");
      return (r ?? []).filter((a) => !a.status || a.status === "OPEN").map((a) => ({ key: String(a.key), name: String(a.name ?? a.key) }));
    } catch { return []; }
  }
}

export interface JiraIssueFull { key: string; summary: string; status: string; statusCategory: string; updated: number; account?: string }
export interface JiraWorklog { id: string; issueKey: string; issueSummary: string; started: number; seconds: number; comment: string }

export function ymd(d: Date) { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; }

export function jiraDate(d: Date) {
  const off = -d.getTimezoneOffset();
  const pad = (n: number) => String(Math.abs(n)).padStart(2, "0");
  return `${ymd(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}:00.000${off >= 0 ? "+" : "-"}${pad(Math.trunc(off / 60))}${pad(off % 60)}`;
}

/** Account-Feld kann Objekt (Tempo), Option, String, Zahl oder Liste sein. */
export function readAccount(v: any): string | undefined {
  if (v == null || v === "") return undefined;
  if (Array.isArray(v)) return v.length ? readAccount(v[0]) : undefined;
  if (typeof v !== "object") return String(v);
  for (const k of ["key", "value", "name", "id"]) if (v[k] != null && v[k] !== "") return String(v[k]);
  return undefined;
}

// ------------------------------------------------------------------ GitLab

export class GitLab {
  private o: HttpOptions;
  constructor(baseUrl: string, token: string, ignoreCerts: boolean) {
    this.o = { baseUrl: baseUrl.replace(/\/$/, "") + "/api/v4", headers: { "PRIVATE-TOKEN": token }, ignoreCerts };
  }

  private id(p: string) { return /^\d+$/.test(p) ? p : encodeURIComponent(p); }

  /** Eigene Events (Pushes, MRs, Kommentare) an einem Tag. */
  async myEvents(day: Date): Promise<{ ts: number; action: string; target: string; ref: string; commitTitle: string; commits: number; project: string }[]> {
    const me = await request<any>(this.o, "GET", "user");
    const after = new Date(day.getFullYear(), day.getMonth(), day.getDate() - 1), before = new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1);
    const d = (x: Date) => `${x.getFullYear()}-${String(x.getMonth() + 1).padStart(2, "0")}-${String(x.getDate()).padStart(2, "0")}`;
    const out: any[] = [];
    const names = new Map<number, string>();
    for (let page = 1; page <= 4; page++) {
      const r = await request<any[]>(this.o, "GET", `users/${me.id}/events?after=${d(after)}&before=${d(before)}&per_page=100&page=${page}`);
      for (const e of r ?? []) {
        const ts = Date.parse(e.created_at);
        if (new Date(ts).toDateString() !== day.toDateString()) continue;
        if (!names.has(e.project_id)) names.set(e.project_id, await request<any>(this.o, "GET", `projects/${e.project_id}`).then((p) => p?.path_with_namespace ?? String(e.project_id)).catch(() => String(e.project_id)));
        out.push({ ts, action: e.action_name ?? "", target: `${e.target_type ?? ""} ${e.target_title ?? ""}`.trim(), ref: e.push_data?.ref ?? "", commitTitle: e.push_data?.commit_title ?? "", commits: e.push_data?.commit_count ?? 0, project: names.get(e.project_id)! });
      }
      if (!r || r.length < 100) break;
    }
    return out.sort((a, b) => a.ts - b.ts);
  }

  async latestPipeline(project: string, ref: string): Promise<{ id: number; status: string; webUrl: string } | undefined> {
    const r = await request<any[]>(this.o, "GET", `projects/${this.id(project)}/pipelines?ref=${encodeURIComponent(ref)}&per_page=1&order_by=id&sort=desc`);
    const p = r?.[0];
    return p ? { id: p.id, status: p.status, webUrl: p.web_url } : undefined;
  }

  async createMr(project: string, source: string, target: string, title: string, description: string): Promise<string> {
    const r = await request<any>(this.o, "POST", `projects/${this.id(project)}/merge_requests`,
      { source_branch: source, target_branch: target, title, description, remove_source_branch: true });
    return r?.web_url ?? "";
  }

  async defaultBranch(project: string): Promise<string> {
    const r = await request<any>(this.o, "GET", `projects/${this.id(project)}`);
    return r?.default_branch ?? "main";
  }
}
