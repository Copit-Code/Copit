import { execFile } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";

export interface GitResult { code: number; out: string; err: string }

export function git(cwd: string, ...args: string[]): Promise<GitResult> {
  return new Promise((resolve) => {
    execFile("git", ["-c", "core.quotepath=off", "-c", "color.ui=never", ...args], { cwd, maxBuffer: 32 * 1024 * 1024, windowsHide: true },
      (e, out, err) => resolve({ code: e ? (typeof (e as any).code === "number" ? (e as any).code : 1) : 0, out: String(out), err: String(err) }));
  });
}

async function ok(cwd: string, ...args: string[]): Promise<string> {
  const r = await git(cwd, ...args);
  if (r.code !== 0) throw new Error((r.err || r.out).trim() || `git ${args[0]} fehlgeschlagen`);
  return r.out;
}

export async function repoRoot(dir: string): Promise<string | undefined> {
  const r = await git(dir, "rev-parse", "--show-toplevel");
  return r.code === 0 ? path.normalize(r.out.trim()) : undefined;
}

export async function headSha(cwd: string): Promise<string | undefined> {
  const r = await git(cwd, "rev-parse", "HEAD");
  return r.code === 0 ? r.out.trim() : undefined;
}

export async function currentBranch(cwd: string): Promise<string> {
  return (await git(cwd, "rev-parse", "--abbrev-ref", "HEAD")).out.trim();
}

export async function branchExists(cwd: string, branch: string): Promise<boolean> {
  return (await git(cwd, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch)).code === 0;
}

/** Legt (falls nötig) einen Worktree für den Branch an und gibt dessen Pfad zurück. */
export async function ensureWorktree(repo: string, branch: string, id: string): Promise<string> {
  const dir = path.join(repo, ".copit", "worktrees", id);
  if (fs.existsSync(path.join(dir, ".git"))) return dir;
  fs.mkdirSync(path.dirname(dir), { recursive: true });
  ensureIgnored(repo);
  if (await branchExists(repo, branch)) await ok(repo, "worktree", "add", dir, branch);
  else await ok(repo, "worktree", "add", "-b", branch, dir);
  return dir;
}

export async function removeWorktree(repo: string, dir: string) {
  await git(repo, "worktree", "remove", "--force", dir);
}

function ensureIgnored(repo: string) {
  try {
    const f = path.join(repo, ".git", "info", "exclude");
    const txt = fs.existsSync(f) ? fs.readFileSync(f, "utf8") : "";
    if (!txt.includes(".copit/")) fs.appendFileSync(f, "\n.copit/\n");
  } catch { /* egal */ }
}

export async function checkoutBranch(cwd: string, branch: string) {
  if (await branchExists(cwd, branch)) await ok(cwd, "checkout", branch);
  else await ok(cwd, "checkout", "-b", branch);
}

export interface DiffStat { added: number; removed: number; files: { path: string; added: number; removed: number; status: string }[] }

/** Änderungen seit einem Commit inklusive Arbeitskopie und neuer Dateien. */
export async function diffStat(cwd: string, base?: string): Promise<DiffStat> {
  const res: DiffStat = { added: 0, removed: 0, files: [] };
  const num = await git(cwd, "diff", "--numstat", base ?? "HEAD");
  for (const l of num.out.split("\n")) {
    const [a, r, p] = l.split("\t");
    if (!p) continue;
    const add = parseInt(a) || 0, rem = parseInt(r) || 0;
    res.files.push({ path: p.trim(), added: add, removed: rem, status: "M" });
    res.added += add; res.removed += rem;
  }
  const untracked = await git(cwd, "ls-files", "--others", "--exclude-standard");
  for (const p of untracked.out.split("\n").map((s) => s.trim()).filter((s) => s && !s.startsWith(".copit/"))) {
    let lines = 0;
    try { lines = fs.readFileSync(path.join(cwd, p), "utf8").split("\n").length; } catch { /* binär */ }
    res.files.push({ path: p, added: lines, removed: 0, status: "A" });
    res.added += lines;
  }
  return res;
}

export async function commitAll(cwd: string, message: string): Promise<string> {
  await ok(cwd, "add", "-A");
  return ok(cwd, "commit", "-m", message);
}

export async function push(cwd: string): Promise<string> {
  const branch = await currentBranch(cwd);
  const up = await git(cwd, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
  const r = up.code === 0 ? await git(cwd, "push") : await git(cwd, "push", "-u", "origin", branch);
  if (r.code !== 0) throw new Error((r.err || r.out).trim());
  return (r.err + r.out).trim();
}

export async function remoteUrl(cwd: string): Promise<string | undefined> {
  const r = await git(cwd, "remote", "get-url", "origin");
  return r.code === 0 ? r.out.trim() : undefined;
}

export function projectPathFromRemote(remote?: string): string | undefined {
  if (!remote) return undefined;
  let p: string;
  if (/^[\w.-]+@[^:]+:/.test(remote)) p = remote.slice(remote.indexOf(":") + 1);
  else { try { p = new URL(remote).pathname; } catch { return undefined; } }
  p = p.replace(/^\/+|\/+$/g, "").replace(/\.git$/, "");
  return p || undefined;
}

export async function lastCommit(cwd: string): Promise<{ sha: string; subject: string; author: string } | undefined> {
  const r = await git(cwd, "log", "-1", "--pretty=format:%H\x1f%s\x1f%an");
  if (r.code !== 0 || !r.out) return undefined;
  const [sha, subject, author] = r.out.split("\x1f");
  return { sha, subject, author };
}

export async function fullDiff(cwd: string, base?: string, max = 60000): Promise<string> {
  const d = (await git(cwd, "diff", base ?? "HEAD")).out;
  return d.length > max ? d.slice(0, max) + "\n… (gekürzt)" : d;
}

/** Datei-Inhalt zu einem Commit (für Diff-Ansicht). */
export async function showFile(cwd: string, rev: string, file: string): Promise<string> {
  const r = await git(cwd, "show", `${rev}:${file.replace(/\\/g, "/")}`);
  return r.code === 0 ? r.out : "";
}
