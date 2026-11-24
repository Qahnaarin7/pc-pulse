using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Forms = System.Windows.Forms;

namespace PcPulse.Desktop;

public partial class App : Application
{
    private const string InstanceEvent = @"Local\PcPulse.Desktop.Show";
    private Mutex? singleton;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private Icon? icon;
    private MainWindow? window;
    private MetricsSampler? sampler;
    private ThermalProtection? protection;
    private CloudRelay? relay;
    private PairingIdentity? identity;
    private WebApplication? server;
    private Task<WebApplication>? pendingServer;
    private readonly CancellationTokenSource startup = new();
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        singleton = new Mutex(true, @"Local\PcPulse.Desktop.Instance", out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(InstanceEvent, out var existing))
            {
                using (existing) existing.Set();
            }
            Shutdown();
            return;
        }

        var port = 8787;
        var portOption = Array.IndexOf(args.Args, "--port");
        if (portOption >= 0 && portOption + 1 < args.Args.Length &&
            int.TryParse(args.Args[portOption + 1], out var requested) && requested is >= 1 and <= 65535)
            port = requested;

        sampler = new MetricsSampler(true);
        protection = new ThermalProtection(sampler);
        window = new MainWindow(sampler, protection, port);
        MainWindow = window;
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceEvent);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            Dispatcher.BeginInvoke(ShowWindow);
        }, null, Timeout.Infinite, false);

        icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Відкрити PC Pulse", null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
        trayMenu.Items.Add("Вихід", null, (_, _) => Dispatcher.BeginInvoke(ExitApp));
        tray = new Forms.NotifyIcon { Icon = icon, Text = "PC Pulse", ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
        if (!args.Args.Contains("--tray"))
            window.Show();

        try
        {
            identity = PairingIdentity.Load();
            pendingServer = MonitorServer.StartAsync(IPAddress.Any, port, identity, sampler, protection, startup.Token);
            server = await pendingServer;
            if (exiting)
                return;
            relay = new CloudRelay(identity, port);
            window.SetRelay(relay);
            window.SetServer(identity, $"Готово до підключення · порт {port}");
        }
        catch (Exception)
        {
            if (!exiting)
                window.SetServer(null, $"Порт {port} недоступний. Закрийте попередній агент або задайте інший порт.");
        }
    }

    private void ShowWindow()
    {
        if (exiting || window is null)
            return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private async void ExitApp()
    {
        if (exiting)
            return;
        exiting = true;
        tray!.Visible = false;
        window?.Hide();
        showWait?.Unregister(null);
        startup.Cancel();
        if (server is null && pendingServer is not null)
        {
            try { server = await pendingServer; }
            catch (Exception) { }
        }
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
        window?.StopWatchingTheme();
        tray?.Dispose();
        trayMenu?.Dispose();
        icon?.Dispose();
        showEvent?.Dispose();
        singleton?.Dispose();
        startup.Dispose();
        base.OnExit(args);
    }
}
