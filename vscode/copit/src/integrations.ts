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

  worklog(key: string, seconds: number, comment: string) {
    const d = new Date();
    const off = -d.getTimezoneOffset();
    const pad = (n: number) => String(Math.abs(n)).padStart(2, "0");
    const started = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:00.000${off >= 0 ? "+" : "-"}${pad(Math.trunc(off / 60))}${pad(off % 60)}`;
    return request(this.o, "POST", `rest/api/2/issue/${encodeURIComponent(key)}/worklog`, { comment, started, timeSpentSeconds: Math.max(60, Math.round(seconds)) });
  }
}

// ------------------------------------------------------------------ GitLab

export class GitLab {
  private o: HttpOptions;
  constructor(baseUrl: string, token: string, ignoreCerts: boolean) {
    this.o = { baseUrl: baseUrl.replace(/\/$/, "") + "/api/v4", headers: { "PRIVATE-TOKEN": token }, ignoreCerts };
  }

  private id(p: string) { return /^\d+$/.test(p) ? p : encodeURIComponent(p); }

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
