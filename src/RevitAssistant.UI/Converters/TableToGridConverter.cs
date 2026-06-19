using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitAssistant.UI;

/// <summary>
/// Builds a bordered <see cref="Grid"/> from a <see cref="ResultTable"/> so list
/// results render as a real table (dynamic columns). Cells are read-only; the
/// "Copy bảng" button copies the data as TSV.
/// </summary>
public sealed class TableToGridConverter : IValueConverter
{
    private static readonly Brush GridLine = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
    private static readonly Brush HeaderBg = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ResultTable t || t.Columns.Count == 0) return null;

        GridLine.Freeze();
        HeaderBg.Freeze();
        var textBrush = (Brush)(Application.Current?.TryFindResource(SystemColors.ControlTextBrushKey)
                                 ?? SystemColors.ControlTextBrush);

        var grid = new Grid();
        for (var c = 0; c < t.Columns.Count; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r <= t.Rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        for (var c = 0; c < t.Columns.Count; c++)
            grid.Children.Add(Cell(t.Columns[c], 0, c, header: true, textBrush));

        // Rows
        for (var r = 0; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var text = c < row.Count ? row[c] : "";
                grid.Children.Add(Cell(text, r + 1, c, header: false, textBrush));
            }
        }

        return new Border
        {
            BorderBrush = GridLine,
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(4),
            Child = grid,
            SnapsToDevicePixels = true,
        };
    }

    private static Border Cell(string text, int row, int col, bool header, Brush textBrush)
    {
        var tb = new TextBlock
        {
            Text = text,
            Padding = new Thickness(7, 3, 7, 3),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = textBrush,
            FontSize = 11,
        };
        var border = new Border
        {
            BorderBrush = GridLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = header ? HeaderBg : Brushes.Transparent,
            Child = tb,
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        return border;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
