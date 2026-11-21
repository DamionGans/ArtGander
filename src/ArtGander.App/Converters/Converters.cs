using System.Globalization;
using System.Text.RegularExpressions;

namespace ArtGander.App.Converters;

// Two-way converters between text fields and typed model values. An invalid entry keeps the previous value
// (BindableProperty.UnsetValue) instead of crashing or clearing the field.

public sealed class NullableDecimalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal d ? d.ToString("0.##", culture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return TryParse(s, culture, out var d) ? d : BindableProperty.UnsetValue;
    }

    internal static bool TryParse(string s, CultureInfo culture, out decimal d) =>
        decimal.TryParse(s, NumberStyles.Number, culture, out d) || decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d);
}

public sealed class DecimalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal d ? d.ToString("0.##", culture) : "0";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return 0m;
        return NullableDecimalConverter.TryParse(s, culture, out var d) ? d : BindableProperty.UnsetValue;
    }
}

public sealed class NullableIntConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i.ToString(culture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return int.TryParse(s, NumberStyles.Integer, culture, out var i) ? i : BindableProperty.UnsetValue;
    }
}

public sealed class IntConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i.ToString(culture) : "0";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        int.TryParse((value as string)?.Trim(), NumberStyles.Integer, culture, out var i) ? i : BindableProperty.UnsetValue;
}

public sealed class DateOnlyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : DateTime.Today;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime dt ? DateOnly.FromDateTime(dt) : BindableProperty.UnsetValue;
}

/// <summary>For DatePicker.Date, which is a nullable DateTime in MAUI 10 and later.</summary>
public sealed class NullableDateOnlyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime dt ? DateOnly.FromDateTime(dt) : null;
}

/// <summary>Turns enum names into readable text, e.g. "PrintOnDemand" becomes "Print on demand".</summary>
public sealed partial class HumanizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Humanize(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public static string Humanize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var spaced = Words().Replace(s, " $1").Trim();
        return char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex Words();
}

public sealed class MoneyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        decimal d => $"{d.ToString("N2", culture)} {parameter as string ?? ""}".Trim(),
        null => "–",
        _ => value.ToString(),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotNullOrEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => false,
        string s => s.Length > 0,
        int i => i > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>"Select" or "Done" for the library's selection-mode button.</summary>
public sealed class SelectLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "Done" : "Select";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A checked or unchecked box character for selection overlays.</summary>
public sealed class CheckMarkConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "☑" : "☐";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>An arrow for the sort direction: true = descending.</summary>
public sealed class SortArrowConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "▼" : "▲";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// For pickers bound to text: a picker reports "nothing selected" when the value isn't in its list, which would wipe
/// the value (and make the page look changed). This keeps the value instead.
/// </summary>
public sealed class KeepValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value ?? Binding.DoNothing;
}

/// <summary>Who ships an order item, in plain words.</summary>
public sealed class FulfillmentNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ArtGander.Core.Model.Fulfillment.Printify => "Printify",
        ArtGander.Core.Model.Fulfillment.Self => "You (self-shipped)",
        ArtGander.Core.Model.Fulfillment.Digital => "Digital download",
        _ => value?.ToString(),
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
