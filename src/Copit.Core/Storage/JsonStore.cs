using System.Text.Json;
using System.Text.Json.Serialization;

namespace Copit.Core.Storage;

public static class AppPaths
{
    private static string? _root;

    /// <summary>Basisordner für alle Daten (%LOCALAPPDATA%\Copit). Für Tests überschreibbar.</summary>
    public static string Root
    {
        get
        {
            _root ??= Environment.GetEnvironmentVariable("COPIT_HOME")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Copit");
            Directory.CreateDirectory(_root);
            return _root;
        }
        set => _root = value;
    }

    public static string File(string name) => Path.Combine(Root, name);

    public static string Dir(string name)
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        return d;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

/// <summary>Speichert ein Objekt als JSON-Datei. Thread-safe, atomisches Schreiben.</summary>
public class JsonStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private T? _value;

    public JsonStore(string path) => _path = path;

    public T Value => _value ??= Load();

    private T Load()
    {
        try
        {
            if (System.IO.File.Exists(_path))
                return JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(_path), Json.Options) ?? new T();
        }
        catch
        {
            // Defekte Datei sichern statt Daten zu verlieren.
            try { System.IO.File.Copy(_path, _path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
        return new T();
    }

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var tmp = _path + ".tmp";
            await System.IO.File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(Value, Json.Options));
            System.IO.File.Move(tmp, _path, true);
        }
        finally { _lock.Release(); }
    }

    public void Save()
    {
        _lock.Wait();
        try
        {
            var tmp = _path + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(Value, Json.Options));
            System.IO.File.Move(tmp, _path, true);
        }
        finally { _lock.Release(); }
    }
}
