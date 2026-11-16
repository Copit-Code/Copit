import * as esbuild from "esbuild";
import { copyFileSync, mkdirSync, readdirSync } from "node:fs";

const watch = process.argv.includes("--watch");
const production = process.argv.includes("--production");
const tests = process.argv.includes("--tests");

mkdirSync("dist", { recursive: true });
copyFileSync("node_modules/sql.js/dist/sql-wasm.wasm", "dist/sql-wasm.wasm");
copyFileSync("webview/style.css", "dist/style.css");

const common = { bundle: true, sourcemap: !production, minify: production, logLevel: "info" };

const builds = [
  { ...common, entryPoints: ["src/extension.ts"], outfile: "dist/extension.js", platform: "node", format: "cjs", external: ["vscode"], target: "node18" },
  {
    ...common,
    entryPoints: ["webview/board.ts", "webview/agent.ts", "webview/calendar.ts", "webview/permissions.ts", "webview/automations.ts", "webview/newagent.ts", "webview/budgets.ts"],
    outdir: "dist/webview", platform: "browser", format: "iife", target: "es2020",
  },
];

if (tests) {
  const files = readdirSync("test").filter((f) => f.endsWith(".test.ts")).map((f) => "test/" + f);
  builds.push({ ...common, entryPoints: files, outdir: "dist-test", platform: "node", format: "cjs", target: "node18", external: ["vscode"] });
}

if (watch) {
  for (const b of builds) (await esbuild.context(b)).watch();
} else {
  await Promise.all(builds.map((b) => esbuild.build(b)));
}
