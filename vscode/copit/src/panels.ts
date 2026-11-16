import * as vscode from "vscode";

export interface PanelProvider {
  state(): unknown | Promise<unknown>;
  onAct?(msg: { act: string; arg?: string; value?: string }): unknown | Promise<unknown>;
  onMessage?(msg: any): unknown | Promise<unknown>;
  dispose?(): void;
}

/** Webview-Panel, das komplett aus einem State rendert. */
export class Panel {
  static readonly all = new Map<string, Panel>();
  private timer?: NodeJS.Timeout;
  private ready = false;
  readonly panel: vscode.WebviewPanel;

  private constructor(private ctx: vscode.ExtensionContext, readonly key: string, title: string, script: string, readonly provider: PanelProvider, column: vscode.ViewColumn) {
    this.panel = vscode.window.createWebviewPanel("copit." + script, title, column, {
      enableScripts: true, retainContextWhenHidden: true,
      localResourceRoots: [vscode.Uri.joinPath(ctx.extensionUri, "dist")],
    });
    this.panel.iconPath = vscode.Uri.joinPath(ctx.extensionUri, "media", "copit.svg");
    this.panel.webview.html = this.html(script);
    this.panel.webview.onDidReceiveMessage(async (m) => {
      try {
        if (m.type === "ready") { this.ready = true; await this.push(); }
        else if (m.type === "refresh") await this.push();
        else if (m.type === "act") { await provider.onAct?.(m); this.refresh(); }
        else await provider.onMessage?.(m);
      } catch (e: any) {
        vscode.window.showErrorMessage(`Copit: ${e?.message ?? e}`);
      }
    });
    this.panel.onDidDispose(() => { Panel.all.delete(key); provider.dispose?.(); clearTimeout(this.timer); });
  }

  static show(ctx: vscode.ExtensionContext, key: string, title: string, script: string, provider: () => PanelProvider, column = vscode.ViewColumn.Active): Panel {
    const existing = Panel.all.get(key);
    if (existing) { existing.panel.reveal(column); existing.refresh(); return existing; }
    const p = new Panel(ctx, key, title, script, provider(), column);
    Panel.all.set(key, p);
    return p;
  }

  /** Alle offenen Panels, deren Key mit prefix beginnt, neu rendern. */
  static refreshAll(prefix = "") {
    for (const [k, p] of Panel.all) if (k.startsWith(prefix)) p.refresh();
  }

  refresh(delay = 120) {
    if (this.timer) return;
    this.timer = setTimeout(() => { this.timer = undefined; void this.push(); }, delay);
  }

  post(msg: unknown) { void this.panel.webview.postMessage(msg); }

  private async push() {
    if (!this.ready) return;
    try { this.post({ type: "state", state: await this.provider.state() }); }
    catch (e: any) { console.error("copit panel", e); }
  }

  private html(script: string) {
    const w = this.panel.webview;
    const js = w.asWebviewUri(vscode.Uri.joinPath(this.ctx.extensionUri, "dist", "webview", script + ".js"));
    const css = w.asWebviewUri(vscode.Uri.joinPath(this.ctx.extensionUri, "dist", "style.css"));
    const nonce = Math.random().toString(36).slice(2) + Date.now().toString(36);
    return `<!doctype html><html lang="de"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${w.cspSource} data:; style-src ${w.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${w.cspSource};">
<meta name="viewport" content="width=device-width, initial-scale=1"><link rel="stylesheet" href="${css}"></head>
<body><div id="app"></div><script nonce="${nonce}" src="${js}"></script></body></html>`;
  }
}
