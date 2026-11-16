using System.Text;
using System.Text.Json;
using Copit.Core.Models;
using Copit.Core.Storage;

namespace Copit.Core.Services;

/// <summary>Verschlüsselt Secrets (in der App: Windows DPAPI).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class PlainProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}

public static class SecretKeys
{
    public const string JiraToken = "jira.token";
    public const string GitLabToken = "gitlab.token";
}

public class SettingsService
{
    private readonly JsonStore<AppSettings> _store = new(AppPaths.File("settings.json"));
    private readonly ISecretProtector _protector;
    private readonly string _secretsPath = AppPaths.File("secrets.bin");
    private Dictionary<string, string>? _secrets;

    public event Action? Changed;

    public SettingsService(ISecretProtector protector) => _protector = protector;

    public AppSettings Settings => _store.Value;

    public async Task SaveAsync()
    {
        await _store.SaveAsync();
        Changed?.Invoke();
    }

    public string GetSecret(string key) => Secrets.TryGetValue(key, out var v) ? v : "";

    public void SetSecret(string key, string value)
    {
        Secrets[key] = value;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Secrets));
        File.WriteAllBytes(_secretsPath, _protector.Protect(bytes));
    }

    private Dictionary<string, string> Secrets
    {
        get
        {
            if (_secrets != null) return _secrets;
            try
            {
                if (File.Exists(_secretsPath))
                {
                    var raw = _protector.Unprotect(File.ReadAllBytes(_secretsPath));
                    _secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(raw));
                }
            }
            catch { }
            return _secrets ??= [];
        }
    }

    public bool JiraConfigured => !string.IsNullOrWhiteSpace(Settings.Jira.BaseUrl) && GetSecret(SecretKeys.JiraToken).Length > 0;
    public bool GitLabConfigured => !string.IsNullOrWhiteSpace(Settings.GitLab.BaseUrl) && GetSecret(SecretKeys.GitLabToken).Length > 0;
}
