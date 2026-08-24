using Jalium.UI.Markup;

namespace Jalium.UI;

/// <summary>Defines a template for data display.</summary>
[DictionaryKeyProperty(nameof(DataTemplateKey))]
public class DataTemplate : FrameworkTemplate
{
    private readonly TriggerCollection _triggers = new();
    private object? _dataType;

    /// <summary>Gets or sets the type of data for which this template is intended.</summary>
    [Ambient]
    public object? DataType
    {
        get => _dataType;
        set
        {
            TemplateKey.ValidateDataType(value, nameof(value));
            CheckSealed();
            _dataType = value;
        }
    }

    /// <summary>Gets the implicit resource key for this template.</summary>
    public object? DataTemplateKey =>
        _dataType is not null ? new DataTemplateKey(_dataType) : null;

    /// <summary>Gets the triggers applied to each generated template root.</summary>
    public TriggerCollection Triggers => _triggers;

    /// <summary>Gets or sets the deferred-XAML parser supplied by Jalium.UI.Xaml.</summary>
    public static Func<string, System.Reflection.Assembly?, FrameworkElement?>? XamlParser { get; set; }

    public DataTemplate()
    {
    }

    public DataTemplate(object dataType)
    {
        TemplateKey.ValidateDataType(dataType, nameof(dataType));
        _dataType = dataType;
    }

    /// <summary>Retained for source and binary compatibility.</summary>
    public DataTemplate(Type dataType)
        : this((object)dataType)
    {
    }

    /// <summary>Sets Jalium's factory representation of the visual tree.</summary>
    public void SetVisualTree(Func<FrameworkElement> visualTreeFactory)
        => SetVisualTreeFactory(visualTreeFactory);

    /// <summary>Creates a new framework-element instance of the data template.</summary>
    public new FrameworkElement? LoadContent()
        => base.LoadContent() as FrameworkElement;

    protected override Func<string, System.Reflection.Assembly?, FrameworkElement?>? DeferredXamlParser
        => XamlParser;

    protected override void OnContentLoaded(DependencyObject? content)
    {
        if (content is FrameworkElement root && _triggers.Count > 0)
        {
            AttachTriggersToRoot(root);
        }
    }

    /// <summary>Data templates can only be applied through a ContentPresenter.</summary>
    protected override void ValidateTemplatedParent(FrameworkElement templatedParent)
    {
        ValidateTemplatedParentType(
            templatedParent,
            "Jalium.UI.Controls.ContentPresenter",
            "ContentPresenter");
    }

    private void AttachTriggersToRoot(FrameworkElement root)
    {
        foreach (var trigger in _triggers)
        {
            // hostIsTemplatedParent: false —— DataTemplate 的 trigger 挂在模板内容的根元素上，
            // 整棵内容树都是模板生成物，因此无 TargetName 的 setter 也按 ParentTemplateTrigger
            // （模板对自己生成物的支配）计优先级，而不是 TemplateTrigger。
            trigger.DeclareTemplateOwner(_triggers, hostIsTemplatedParent: false);
            trigger.Attach(root);
        }

        RoutedEventHandler? unloadedHandler = null;
        unloadedHandler = (_, _) =>
        {
            foreach (var trigger in _triggers)
            {
                trigger.Detach(root);
            }

            if (unloadedHandler is not null)
            {
                root.Unloaded -= unloadedHandler;
            }
        };
        root.Unloaded += unloadedHandler;
    }
}
