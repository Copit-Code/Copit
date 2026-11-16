using System.Diagnostics;

namespace Copit.Core.Tests;

/// <summary>Führt die PowerShell-Skripte der Obsidian-Extension aus (nur wenn pwsh installiert ist, z. B. in der CI).</summary>
public class ObsidianExtensionScriptTests
{
    private static string? Pwsh()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                     .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools")))
            foreach (var name in new[] { "pwsh", "pwsh.exe" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    private static string Scripts()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "extensions"))) d = d.Parent;
        return Path.Combine(d!.FullName, "extensions", "copit-obsidian", "scripts");
    }

    private static string Run(string pwsh, string script, string cwd, Dictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo(pwsh, ["-NoProfile", "-NonInteractive", "-File", Path.Combine(Scripts(), script), .. args])
        { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{script}: {e}\n{o}");
        return o;
    }

    [Fact]
    public void ArchitectureAndAgentNoteAreLinked()
    {
        var pwsh = Pwsh();
        if (pwsh == null) return;
        var root = Directory.CreateTempSubdirectory("copit-obs").FullName;
        var repo = Path.Combine(root, "shop"); var vault = Path.Combine(root, "vault"); var home = Path.Combine(root, "home");
        Directory.CreateDirectory(Path.Combine(repo, "api")); Directory.CreateDirectory(Path.Combine(repo, "web"));
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian")); Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(repo, "api", "Shop.Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        File.WriteAllText(Path.Combine(repo, "api", "Orders.cs"), """
            [Route("api/[controller]")]
            public class OrdersController(IOrderService s) { [HttpGet("{id}")] public object Get(int id) => s; }
            public interface IOrderService { }
            public class OrderService(JiraClient j) : IOrderService { }
            public class JiraClient(HttpClient h) { public Task<HttpResponseMessage> Me() => h.GetAsync("rest/api/2/myself"); }
            """);
        File.WriteAllText(Path.Combine(repo, "web", "package.json"), """{ "name": "shop-web" }""");
        File.WriteAllText(Path.Combine(repo, "web", "orders.ts"), "export const load = (id: number) => fetch(`/api/orders/${id}`);");
        var env = new Dictionary<string, string> { ["COPIT_HOME"] = home, ["COPIT_PARAM_VAULT"] = vault, ["COPIT_REPO"] = repo };

        var outp = Run(pwsh, "architecture.ps1", repo, env);
        Assert.Contains("2 Apps", outp);
        var arch = Path.Combine(vault, "Copit", "Architektur", "shop");
        var controller = File.ReadAllText(Path.Combine(arch, "Services", "OrdersController.md"));
        Assert.Contains("[[Copit/Architektur/shop/Services/OrderService|OrderService]]", controller);   // Interface → Implementierung
        var call = Directory.GetFiles(Path.Combine(arch, "API-Aufrufe"), "*.md", SearchOption.AllDirectories).Single(f => f.Contains("orders"));
        Assert.Contains("Endpunkte/Shop.Api/GET", File.ReadAllText(call));                               // Web → interner Endpunkt
        Assert.Contains("[[Copit/Architektur/shop/Services/JiraClient|JiraClient]]", File.ReadAllText(Path.Combine(vault, "Copit", "Systeme", "Jira.md")));
        Assert.True(File.Exists(Path.Combine(arch, "shop – Architektur.canvas")));

        env["COPIT_VAR_AGENTID"] = "a1"; env["COPIT_VAR_TITLE"] = "Orders | Fix"; env["COPIT_VAR_TICKET"] = "shop-7"; env["COPIT_VAR_SUMMARY"] = "Erledigt";
        Run(pwsh, "agent-note.ps1", repo, env);
        Run(pwsh, "agent-note.ps1", repo, env); // zweiter Lauf aktualisiert dieselbe Notiz
        var agents = Directory.GetFiles(Path.Combine(vault, "Copit", "Agents"), "*.md", SearchOption.AllDirectories);
        Assert.Single(agents);
        Assert.Contains("ticket: \"[[Copit/Tickets/SHOP-7|SHOP-7]]\"", File.ReadAllText(agents[0]));
        Assert.Contains("Orders / Fix", File.ReadAllText(Path.Combine(vault, "Copit", "Tickets", "SHOP-7.md")));

        var ctx = Run(pwsh, "context.ps1", repo, env, "-Ticket", "SHOP-7");
        Assert.Contains("Erledigt", ctx);
        Assert.Contains("JiraClient", ctx);
    }
}
