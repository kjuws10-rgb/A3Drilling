namespace Drilling.UI.Menu
{
    using System.Windows.Input;
    using System.ComponentModel;
    using System.Windows;
    using System.Windows.Controls.Primitives;
    using System.Windows.Media;

    public sealed class CButtonCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public static CButtonCommand NoOp { get; } = new(DoNothing);

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter)
        {
            return canExecute?.Invoke(parameter) ?? true;
        }

        public void Execute(object? parameter)
        {
            execute(parameter);
        }

        public void NotifyCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void DoNothing(object? parameter)
        {
        }
    }

    public static class CButtonCommandBehavior
    {
        private static readonly Dictionary<CButtonCommand, HashSet<UIElement>> RegisteredTargets = [];
        private static readonly DependencyPropertyDescriptor CommandParameterDescriptor = DependencyPropertyDescriptor.FromProperty(ButtonBase.CommandParameterProperty, typeof(ButtonBase));
        public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached("Command", typeof(CButtonCommand), typeof(CButtonCommandBehavior), new PropertyMetadata(null, OnCommandChanged));
        public static CButtonCommand? GetCommand(DependencyObject target)
        {
            return (CButtonCommand? )target.GetValue(CommandProperty);
        }

        public static void SetCommand(DependencyObject target, CButtonCommand? value)
        {
            target.SetValue(CommandProperty, value);
        }

        private static void OnCommandChanged(DependencyObject target, DependencyPropertyChangedEventArgs eventArgs)
        {
            if (target is not FrameworkElement element)
            {
                return;
            }

            element.Loaded -= Element_Loaded;
            element.Unloaded -= Element_Unloaded;
            DetachElement(element, eventArgs.OldValue as CButtonCommand);
            if (eventArgs.NewValue is CButtonCommand newCommand)
            {
                element.Loaded += Element_Loaded;
                element.Unloaded += Element_Unloaded;
                // A command can be assigned before the control joins the visual
                // tree (including Review screen rebuilds). Attach immediately;
                // AttachElement is idempotent and Loaded will only refresh it.
                AttachElement(element, newCommand);
                UpdateTargetState(element, newCommand);
            }
            else
            {
                element.SetCurrentValue(UIElement.IsEnabledProperty, true);
            }
        }

        private static void Element_Loaded(object sender, RoutedEventArgs eventArgs)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            var command = GetCommand(element);
            if (command is not null)
            {
                AttachElement(element, command);
                UpdateTargetState(element, command);
            }
        }

        private static void Element_Unloaded(object sender, RoutedEventArgs eventArgs)
        {
            if (sender is FrameworkElement element)
            {
                DetachElement(element, GetCommand(element));
            }
        }

        private static void AttachElement(UIElement element, CButtonCommand command)
        {
            RegisterTarget(command, element);
            if (element is ButtonBase button)
            {
                button.Click -= Button_Click;
                button.Click += Button_Click;
                CommandParameterDescriptor.RemoveValueChanged(button, ButtonCommandParameterChanged);
                CommandParameterDescriptor.AddValueChanged(button, ButtonCommandParameterChanged);
            }
            else
            {
                element.MouseLeftButtonDown -= Element_MouseLeftButtonDown;
                element.MouseLeftButtonDown += Element_MouseLeftButtonDown;
            }
        }

        private static void DetachElement(UIElement element, CButtonCommand? command)
        {
            if (element is ButtonBase button)
            {
                button.Click -= Button_Click;
                CommandParameterDescriptor.RemoveValueChanged(button, ButtonCommandParameterChanged);
            }
            else
            {
                element.MouseLeftButtonDown -= Element_MouseLeftButtonDown;
            }

            if (command is not null)
            {
                UnregisterTarget(command, element);
            }
        }

        private static void RegisterTarget(CButtonCommand command, UIElement element)
        {
            if (!RegisteredTargets.TryGetValue(command, out var targets))
            {
                targets = [];
                RegisteredTargets.Add(command, targets);
                command.CanExecuteChanged += Command_CanExecuteChanged;
            }

            targets.Add(element);
        }

        private static void UnregisterTarget(CButtonCommand command, UIElement element)
        {
            if (!RegisteredTargets.TryGetValue(command, out var targets))
            {
                return;
            }

            targets.Remove(element);
            if (targets.Count == 0)
            {
                command.CanExecuteChanged -= Command_CanExecuteChanged;
                RegisteredTargets.Remove(command);
            }
        }

        private static void Element_MouseLeftButtonDown(object sender, RoutedEventArgs eventArgs)
        {
            if (sender is not UIElement element)
            {
                return;
            }

            for (var source = eventArgs.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, element); source = VisualTreeHelper.GetParent(source))
            {
                if (source is ButtonBase)
                {
                    return;
                }
            }

            var command = GetCommand(element);
            if (command is not null && command.CanExecute(null))
            {
                command.Execute(null);
            }
        }

        private static void Button_Click(object sender, RoutedEventArgs eventArgs)
        {
            if (sender is not ButtonBase button)
            {
                return;
            }

            var command = GetCommand(button);
            var parameter = button.CommandParameter;
            if (command is not null && command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }

        private static void Command_CanExecuteChanged(object? sender, EventArgs eventArgs)
        {
            if (sender is not CButtonCommand command || !RegisteredTargets.TryGetValue(command, out var targets))
            {
                return;
            }

            foreach (var element in targets)
            {
                UpdateTargetState(element, command);
            }
        }

        private static void ButtonCommandParameterChanged(object? sender, EventArgs eventArgs)
        {
            if (sender is not ButtonBase button)
            {
                return;
            }

            var command = GetCommand(button);
            if (command is not null)
            {
                UpdateTargetState(button, command);
            }
        }

        private static void UpdateTargetState(UIElement element, CButtonCommand command)
        {
            var parameter = element is ButtonBase button ? button.CommandParameter : null;
            var canExecute = command.CanExecute(parameter);
            element.SetCurrentValue(UIElement.IsEnabledProperty, canExecute);
        }
    }
}
