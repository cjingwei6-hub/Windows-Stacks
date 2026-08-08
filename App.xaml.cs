using System;
using System.Threading;
using System.Windows;

namespace Stacks;

public partial class App : Application
{
    private static readonly string MutexName = "Global\\Stacks_DesktopOverlay_Singleton_2026";
    private static Mutex? _singletonMutex;
    private static bool _mutexOwned;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Singleton: only one Stacks instance allowed ──
        _singletonMutex = new Mutex(false, MutexName, out bool createdNew);
        _mutexOwned = createdNew;

        if (!createdNew)
        {
            MessageBox.Show("Stacks 已在运行。\n请检查系统托盘（右下角）。",
                "Stacks", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Check for debug flag
        bool isDebug = false;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--debug", StringComparison.OrdinalIgnoreCase))
            {
                isDebug = true;
                break;
            }
        }

        // Global exception handler — restore desktop icons on crash
        DispatcherUnhandledException += (s, args) =>
        {
            try { Interop.NativeMethods.SetDesktopIconsVisible(true); } catch { }
            MessageBox.Show($"Stacks 遇到错误:\n{args.Exception.Message}\n\n桌面图标已恢复。",
                "Stacks 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown();
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try { Interop.NativeMethods.SetDesktopIconsVisible(true); } catch { }
        };

        var window = new MainWindow(isDebug);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutexOwned && _singletonMutex != null)
        {
            try { _singletonMutex.ReleaseMutex(); } catch { }
            _singletonMutex.Dispose();
        }
        base.OnExit(e);
    }
}
