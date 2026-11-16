import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { PermissionLevel, PERMISSION_OPS } from "./model";

/** Woher eine Regel stammt: Claude global, Claude-Projektdatei oder Copit (per CLI-Flag). */
export type RuleSource = "global" | "project" | "copit";

export interface PermissionRule {
  id: string;
  pattern: string;
  level: PermissionLevel;
  source: RuleSource;
  /** Bei source=project: Repo-Pfad */
  repo?: string;
  enabled: boolean;
}

const LEVELS: PermissionLevel[] = ["deny", "ask", "allow"];

export function globalSettingsPath() { return path.join(os.homedir(), ".claude", "settings.json"); }
export function projectSettingsPath(repo: string) { return path.join(repo, ".claude", "settings.json"); }

function readJson(file: string): any {
  try { return JSON.parse(fs.readFileSync(file, "utf8")); } catch { return {}; }
}

function writeJson(file: string, data: any) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(data, null, 2) + "\n", "utf8");
}

export function ruleId(source: RuleSource, pattern: string, repo?: string) {
  return `${source}|${repo ?? ""}|${pattern}`;
}

/** Liest Regeln aus einer Claude-settings.json. */
export function readClaudeRules(file: string, source: RuleSource, repo?: string): PermissionRule[] {
  const p = readJson(file)?.permissions ?? {};
  const out: PermissionRule[] = [];
  for (const level of LEVELS)
    for (const pattern of (p[level] ?? []) as string[])
      out.push({ id: ruleId(source, pattern, repo), pattern, level, source, repo, enabled: true });
  return out;
}

/** Schreibt eine Regel in eine Claude-settings.json (entfernt sie aus den anderen Listen). */
export function writeClaudeRule(file: string, pattern: string, level: PermissionLevel | null) {
  const data = readJson(file);
  data.permissions ??= {};
  for (const l of LEVELS) {
    const list: string[] = data.permissions[l] ?? [];
    data.permissions[l] = list.filter((x) => x !== pattern);
    if (!data.permissions[l].length) delete data.permissions[l];
  }
  if (level) (data.permissions[level] ??= []).push(pattern);
  writeJson(file, data);
}

/** Beschreibung einer Regel für die Liste, z. B. "Bash · Bash(git push:*)" → Titel "git push *". */
export function describeRule(pattern: string): { title: string; tool: string } {
  const m = /^(\w+)\((.*)\)$/.exec(pattern);
  if (!m) return { title: pattern, tool: pattern };
  return { title: m[2].replace(/:\*$/, " *"), tool: m[1] };
}

/** CLI-Flags für einen Agent: Copit-Regeln (global + projektbezogen). Claude-eigene Dateien wirken ohnehin. */
export function flagsFor(rules: PermissionRule[], repo: string, includeClaudeFiles: boolean): { allowed: string[]; disallowed: string[] } {
  const applicable = rules.filter((r) => r.enabled && (
    r.source === "copit" && (!r.repo || samePath(r.repo, repo)) ||
    includeClaudeFiles && (r.source === "global" || (r.source === "project" && r.repo && samePath(r.repo, repo)))));
  // deny schlägt ask schlägt allow
  const deny = new Set(applicable.filter((r) => r.level === "deny").map((r) => r.pattern));
  const allow = applicable.filter((r) => r.level === "allow" && !deny.has(r.pattern)).map((r) => r.pattern);
  return { allowed: [...new Set(allow)], disallowed: [...deny] };
}

export function samePath(a: string, b: string) {
  return path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
}

/** Git-Matrix: Zelle (Operation × Scope) → Copit-Regeln. */
export function matrixLevel(rules: PermissionRule[], opId: string, repo?: string): PermissionLevel {
  const op = PERMISSION_OPS.find((o) => o.id === opId);
  if (!op) return "ask";
  const own = rules.filter((r) => r.source === "copit" && r.enabled && (repo ? r.repo && samePath(r.repo, repo) : !r.repo) && op.patterns.includes(r.pattern));
  if (own.some((r) => r.level === "deny")) return "deny";
  if (own.length && own.every((r) => r.level === "allow")) return "allow";
  return "ask";
}

export function setMatrixLevel(rules: PermissionRule[], opId: string, level: PermissionLevel, repo?: string): PermissionRule[] {
  const op = PERMISSION_OPS.find((o) => o.id === opId);
  if (!op) return rules;
  const rest = rules.filter((r) => !(r.source === "copit" && op.patterns.includes(r.pattern) && (repo ? r.repo && samePath(r.repo, repo) : !r.repo)));
  if (level === "ask") return rest; // "ask" = keine Copit-Regel → CLI fragt nach
  return [...rest, ...op.patterns.map((p) => ({ id: ruleId("copit", p, repo), pattern: p, level, source: "copit" as const, repo, enabled: true }))];
}

/** Sinnvolle Startregeln für Copit. */
export function defaultCopitRules(): PermissionRule[] {
  let rules: PermissionRule[] = [];
  const set = (op: string, l: PermissionLevel) => { rules = setMatrixLevel(rules, op, l); };
  set("git.read", "allow"); set("files.read", "allow"); set("files.write", "allow"); set("shell.build", "allow");
  set("git.branch", "allow"); set("git.add", "allow"); set("git.pull", "allow");
  set("git.forcepush", "deny"); set("git.reset", "deny");
  return rules;
}
