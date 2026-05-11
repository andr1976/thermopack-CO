using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ThermoPack.Core.Models;
using ThermoPack.Editor.ViewModels;
using ThermoPack.Editor.Views;

namespace ThermoPack.Editor;

public static class EditorLauncher
{
    private static Thread? _avaloniaThread;
    private static readonly object _lock = new();
    private static readonly ManualResetEventSlim _ready = new(false);

    /// <summary>
    /// Shows the component editor dialog and returns the selected components,
    /// or null if cancelled.
    /// </summary>
    public static IReadOnlyList<Component>? ShowComponentEditor(
        IReadOnlyList<Component> allComponents,
        IReadOnlyList<Component> currentSelection,
        EosType eosType)
    {
        EnsureAvaloniaThread();

        IReadOnlyList<Component>? result = null;
        var done = new ManualResetEventSlim(false);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var vm = new ComponentSelectorViewModel(allComponents, currentSelection, eosType);
                var window = new ComponentSelectorWindow
                {
                    DataContext = vm
                };

                window.Closed += (_, _) =>
                {
                    if (vm.DialogResult)
                    {
                        result = vm.SelectedComponents;
                    }
                    done.Set();
                };

                window.Show();
                window.Activate();
            }
            catch
            {
                done.Set();
            }
        });

        done.Wait();
        return result;
    }

    private static void EnsureAvaloniaThread()
    {
        if (_avaloniaThread?.IsAlive == true) return;
        lock (_lock)
        {
            if (_avaloniaThread?.IsAlive == true) return;
            _ready.Reset();
            _avaloniaThread = new Thread(AvaloniaThreadProc);
            _avaloniaThread.SetApartmentState(ApartmentState.STA);
            _avaloniaThread.IsBackground = true;
            _avaloniaThread.Name = "ThermoPack.Editor.Avalonia";
            _avaloniaThread.Start();
            _ready.Wait();
        }
    }

    private static void AvaloniaThreadProc()
    {
        try
        {
            AppBuilder.Configure<EditorApp>()
                .UsePlatformDetect()
                .AfterSetup(_ => _ready.Set())
                .StartWithClassicDesktopLifetime(
                    Array.Empty<string>(),
                    ShutdownMode.OnExplicitShutdown);
        }
        catch
        {
            _ready.Set();
        }
    }
}

public class EditorApp : Application
{
    public override void Initialize()
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Styles.Add(theme);
    }
}
