using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeTracker.App;

/// <summary>
/// Keeps the elements of a view from one render to the next, by name. A view here is drawn
/// again from the view model at every change, which is every few seconds. Made of new
/// elements each time, it takes the keyboard focus from whatever had it, sends a screen
/// reader back to the top of the window, and scrolls a list back to its start. Asked for by
/// the same name, an element is the same element, and a render only changes what it says.
/// </summary>
internal sealed class ViewCache
{
    private Dictionary<string, FrameworkElement> kept = [];
    private Dictionary<string, FrameworkElement> asked = [];

    /// <summary>
    /// The element of this name: the one the last render used, or a new one. Whatever the
    /// caller sets on it, it must set every time — the element remembers its last render.
    /// </summary>
    public T Keep<T>(string name) where T : FrameworkElement, new()
    {
        if (asked.TryGetValue(name, out var already) && already is T again) return again;
        var element = kept.TryGetValue(name, out var before) && before is T same ? same : new T();
        asked[name] = element;
        return element;
    }

    /// <summary>Ends a render: what it did not ask for is forgotten.</summary>
    public void Sweep()
    {
        kept = asked;
        asked = [];
    }

    /// <summary>Gives a panel these children in this order, touching nothing when it has them already.</summary>
    public static void SetChildren(Panel panel, IReadOnlyList<UIElement> children)
    {
        var same = panel.Children.Count == children.Count;
        for (var i = 0; same && i < children.Count; i++) same = ReferenceEquals(panel.Children[i], children[i]);
        if (same) return;

        panel.Children.Clear();
        foreach (var child in children)
        {
            // A kept element may still sit where the last render put it, and has one parent.
            if (VisualTreeHelper.GetParent(child) is Panel other) other.Children.Remove(child);
            panel.Children.Add(child);
        }
    }
}
