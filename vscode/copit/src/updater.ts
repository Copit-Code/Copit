import * as fs from "node:fs";
import * as https from "node:https";
import * as os from "node:os";
import * as path from "node:path";
import { execFile } from "node:child_process";

export interface ReleaseInfo { tag: string; version: string; name: string; notes: string; assetUrl?: string; assetName?: string; htmlUrl: string }

/** Vergleicht semantische Versionen ("1.2.10" > "1.2.9"). */
export function isNewer(remote: string, local: string): boolean {
  const p = (v: string) => v.replace(/^v/, "").split(/[.-]/).map((x) => parseInt(x) || 0);
  const a = p(remote), b = p(local);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    if ((a[i] ?? 0) !== (b[i] ?? 0)) return (a[i] ?? 0) > (b[i] ?? 0);
  }
  return false;
}

function get(url: string, token: string | undefined, accept: string, redirects = 5): Promise<{ status: number; body: Buffer; location?: string }> {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const headers: Record<string, string> = { "User-Agent": "copit-updater", Accept: accept };
    // Token nur an GitHub selbst schicken, nicht an Download-CDNs nach Redirect
    if (token && (u.hostname === "api.github.com" || u.hostname === "github.com")) headers.Authorization = `Bearer ${token}`;
    https.get(u, { headers }, (res) => {
      if (res.statusCode && res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
        res.resume();
        return resolve(get(res.headers.location, token, accept, redirects - 1));
      }
      const chunks: Buffer[] = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => resolve({ status: res.statusCode ?? 0, body: Buffer.concat(chunks) }));
    }).on("error", reject);
  });
}

/** Token aus Einstellungen oder (falls vorhanden) aus der GitHub CLI. */
export function ghToken(): Promise<string | undefined> {
  return new Promise((resolve) => {
    execFile("gh", ["auth", "token"], { windowsHide: true, timeout: 5000 }, (e, out) => resolve(e ? undefined : String(out).trim() || undefined));
  });
}

export async function latestRelease(repo: string, token?: string): Promise<ReleaseInfo | undefined> {
  const r = await get(`https://api.github.com/repos/${repo}/releases/latest`, token, "application/vnd.github+json");
  if (r.status === 404) return undefined;
  if (r.status >= 400) throw new Error(`GitHub ${r.status}: ${r.body.toString().slice(0, 200)}`);
  const j = JSON.parse(r.body.toString());
  const asset = (j.assets ?? []).find((a: any) => String(a.name).endsWith(".vsix"));
  return {
    tag: j.tag_name, version: String(j.tag_name).replace(/^v/, ""), name: j.name ?? j.tag_name, notes: j.body ?? "",
    assetUrl: asset?.url, assetName: asset?.name, htmlUrl: j.html_url,
  };
}

/** Lädt das .vsix-Asset herunter (über die API-URL, funktioniert auch bei privaten Repos). */
export async function downloadAsset(rel: ReleaseInfo, token?: string): Promise<string> {
  if (!rel.assetUrl) throw new Error("Release enthält keine .vsix-Datei");
  const r = await get(rel.assetUrl, token, "application/octet-stream");
  if (r.status >= 400) throw new Error(`Download fehlgeschlagen (${r.status})`);
  const file = path.join(os.tmpdir(), rel.assetName ?? `copit-${rel.version}.vsix`);
  fs.writeFileSync(file, r.body);
  return file;
}
