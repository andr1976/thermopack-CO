using Avalonia.Controls;
using Avalonia.Interactivity;
using ThermoPack.Editor.ViewModels;

namespace ThermoPack.Editor.Views;

public partial class ComponentSelectorWindow : Window
{
    public ComponentSelectorWindow()
    {
        InitializeComponent();
    }

    private ComponentSelectorViewModel? Vm => DataContext as ComponentSelectorViewModel;

    private void OnAdd(object? sender, RoutedEventArgs e) => Vm?.AddComponent();
    private void OnRemove(object? sender, RoutedEventArgs e) => Vm?.RemoveComponent();
    private void OnAddDouble(object? sender, RoutedEventArgs e) => Vm?.AddComponent();
    private void OnRemoveDouble(object? sender, RoutedEventArgs e) => Vm?.RemoveComponent();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Vm?.Confirm();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
