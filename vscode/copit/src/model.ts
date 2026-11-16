export type AgentStatus = "inbox" | "in_progress" | "waiting_subagent" | "need_input" | "review" | "done" | "failed";

export const COLUMNS: { id: AgentStatus; title: string; color: string }[] = [
  { id: "inbox", title: "Inbox", color: "#8B7CF6" },
  { id: "in_progress", title: "In Progress", color: "#4FB3D9" },
  { id: "waiting_subagent", title: "Waiting for Subagent", color: "#D9A441" },
  { id: "need_input", title: "Need Input", color: "#E8743B" },
  { id: "review", title: "Review", color: "#D96BA0" },
  { id: "done", title: "Done", color: "#7BC47F" },
  { id: "failed", title: "Failed", color: "#E0604F" },
];

export type Provider = "claude" | "copilot";
export type Mode = "ask" | "plan" | "edit" | "auto";

export interface Agent {
  id: string;
  title: string;
  prompt: string;
  ticket?: string;
  project: string;
  /** Repository-Wurzel */
  repo: string;
  /** Arbeitsordner (Repo oder Worktree) */
  cwd: string;
  branch?: string;
  worktree?: boolean;
  /** Commit beim Anlegen – Basis für +/- Zeilen */
  baseSha?: string;
  provider: Provider;
  mode: Mode;
  model?: string;
  status: AgentStatus;
  sessionId?: string;
  createdAt: number;
  updatedAt: number;
  /** Zeit, in der das Modell generiert hat (Agent-Zeit minus Tool-Ausführung) */
  aiMs: number;
  /** Wanduhr-Laufzeit aller Läufe */
  agentMs: number;
  toolMs: number;
  costUsd: number;
  contextTokens: number;
  turns: number;
  linesAdded: number;
  linesRemoved: number;
  filesChanged: number;
  summary?: string;
  error?: string;
  pendingQuestion?: string;
  /** Freigabe-Anfragen der CLI, die noch beantwortet werden müssen */
  pendingPermissions?: { tool: string; input: string }[];
  allowedOnce?: string[];
  running?: boolean;
}

export type EventKind = "user" | "text" | "thinking" | "tool_use" | "tool_result" | "result" | "error" | "status" | "init";

export interface AgentEvent {
  id?: number;
  agentId: string;
  runId: string;
  ts: number;
  kind: EventKind;
  /** Bei Sub-Agents: Tool-Use-ID des Task-Aufrufs */
  parentToolId?: string;
  toolId?: string;
  name?: string;
  text?: string;
  durationMs?: number;
  isError?: boolean;
}

export interface Run {
  id: string;
  agentId: string;
  startedAt: number;
  endedAt?: number;
  aiMs: number;
  toolMs: number;
  costUsd: number;
  turns: number;
  prompt: string;
  ok?: boolean;
}

// ---------------- Permissions ----------------

export type PermissionLevel = "allow" | "ask" | "deny";

export interface PermissionOp {
  id: string;
  label: string;
  group: "Git" | "Dateien" | "Shell" | "Netz";
  /** Muster im Format der Claude-CLI (--allowedTools / --disallowedTools) */
  patterns: string[];
  risky?: boolean;
}

