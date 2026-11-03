using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.App;

/// <summary>
/// Wraps controls that only apply to OpenTS3 (DESIGN.md, "Project target"): under any other target they are disabled
/// and an "OpenTS3 only" note shows under them. The target is an inherited attached property, so the editor sets it
/// once on the window: <c>&lt;local:OpenTS3OnlyGate&gt;&lt;CheckBox … /&gt;&lt;/local:OpenTS3OnlyGate&gt;</c>.
/// </summary>
public sealed class OpenTS3OnlyGate : StackPanel
{
    public static readonly AttachedProperty<ProjectTarget> TargetProperty =
        AvaloniaProperty.RegisterAttached<OpenTS3OnlyGate, Control, ProjectTarget>("Target", ProjectTarget.Retail, inherits: true);

    public static ProjectTarget GetTarget(Control control) => control.GetValue(TargetProperty);
    public static void SetTarget(Control control, ProjectTarget value) => control.SetValue(TargetProperty, value);

    readonly TextBlock _note = new() { Text = "OpenTS3 only", FontSize = 11, Foreground = (IBrush?)Application.Current?.FindResource("Muted") };

    public OpenTS3OnlyGate() => Spacing = 2;

    /// <summary>True when the controls are usable under the current target.</summary>
    public bool Applies => GetTarget(this) == ProjectTarget.OpenTS3Only;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty) Update();
    }

    void Update()
    {
        foreach (var child in Children)
            if (child != _note) child.IsEnabled = Applies;
        if (Applies) Children.Remove(_note);
        else if (!Children.Contains(_note)) Children.Add(_note);
    }
}
