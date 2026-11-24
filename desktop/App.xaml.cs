using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PcPulse.Desktop;

public partial class App : Application
{
    private Mutex? singleton;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private Icon? statusIcon;
    private Process? windowProcess;
    private Process? agentProcess;
    private MetricsSampler? sampler;
    private ThermalProtection? protection;
    private CloudRelay? relay;
    private BluetoothBridge? bluetooth;
    private PairingIdentity? identity;
    private WebApplication? server;
    private Task<WebApplication>? pendingServer;
    private Task? desktopListener;
    private readonly CancellationTokenSource startup = new();
    private readonly PhonePresence phones = new();
    private DispatcherTimer? presenceTimer;
    private string? lastRoute;
    private bool trayLight;
    private volatile string serverStatus = "Запуск моніторингу…";
    private int port = 8787;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        var portOption = Array.IndexOf(args.Args, "--port");
        if (portOption >= 0 && portOption + 1 < args.Args.Length &&
            int.TryParse(args.Args[portOption + 1], out var requested) && requested is >= 1 and <= 65535)
            port = requested;

        var ownerOption = Array.IndexOf(args.Args, "--window-owner");
        if (ownerOption >= 0 && ownerOption + 1 < args.Args.Length && int.TryParse(args.Args[ownerOption + 1], out var parent))
        {
            OpenWindowProcess(parent);
            return;
        }