export const PERMISSION_OPS: PermissionOp[] = [
  { id: "git.read", label: "status / diff / log", group: "Git", patterns: ["Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git branch:*)"] },
  { id: "git.branch", label: "Branch erstellen / wechseln", group: "Git", patterns: ["Bash(git checkout:*)", "Bash(git switch:*)"] },
  { id: "git.add", label: "add / stage", group: "Git", patterns: ["Bash(git add:*)", "Bash(git restore:*)"] },
  { id: "git.commit", label: "commit", group: "Git", patterns: ["Bash(git commit:*)"] },
  { id: "git.push", label: "push", group: "Git", patterns: ["Bash(git push:*)"] },
  { id: "git.forcepush", label: "force push", group: "Git", patterns: ["Bash(git push --force:*)", "Bash(git push -f:*)"], risky: true },
  { id: "git.merge", label: "merge / rebase", group: "Git", patterns: ["Bash(git merge:*)", "Bash(git rebase:*)"] },
  { id: "git.reset", label: "reset / clean", group: "Git", patterns: ["Bash(git reset:*)", "Bash(git clean:*)"], risky: true },
  { id: "git.tag", label: "tag", group: "Git", patterns: ["Bash(git tag:*)"] },
  { id: "git.pull", label: "fetch / pull", group: "Git", patterns: ["Bash(git fetch:*)", "Bash(git pull:*)"] },
  { id: "files.read", label: "Dateien lesen", group: "Dateien", patterns: ["Read", "Grep", "Glob"] },
  { id: "files.write", label: "Dateien ändern", group: "Dateien", patterns: ["Edit", "Write", "MultiEdit", "NotebookEdit"] },
  { id: "shell.build", label: "Build / Tests", group: "Shell", patterns: ["Bash(npm:*)", "Bash(dotnet:*)", "Bash(composer:*)", "Bash(php:*)", "Bash(yarn:*)", "Bash(pnpm:*)"] },
  { id: "shell.any", label: "Beliebige Shell", group: "Shell", patterns: ["Bash"], risky: true },
  { id: "net.fetch", label: "Web abrufen", group: "Netz", patterns: ["WebFetch", "WebSearch"] },
];

export interface PermissionMatrix {
  /** Spalten: Scope-Namen (Standard, Projekte, Modus) */
  scopes: string[];
  /** scope -> opId -> level */
  levels: Record<string, Record<string, PermissionLevel>>;
}

// ---------------- Automationen ----------------

export type TriggerType =
  | "agent.started" | "agent.finished" | "agent.needs_input" | "agent.failed" | "agent.stopped" | "agent.moved"
  | "git.commit" | "git.push" | "git.branch"
  | "pipeline.failed" | "pipeline.success"
  | "jira.transitioned" | "jira.assigned"
  | "schedule";

export type ActionType =
  | "agent.set_status" | "agent.start" | "agent.continue"
  | "notify"
  | "jira.comment" | "jira.transition" | "jira.worklog"
  | "git.commit" | "git.push" | "git.branch"
  | "gitlab.mr"
  | "shell";

export interface Condition { field: string; op: "equals" | "contains" | "not_equals" | "matches"; value: string }

export interface Action { type: ActionType; params: Record<string, string> }

export interface Automation {
  id: string;
  name: string;
  enabled: boolean;
  trigger: { type: TriggerType; params: Record<string, string> };
  conditions: Condition[];
  actions: Action[];
  lastRun?: number;
  lastResult?: string;
  runs?: number;
}

export interface AutomationEvent {
  type: TriggerType;
  agent?: Agent;
  /** Zusätzliche Template-Variablen (branch, ticket, status, pipeline, ...) */
  vars: Record<string, string>;
}

// ---------------- Budgets ----------------

export type BudgetType = "cost" | "hours";
export type BudgetPeriod = "week" | "month" | "total";

export interface Budget {
  id: string;
  name: string;
  type: BudgetType;
  /** USD bzw. Agent-Stunden */
  amount: number;
  period: BudgetPeriod;
  /** Projektordner (Repo-Wurzeln); leer = alle */
  folders: string[];
  /** Warnschwellen in Prozent */
  warnAt: number[];
  /** Neue Agent-Läufe blockieren, wenn 100 % erreicht */
  hardStop: boolean;
  /** Bereits gemeldete Schwellen: "<periodKey>:<pct>" */
  notified?: string[];
}

export interface BudgetStatus {
  budget: Budget;
  used: number;
  fraction: number;
  periodLabel: string;
  periodKey: string;
}

export function newId(): string {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}
