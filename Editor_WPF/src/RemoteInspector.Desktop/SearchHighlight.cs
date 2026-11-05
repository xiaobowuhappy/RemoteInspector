using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RemoteInspector.Desktop;

// A shared matcher and text renderer keep filtering and highlighted characters consistent.
public static class SearchHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(SearchHighlight), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached("Query", typeof(string), typeof(SearchHighlight), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits, Changed));
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetQuery(DependencyObject o, string value) => o.SetValue(QueryProperty, value);
    public static string GetQuery(DependencyObject o) => (string)o.GetValue(QueryProperty);
    public static bool Matches(string text, string query) => Positions(text ?? "", query).matched;
    public static bool TryMatchSpan(string text, string query, out int start, out int length)
    {
        var result = Positions(text ?? "", query);
        start = result.positions.Count == 0 ? 0 : result.positions.Min();
        length = result.positions.Count == 0 ? 0 : result.positions.Max() - start + 1;
        return result.matched && length > 0;
    }
    private static (bool matched, HashSet<int> positions) Positions(string text, string query)
    {
        var positions = new HashSet<int>();
        foreach (string word in (query ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            int exact = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (exact >= 0) { for (int i = 0; i < word.Length; i++) positions.Add(exact + i); continue; }
            int next = 0;
            foreach (char ch in word)
            {
                int found = text.IndexOf(ch.ToString(), next, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return (false, new());
                positions.Add(found); next = found + 1;
            }
        }
        return (true, positions);
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, object> managed = new();
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock block) return;
        if (e.Property == TextProperty)
        {
            if (!managed.TryGetValue(block, out _))
            {
                managed.Add(block, new object());
                block.Loaded += (_, _) => Render(block);
            }
        }
        else if (!managed.TryGetValue(block, out _)) return;
        block.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() => Render(block)));
    }
    private static void Render(TextBlock block)
    {
        string text = GetText(block) ?? ""; var result = Positions(text, GetQuery(block));
        block.Inlines.Clear();
        if (!result.matched || result.positions.Count == 0) { block.Inlines.Add(new Run(text)); return; }
        for (int start = 0; start < text.Length;)
        {
            bool hit = result.positions.Contains(start); int end = start + 1;
            while (end < text.Length && result.positions.Contains(end) == hit) end++;
            var run = new Run(text[start..end]);
            if (hit) { run.Background = Brushes.Gold; run.Foreground = Brushes.Black; run.FontWeight = FontWeights.SemiBold; }
            block.Inlines.Add(run); start = end;
        }
    }
}