        var instanceSuffix = port == 8787 ? "" : $".{port}";
        var instanceEvent = @"Local\PcPulse.Desktop.Show" + instanceSuffix;
        singleton = new Mutex(true, @"Local\PcPulse.Desktop.Instance" + instanceSuffix, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(instanceEvent, out var existing))
            {
                using (existing) existing.Set();
            }
            Shutdown();
            return;
        }

        sampler = new MetricsSampler(true);
        protection = new ThermalProtection(sampler);
        desktopListener = Task.Run(() => new DesktopBridge(port).ListenAsync(ExecuteDesktopCommand, startup.Token));
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceEvent);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            Dispatcher.BeginInvoke(ShowWindow);
        }, null, Timeout.Infinite, false);

        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Відкрити PC Pulse", null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
        trayMenu.Items.Add("Вихід", null, (_, _) => Dispatcher.BeginInvoke(ExitApp));
        tray = new Forms.NotifyIcon { Text = "PC Pulse", ContextMenuStrip = trayMenu };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
        presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        presenceTimer.Tick += (_, _) => UpdatePhonePresence();
        trayLight = TrayGlyph.HasLightBackground();
        SystemEvents.UserPreferenceChanged += OnTrayThemeChanged;
        UpdatePhonePresence(true);
        tray.Visible = true;
        presenceTimer.Start();
        if (!args.Args.Contains("--tray"))
            ShowWindow();

        try
        {
            identity = PairingIdentity.Load();
            pendingServer = MonitorServer.StartAsync(IPAddress.Any, port, identity, sampler, protection, startup.Token, phones);
            server = await pendingServer;
            if (exiting)
                return;
            relay = new CloudRelay(identity, port);
            bluetooth = new BluetoothBridge(relay);
            serverStatus = $"Готово до підключення · порт {port}";
        }
        catch (Exception)
        {
            if (!exiting)
                serverStatus = $"Порт {port} недоступний. Закрийте попередній агент або задайте інший порт.";
        }
    }

    private string WindowEvent => $@"Local\PcPulse.Window.{port}";

    private void OpenWindowProcess(int owner)
    {
        try { agentProcess = Process.GetProcessById(owner); }
        catch (ArgumentException) { Shutdown(); return; }
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow(new DesktopBridge(port));
        MainWindow = window;
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WindowEvent);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            window.WindowState = WindowState.Normal;
            window.Activate();
        }), null, Timeout.Infinite, false);
        agentProcess.Exited += (_, _) => Dispatcher.BeginInvoke(window.Close);
        agentProcess.EnableRaisingEvents = true;
        if (agentProcess.HasExited)
            Shutdown();
        else
            window.Show();
    }

    private void ShowWindow()
    {
        if (exiting)
            return;
        if (windowProcess is { HasExited: false })
        {
            if (EventWaitHandle.TryOpenExisting(WindowEvent, out var opened))
            {
                using (opened) opened.Set();
            }
            return;
        }
        windowProcess?.Dispose();
        windowProcess = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
            $"--port {port} --window-owner {Environment.ProcessId}") { UseShellExecute = false });
    }

    private DesktopStatus ReadDesktopStatus() => new(serverStatus, phones.ActiveRoute(), protection!.GetStatus(), relay?.ReadStatus(),
        bluetooth?.ReadStatus(), server is not null, protection.GetKeyboardStatus());

    private async Task<object?> ExecuteDesktopCommand(DesktopCommand command, CancellationToken token)
    {
        switch (command.Name)
        {
            case "overview": return new DesktopOverview(sampler!.ReadOverview(true), ReadDesktopStatus());
            case "processes": return new DesktopProcesses(await sampler!.ReadProcessesAsync(token), ReadDesktopStatus());
            case "terminate": return await sampler!.TerminateProcessAsync(command.Pid, command.Started, token);
            case "status": return ReadDesktopStatus();
            case "fans": return protection!.SetManualMode(command.Enabled);
            case "thermal": return command.Protection is { } settings ? protection!.Configure(settings) : null;
            case "keyboard": return command.Keyboard is { } thresholds ? protection!.ConfigureKeyboard(thresholds) : null;
            case "relay":
                if (relay is null)
                    throw new ArgumentException("Агент ще не готовий до підключення.");
                relay.Configure(command.Enabled, command.Address ?? "", command.Enrollment ?? "");
                return ReadDesktopStatus();
            case "bluetooth":
                if (bluetooth is null)
                    throw new ArgumentException("Агент ще не готовий до підключення.");
                await bluetooth.ConfigureAsync(command.Enabled);
                return ReadDesktopStatus();
            case "pair":
            case "pair-bluetooth":
                if (server is null || identity is null)
                    return null;
                var addresses = MonitorServer.PrivateAddresses();
                var direct = addresses.Count > 0 ? identity.CreateCode(addresses, port) : null;
                var cloud = relay?.PhoneCode(direct);
                return command.Name == "pair-bluetooth" ? bluetooth?.Pair(cloud ?? direct) : command.Enabled ? cloud : direct;
            default: throw new ArgumentException("Невідома дія для PC Pulse.");
        }
    }

    private void UpdatePhonePresence(bool initialize = false)
    {
        var route = phones.ActiveRoute();
        if (!initialize && route == lastRoute)
            return;
        lastRoute = route;
        var previous = statusIcon;
        statusIcon = TrayGlyph.Draw(trayLight, route is not null);
        tray!.Icon = statusIcon;
        tray.Text = route is null ? "PC Pulse · Телефон від’єднано" : $"PC Pulse · Телефон · {route}";
        previous?.Dispose();
    }

    private void OnTrayThemeChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            var light = TrayGlyph.HasLightBackground();
            if (!exiting && trayLight != light)
            {
                trayLight = light;
                UpdatePhonePresence(true);
            }
        });
    }

    private async void ExitApp()
    {
        if (exiting)
            return;
        exiting = true;
        tray!.Visible = false;
        presenceTimer?.Stop();
        if (windowProcess is { HasExited: false })
            windowProcess.CloseMainWindow();
        showWait?.Unregister(null);
        startup.Cancel();
        if (desktopListener is not null)
            await desktopListener;
        if (server is null && pendingServer is not null)
        {
            try { server = await pendingServer; }
            catch (Exception) { }
        }
        if (bluetooth is not null)
            await bluetooth.DisposeAsync();
        if (relay is not null)
            await relay.DisposeAsync();
        if (server is not null)
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await server.StopAsync(stop.Token);
            }
            catch (OperationCanceledException) { }
            await server.DisposeAsync();
        }
        if (protection is not null)
            await protection.DisposeAsync();
        if (sampler is not null)
            await Task.Run(sampler.Dispose);
        identity?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        if (tray is not null)
            SystemEvents.UserPreferenceChanged -= OnTrayThemeChanged;
        tray?.Dispose();
        trayMenu?.Dispose();
        showWait?.Unregister(null);
        windowProcess?.Dispose();
        agentProcess?.Dispose();
        statusIcon?.Dispose();
        showEvent?.Dispose();
        singleton?.Dispose();
        startup.Dispose();
        base.OnExit(args);
    }
}
