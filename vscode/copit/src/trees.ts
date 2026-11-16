import * as vscode from "vscode";
import { Agent, Automation, COLUMNS } from "./model";
import { TRIGGERS } from "./automation";

const STATUS_ICON: Record<string, [string, string]> = {
  inbox: ["inbox", "charts.purple"], in_progress: ["sync~spin", "charts.blue"], waiting_subagent: ["type-hierarchy", "charts.yellow"],
  need_input: ["question", "charts.orange"], review: ["git-pull-request", "charts.red"], done: ["pass", "charts.green"], failed: ["error", "errorForeground"],
};

type Node = { kind: "nav"; label: string; command: string; icon: string } | { kind: "group"; status: string; agents: Agent[] } | { kind: "agent"; agent: Agent };

export class AgentsTree implements vscode.TreeDataProvider<Node> {
  private ev = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.ev.event;
  constructor(private agents: () => Agent[], private running: (id: string) => boolean) {}
  refresh() { this.ev.fire(); }

  getChildren(n?: Node): Node[] {
    if (!n) {
      const all = this.agents();
      const nav: Node[] = [
        { kind: "nav", label: "Zeitbuchung", command: "copit.openTime", icon: "clock" },
        { kind: "nav", label: "Board", command: "copit.openBoard", icon: "project" },
        { kind: "nav", label: "Activity", command: "copit.openCalendar", icon: "pulse" },
        { kind: "nav", label: "Budgets", command: "copit.openBudgets", icon: "credit-card" },
        { kind: "nav", label: "Customize", command: "copit.openPermissions", icon: "shield" },
      ];
      return [...nav, ...COLUMNS.map((c) => ({ kind: "group" as const, status: c.id, agents: all.filter((a) => a.status === c.id) })).filter((g) => g.agents.length)];
    }
    if (n.kind === "group") return n.agents.map((agent) => ({ kind: "agent" as const, agent }));
    return [];
  }

  getTreeItem(n: Node): vscode.TreeItem {
    if (n.kind === "nav") {
      const t = new vscode.TreeItem(n.label);
      t.iconPath = new vscode.ThemeIcon(n.icon);
      t.command = { command: n.command, title: n.label };
      return t;
    }
    if (n.kind === "group") {
      const col = COLUMNS.find((c) => c.id === n.status)!;
      const t = new vscode.TreeItem(col.title.toUpperCase(), vscode.TreeItemCollapsibleState.Expanded);
      t.description = String(n.agents.length);
      const [ic, color] = STATUS_ICON[n.status];
      t.iconPath = new vscode.ThemeIcon(ic.replace("~spin", ""), new vscode.ThemeColor(color));
      return t;
    }
    const a = n.agent;
    const t = new vscode.TreeItem(`${a.ticket ? a.ticket + "  " : ""}${a.title}`);
    const running = this.running(a.id);
    const [ic, color] = running ? ["sync~spin", "charts.orange"] : STATUS_ICON[a.status];
    t.iconPath = new vscode.ThemeIcon(ic, new vscode.ThemeColor(color));
    t.description = [a.project, a.linesAdded || a.linesRemoved ? `+${a.linesAdded} -${a.linesRemoved}` : "", a.costUsd ? `$${a.costUsd.toFixed(2)}` : ""].filter(Boolean).join(" · ");
    t.tooltip = new vscode.MarkdownString(`**${a.title}**\n\n${a.pendingQuestion ?? a.summary ?? ""}`);
    t.contextValue = running ? "agent-running" : "agent";
    t.command = { command: "copit.openAgent", title: "Öffnen", arguments: [a.id] };
    return t;
  }
}

export class AutomationsTree implements vscode.TreeDataProvider<Automation> {
  private ev = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.ev.event;
  constructor(private list: () => Automation[]) {}
  refresh() { this.ev.fire(); }
  getChildren(n?: Automation) { return n ? [] : this.list().sort((a, b) => Number(b.enabled) - Number(a.enabled)); }
  getTreeItem(a: Automation): vscode.TreeItem {
    const t = new vscode.TreeItem(a.name);
    t.description = TRIGGERS.find((x) => x.type === a.trigger.type)?.label.toUpperCase();
    const ok = a.lastResult && !a.lastResult.startsWith("Fehler");
    t.iconPath = !a.enabled ? new vscode.ThemeIcon("circle-slash", new vscode.ThemeColor("descriptionForeground"))
      : a.lastRun ? new vscode.ThemeIcon(ok ? "pass-filled" : "error", new vscode.ThemeColor(ok ? "charts.green" : "errorForeground"))
      : new vscode.ThemeIcon("circle-large-outline");
    t.tooltip = a.lastResult ? `${a.lastResult}\n${new Date(a.lastRun ?? 0).toLocaleString("de-DE")}` : "Noch nicht ausgeführt";
    t.contextValue = "automation";
    t.command = { command: "copit.openAutomations", title: "Öffnen", arguments: [a.id] };
    return t;
  }
}
