namespace Jalium.UI;

/// <summary>
/// Defines a template for data display.
/// </summary>
public class DataTemplate
{
    private Func<FrameworkElement>? _visualTree;
    private bool _isSealed;
    private readonly List<TriggerBase> _triggers = new();

    /// <summary>
    /// Gets or sets the type of data for which this template is intended.
    /// </summary>
    public Type? DataType { get; set; }

    /// <summary>
    /// Gets the collection of triggers that activate based on the model state of items in this template.
    /// 与 <see cref="ControlTemplate.Triggers"/> 行为一致：每次 <see cref="LoadContent"/> 生成新 root
    /// 后，所有 trigger 会 Attach 到该 root；root 触发 <see cref="FrameworkElement.Unloaded"/> 时自动 detach，
    /// 同一 trigger 实例在同一 DataTemplate 多次实例化场景中被共享（trigger 内部按 element 维度索引状态，
    /// 多 root 各自独立运行）。
    /// </summary>
    public IList<TriggerBase> Triggers => _triggers;

    /// <summary>
    /// Gets a value indicating whether this template is read-only.
    /// </summary>
    public bool IsSealed => _isSealed;

    /// <summary>
    /// Gets or sets the raw XAML content for this template.
    /// This is set by the XAML parser and used by LoadContent() to create the visual tree.
    /// </summary>
    internal string? VisualTreeXaml { get; set; }

    /// <summary>
    /// Gets or sets the assembly context for parsing the XAML content.
    /// </summary>
    internal System.Reflection.Assembly? SourceAssembly { get; set; }

    /// <summary>
    /// 模板被 XAML 解析器扫描到时的祖先 ResourceDictionary 快照。
    /// LoadContent() 时通过 <see cref="TemplateAmbientResourceContext"/> 桥接给延迟解析器，
    /// 让模板内 <c>{StaticResource ...}</c> 能解析到外层 UserControl.Resources / Window.Resources 等声明的资源。
    /// </summary>
    internal IReadOnlyList<ResourceDictionary>? AmbientResourceDictionaries { get; set; }

    /// <summary>
    /// Gets or sets a callback used by LoadContent to parse XAML.
    /// This allows the Controls assembly to remain independent of the Xaml assembly.
    /// </summary>
    public static Func<string, System.Reflection.Assembly?, FrameworkElement?>? XamlParser { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DataTemplate"/> class.
    /// </summary>
    public DataTemplate()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DataTemplate"/> class with the specified data type.
    /// </summary>
    /// <param name="dataType">The type of data for which this template is intended.</param>
    public DataTemplate(Type dataType)
    {
        DataType = dataType;
    }

    /// <summary>
    /// Sets the visual tree factory for this template.
    /// </summary>
    /// <param name="visualTreeFactory">A function that creates the visual tree.</param>
    public void SetVisualTree(Func<FrameworkElement> visualTreeFactory)
    {
        if (_isSealed)
            throw new InvalidOperationException("Cannot modify a sealed DataTemplate.");

        _visualTree = visualTreeFactory;
    }

    /// <summary>
    /// Seals the template so that it can no longer be modified.
    /// </summary>
    public void Seal()
    {
        _isSealed = true;
    }

    /// <summary>
    /// Creates the visual tree defined by this template.
    /// </summary>
    /// <returns>The root element of the visual tree.</returns>
    public FrameworkElement? LoadContent()
    {
        FrameworkElement? root = null;

        // If we have a factory function, use it
        if (_visualTree != null)
        {
            root = _visualTree.Invoke();
        }
        else if (!string.IsNullOrEmpty(VisualTreeXaml) && XamlParser != null)
        {
            // 把模板被声明时的祖先 ResourceDictionary 链通过 ThreadStatic 桥
            // 透传给延迟 XAML 解析器，让模板内 {StaticResource X} 能解析到外层声明的资源。
            using (TemplateAmbientResourceContext.Push(AmbientResourceDictionaries))
            {
                root = XamlParser(VisualTreeXaml, SourceAssembly);
            }
        }

        if (root != null && _triggers.Count > 0)
        {
            AttachTriggersToRoot(root);
        }

        return root;
    }

    /// <summary>
    /// 为给定 root 挂上本 DataTemplate 上声明的所有 trigger。设计要点：
    ///   - <see cref="TriggerBase.ParentTemplateTriggers"/> 必须设为 <see cref="_triggers"/>，
    ///     这样 trigger 在写 setter 值时会走 <c>TemplateTrigger</c> layer（而不是 StyleTrigger
    ///     layer），与 ControlTemplate 行为一致；
    ///   - root 的 <see cref="FrameworkElement.Unloaded"/> 触发时统一 detach，避免 element 被
    ///     从树中移除后 trigger 仍然持有 PropertyChanged / Binding 订阅造成内存泄漏 + 持续
    ///     回调错误目标。
    ///
    /// 同一 DataTemplate 实例可能被 ItemsControl 复用产生 N 个 root。trigger 内部按 element
    /// 维度（_elementStates Dictionary）跟踪状态，多 root attach 到同一 trigger 是允许的；
    /// 这里不需要去重。
    /// </summary>
    private void AttachTriggersToRoot(FrameworkElement root)
    {
        foreach (var trigger in _triggers)
        {
            trigger.ParentTemplateTriggers = _triggers;
            trigger.Attach(root);
        }

        RoutedEventHandler? unloadedHandler = null;
        unloadedHandler = (_, _) =>
        {
            foreach (var trigger in _triggers)
            {
                trigger.Detach(root);
            }
            if (unloadedHandler != null)
            {
                root.Unloaded -= unloadedHandler;
            }
        };
        root.Unloaded += unloadedHandler;
    }

    /// <summary>
    /// Finds a named element in the template content applied to the specified parent.
    /// </summary>
    /// <param name="name">The name of the element to find.</param>
    /// <param name="templatedParent">The element to which this template was applied.</param>
    /// <returns>The named element, or null if not found.</returns>
    public object? FindName(string name, FrameworkElement templatedParent)
    {
        ArgumentNullException.ThrowIfNull(templatedParent);

        if (string.IsNullOrEmpty(name))
            return null;

        // Search the visual tree of the templated parent for a named element
        return FindNameInVisualTree(templatedParent, name);
    }

    private static object? FindNameInVisualTree(Visual root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name)
            return fe;

        for (int i = 0; i < root.VisualChildrenCount; i++)
        {
            var child = root.GetVisualChild(i);
            if (child != null)
            {
                var result = FindNameInVisualTree(child, name);
                if (result != null) return result;
            }
        }

        return null;
    }
}
