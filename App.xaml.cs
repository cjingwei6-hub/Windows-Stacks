using System;
using System.IO;
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

        // ── Self-test mode: skip mutex + show SettingsWindow directly so we can
        //    catch XAML parse errors without dealing with tray menu interaction.
        bool selftest = false;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            {
                selftest = true;
                break;
            }
        }

        if (selftest)
        {
            // Write every crash to a log so we can inspect without a UI
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                WriteCrash("AppDomain", args.ExceptionObject as Exception);
            DispatcherUnhandledException += (s, args) =>
            {
                WriteCrash("Dispatcher", args.Exception);
                MessageBox.Show(
                    "Stacks selftest 崩溃:\n" + args.Exception.Message +
                    "\n\n详细信息: %LOCALAPPDATA%\\Stacks\\selftest-crash.log",
                    "Stacks 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
                Shutdown(1);
            };

            try
            {
                var w = new SettingsWindow();
                w.Show();
            }
            catch (Exception ex)
            {
                WriteCrash("SettingsWindow ctor", ex);
                Shutdown(1);
            }
            return;
        }

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

    private static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Stacks");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "selftest-crash.log"),
                $"[{DateTime.Now:O}] {source}\n{ex}\n\n");
        }
        catch { }
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
