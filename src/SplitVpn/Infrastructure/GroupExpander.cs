using System.Windows;
using System.Windows.Controls;

namespace SplitVpn.Infrastructure;

/// <summary>Хранилище состояния свёрнутости групп. Реализуется ViewModel'ю.</summary>
public interface IGroupExpansionStore
{
    bool IsExpanded(string key);
    void SetExpanded(string key, bool expanded);
}

/// <summary>
/// Связывает <see cref="Expander"/> в шаблоне GroupItem с хранилищем состояния.
/// </summary>
/// <remarks>
/// Обычной привязкой это не выражается: ключ группы известен только внутри шаблона,
/// а индексатор с параметром-привязкой XAML не поддерживает. Хранилище передаётся
/// свойством, а не статикой, чтобы состояние не утекало между окнами и тестами.
/// </remarks>
public static class GroupExpander
{
    public static readonly DependencyProperty StoreProperty =
        DependencyProperty.RegisterAttached(
            "Store", typeof(IGroupExpansionStore), typeof(GroupExpander),
            new PropertyMetadata(null, OnAttachedChanged));

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(GroupExpander),
            new PropertyMetadata(null, OnAttachedChanged));

    public static IGroupExpansionStore? GetStore(DependencyObject o) =>
        (IGroupExpansionStore?)o.GetValue(StoreProperty);

    public static void SetStore(DependencyObject o, IGroupExpansionStore? value) =>
        o.SetValue(StoreProperty, value);

    public static string? GetKey(DependencyObject o) => (string?)o.GetValue(KeyProperty);

    public static void SetKey(DependencyObject o, string? value) => o.SetValue(KeyProperty, value);

    private static void OnAttachedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Expander expander) return;

        var store = GetStore(expander);
        var key = GetKey(expander);

        expander.Expanded -= OnToggled;
        expander.Collapsed -= OnToggled;

        if (store is null || string.IsNullOrEmpty(key)) return;

        expander.IsExpanded = store.IsExpanded(key);

        expander.Expanded += OnToggled;
        expander.Collapsed += OnToggled;
    }

    private static void OnToggled(object sender, RoutedEventArgs e)
    {
        // Expanded/Collapsed всплывают: без этой проверки вложенный Expander писал бы чужой ключ.
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is not Expander expander) return;

        var store = GetStore(expander);
        var key = GetKey(expander);

        if (store is null || string.IsNullOrEmpty(key)) return;

        store.SetExpanded(key, expander.IsExpanded);
    }
}
