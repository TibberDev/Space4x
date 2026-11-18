using Avalonia.Data.Converters;

namespace Pulsar4X.UI.Desktop.Converters;

/// <summary>Small value converters shared by the views.</summary>
public static class Converters
{
    /// <summary>Greys out an option that cannot be chosen (true → fully opaque, false → faded).</summary>
    public static readonly IValueConverter AvailableOpacity =
        new FuncValueConverter<bool, double>(available => available ? 1.0 : 0.45);
}
