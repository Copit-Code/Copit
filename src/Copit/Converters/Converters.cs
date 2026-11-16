using System.Collections;
using System.Globalization;

namespace Copit.Converters;

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is not true;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is not true;
}

public class HasTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => !string.IsNullOrWhiteSpace(v?.ToString());
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class NoTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => string.IsNullOrWhiteSpace(v?.ToString());
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class NotNullConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v != null;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class IsNullConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v == null;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class CountToBoolConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var any = v switch
        {
            int i => i > 0,
            ICollection col => col.Count > 0,
            IEnumerable e => e.GetEnumerator().MoveNext(),
            _ => false
        };
        return Invert ? !any : any;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class EqualsConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => string.Equals(v?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class HexColorConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        try { return Color.FromArgb(v?.ToString() ?? "#8B5CF6"); } catch { return Color.FromArgb("#8B5CF6"); }
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Farben für Pipeline-/Job-/Ticket-Status.</summary>
public static class StatusPalette
{
    public static string Key(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "success" or "passed" or "done" or "merged" or "approve" => "Green",
        "failed" or "error" or "canceled" or "request_changes" => "Red",
        "running" or "pending" or "preparing" or "waiting_for_resource" or "inprogress" or "comment" => "Cyan",
        "manual" or "review" or "warning" or "skipped" => "Amber",
        "created" or "scheduled" or "open" => "Accent",
        _ => "Muted"
    };

    public static Color Get(string key) =>
        Application.Current!.Resources.TryGetValue(key, out var v) && v is Color c ? c : Colors.Gray;
}

public class StatusColorConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => StatusPalette.Get(StatusPalette.Key(v?.ToString()));
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class StatusSoftColorConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var k = StatusPalette.Key(v?.ToString());
        return StatusPalette.Get(k == "Muted" ? "Card" : k + "Soft");
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class StatusIconConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => Copit.Controls.IconSet.ForStatus(v?.ToString());
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class SeverityColorConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => StatusPalette.Get((v?.ToString() ?? "").ToLowerInvariant() switch
    {
        "critical" => "Red",
        "major" => "Pink",
        "minor" => "Amber",
        _ => "Cyan"
    });
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
