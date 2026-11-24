using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PcPulse.Desktop;

public partial class MainWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKey = @"Software\PcPulse";
    private const string AppName = "PC Pulse";
    private readonly MetricsSampler sampler;
    private readonly ThermalProtection protection;
    private readonly int port;
    private readonly DispatcherTimer refresh;
    private CancellationTokenSource? request;
    private PairingIdentity? identity;
    private CloudRelay? relay;
    private ProcessSnapshot? processes;
    private bool ramSort;
    private bool day;
    private string themeMode;
    private ProtectionSettings? lastProtection;
    private int tab;

    internal MainWindow(MetricsSampler sampler, ThermalProtection protection, int port)
    {
        this.sampler = sampler;
        this.protection = protection;
        this.port = port;
        refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refresh.Tick += (_, _) => Refresh();
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTitleBar();
        ComputerTitle.Text = Environment.MachineName;
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        AutoStart.IsChecked = run?.GetValue(AppName) is string;
        using var settings = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var stored = settings?.GetValue("Theme") as string;
        themeMode = stored is "day" or "night" ? stored : "system";
        day = IsDayTheme();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnSystemPreference;
        Activated += (_, _) => UpdateSystemTheme();
    }

    internal void SetServer(PairingIdentity? currentIdentity, string message)
    {
        identity = currentIdentity;
        ServerStatus.Text = message;
        UpdateAddresses();
    }

    internal void SetRelay(CloudRelay bridge)
    {
        relay = bridge;
        var current = bridge.ReadStatus();
        RelayEnabled.IsChecked = current.Enabled;
        RelayAddress.Text = current.Origin;
        RelaySave.IsEnabled = true;
        UpdateAddresses();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        args.Cancel = true;
        Hide();
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (IsVisible)
        {
            Refresh();
            refresh.Start();
        }
        else
        {
            refresh.Stop();
            CancelRefresh();
        }
    }

    private void OverviewClicked(object sender, RoutedEventArgs args) => SelectTab(0);
    private void ProcessesClicked(object sender, RoutedEventArgs args) => SelectTab(1);
    private void SettingsClicked(object sender, RoutedEventArgs args) => SelectTab(2);

    private void SelectTab(int index)
    {
        tab = index;
        refresh.Interval = TimeSpan.FromSeconds(index == 0 ? 2 : 5);
        OverviewPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProcessPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
        CancelRefresh();
        Refresh();
    }

    private async void Refresh()
    {
        if (!IsVisible || request is not null)
            return;
        if (tab == 2)
        {
            UpdateAddresses();
            RenderProtection(protection.GetStatus());
            return;
        }

        var current = new CancellationTokenSource();
        request = current;
        try
        {
            if (tab == 0)
            {
                var data = await Task.Run(() => sampler.ReadOverview(true), current.Token);
                if (!current.IsCancellationRequested && IsVisible)
                    RenderOverview(data);
            }
            else
            {
                var data = await Task.Run(() => sampler.ReadProcessesAsync(current.Token), current.Token);
                if (!current.IsCancellationRequested && IsVisible)
                {
                    processes = data;
                    SortProcesses();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!current.IsCancellationRequested)
            {
                if (tab == 0) UpdatedAt.Text = "Показники тимчасово недоступні.";
                else ProcessSummary.Text = "Список процесів тимчасово недоступний.";
            }
        }
        finally
        {
            if (ReferenceEquals(request, current))
                request = null;
            current.Dispose();
        }
    }

    private void CancelRefresh()
    {
        request?.Cancel();
        request = null;
    }

    private void RenderOverview(OverviewResponse data)
    {
        UpdatedAt.Text = "Оновлено о " + data.SampledAt.LocalDateTime.ToString("HH:mm:ss");
        CpuLoad.Text = Percent(data.CpuPercent);
        CpuBar.Value = data.CpuPercent ?? 0;
        var memory = data.Memory;
        var ram = memory is { TotalBytes: > 0 } ? memory.UsedBytes * 100d / memory.TotalBytes : (double?)null;
        RamLoad.Text = memory is null ? "—" : $"{Percent(ram)} · {Bytes(memory.UsedBytes)}";
        RamBar.Value = ram ?? 0;
        CpuTemp.Text = Degrees(data.CpuTemperatureC);
        CpuSource.Text = data.CpuTemperatureSource == "gigabyte_control_center" ? "Датчик Control Center"
            : data.CpuTemperatureC.HasValue ? "Датчик процесора" : SensorStatus(data.CpuTemperatureStatus);
        var gpu = data.Gpus.FirstOrDefault(value => value.TemperatureC.HasValue) ?? data.Gpus.FirstOrDefault();
        GpuTemp.Text = Degrees(gpu?.TemperatureC);
        GpuDetail.Text = gpu is null ? "Датчик GPU недоступний" : string.Join(Environment.NewLine,
            data.Gpus.Select(item => $"{item.Name} · {Percent(item.LoadPercent)} · {Degrees(item.TemperatureC)}"));
        DiskSensors.Text = Components(data.ComponentTemperatures?.Disks);
        MemorySensors.Text = Components(data.ComponentTemperatures?.Memory);
        RenderCooling(data.Cooling);
    }

    private void RenderCooling(CoolingResponse? cooling)
    {
        CoolingCard.Visibility = cooling is null ? Visibility.Collapsed : Visibility.Visible;
        if (cooling is null)
            return;
        var mode = cooling.Mode switch
        {
            "automatic" => "Авто",
            "maximum" => "Максимум",
            "custom" => "Власний профіль",
            _ => "Режим недоступний"
        };
        CoolingText.Text = $"{mode} · CPU {cooling.CpuFanRpm?.ToString() ?? "—"} · GPU {cooling.GpuFanRpm?.ToString() ?? "—"} об/хв";
        var protectedMode = protection.GetStatus().State is "maximum" or "critical" or "recovering";
        AutoFans.IsEnabled = cooling.CanControl && cooling.Mode != "automatic" && !protectedMode;
        MaxFans.IsEnabled = cooling.CanControl && cooling.Mode != "maximum";
    }

    private async void AutoFansClicked(object sender, RoutedEventArgs args) => await SetFans(false);
    private async void MaxFansClicked(object sender, RoutedEventArgs args) => await SetFans(true);

    private async Task SetFans(bool maximum)
    {
        AutoFans.IsEnabled = MaxFans.IsEnabled = false;
        try
        {
            var cooling = await Task.Run(() => protection.SetManualMode(maximum));
            if (cooling is null) CoolingText.Text = "Термозахист або Control Center утримує поточний режим.";
            else RenderCooling(cooling);
        }
        catch (Exception)
        {
            CoolingText.Text = "Не вдалося перемкнути охолодження.";
        }
    }

    private void SortCpuClicked(object sender, RoutedEventArgs args) => SetSort(false);
    private void SortRamClicked(object sender, RoutedEventArgs args) => SetSort(true);
    private void SearchChanged(object sender, TextChangedEventArgs args) => SortProcesses();

    private void SetSort(bool byMemory)
    {
        ramSort = byMemory;
        UpdateButtons();
        SortProcesses();
    }

    private void SortProcesses()
    {
        if (processes is not { } list)
            return;
        var query = Search.Text.Trim();
        var filtered = list.Processes.Where(process => query.Length == 0 ||
            process.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            process.Pid.ToString().Contains(query));
        var sorted = ramSort
            ? filtered.OrderByDescending(process => process.WorkingSetBytes).ThenBy(process => process.Pid)
            : filtered.OrderByDescending(process => process.CpuPercent ?? -1).ThenBy(process => process.Pid);
        var rows = sorted.Select(process => new ProcessRow(process.Name, process.Pid,
            process.CpuPercent is { } cpu ? $"{cpu:F1} %" : "—", Bytes(process.WorkingSetBytes))).ToArray();
        ProcessList.ItemsSource = rows;
        var window = list.CpuWindowSeconds is { } seconds ? $" · CPU за {seconds:F1} с" : "";
        var metric = ramSort ? "Пам’ять" : "CPU";
        ProcessSummary.Text = $"{metric} · {rows.Length} процесів{window} · {list.SampledAt.LocalDateTime:HH:mm:ss}";
    }

    private void UpdateAddresses()
    {
        var addresses = MonitorServer.PrivateAddresses();
        AddressList.Text = addresses.Count == 0 ? "Активних локальних IPv4-адрес поки немає."
            : string.Join("   ·   ", addresses.Select(a => $"{a.Adapter} {a.Address}"));
        var status = relay?.ReadStatus();
        CopyCodeButton.IsEnabled = identity is not null && addresses.Count > 0;
        CopyRelayCodeButton.IsEnabled = identity is not null && status?.CanPair == true;
        RelayStatus.Text = status?.State switch
        {
            "online" => "Cloudflare підключено. У PC Pulse на телефоні відкрийте «Налаштування», вставте код і збережіть підключення.",
            "connecting" => "Підключення до Cloudflare…",
            "offline" => "Cloudflare не відповів. Перевірте адресу й ключ реєстрації.",
            _ => "Cloudflare вимкнено. Відкрийте налаштування ретранслятора, щоб додати Worker."
        };
    }

    private void CopyCodeClicked(object sender, RoutedEventArgs args) => CopyPairingCode(false, CopyFeedback);
    private void CopyRelayCodeClicked(object sender, RoutedEventArgs args) => CopyPairingCode(true, CopyRelayFeedback);

    private void CopyPairingCode(bool cloud, TextBlock feedback)
    {
        var addresses = MonitorServer.PrivateAddresses();
        if (identity is null)
            return;
        var direct = addresses.Count > 0 ? identity.CreateCode(addresses, port) : null;
        var code = cloud ? relay?.PhoneCode(direct) : direct;
        if (code is null)
            return;
        try
        {
            Clipboard.SetText(code);
            feedback.Text = "Код скопійовано. Вставте його в PC Pulse на телефоні.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            feedback.Text = "Буфер обміну зайнятий. Спробуйте ще раз.";
        }
    }

    private void SaveRelayClicked(object sender, RoutedEventArgs args)
    {
        if (relay is null)
            return;
        try
        {
            relay.Configure(RelayEnabled.IsChecked == true, RelayAddress.Text, RelayEnrollment.Password.Trim());
            RelayEnrollment.Clear();
            RelayFeedback.Text = "Налаштування збережено.";
            UpdateAddresses();
        }
        catch (ArgumentException error)
        {
            RelayFeedback.Text = error.Message;
        }
        catch (Exception)
        {
            RelayFeedback.Text = "Не вдалося зберегти налаштування Cloudflare.";
        }
    }

    private void AutoStartClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (AutoStart.IsChecked == true)
                run.SetValue(AppName, $"\"{Environment.ProcessPath}\" --tray");
            else
                run.DeleteValue(AppName, false);
            SettingsFeedback.Text = AutoStart.IsChecked == true ? "Автозапуск у треї увімкнено." : "Автозапуск вимкнено.";
        }
        catch (UnauthorizedAccessException)
        {
            AutoStart.IsChecked = AutoStart.IsChecked != true;
            SettingsFeedback.Text = "Windows відхилила зміну автозапуску.";
        }
    }

    private async void ProtectionClicked(object sender, RoutedEventArgs args)
    {
        if (!int.TryParse(ProtectCpu.Text, out var cpu) || !int.TryParse(ProtectGpu.Text, out var gpu) ||
            !new ProtectionSettings(true, cpu, gpu).IsValid)
        {
            ProtectionFeedback.Text = "Допустимі пороги: CPU 70–100 °C, GPU 60–95 °C.";
            return;
        }
        var requested = new ProtectionSettings(ProtectEnabled.IsChecked == true, cpu, gpu);
        SaveProtection.IsEnabled = false;
        try
        {
            var configured = await Task.Run(() => protection.Configure(requested));
            if (configured is null)
                ProtectionFeedback.Text = "ПК не підтвердив доступ до режиму максимальних обертів.";
            else
            {
                lastProtection = null;
                RenderProtection(configured);
                ProtectionFeedback.Text = "Збережено.";
            }
        }
        catch (Exception)
        {
            ProtectionFeedback.Text = "Не вдалося зберегти налаштування.";
        }
        finally
        {
            SaveProtection.IsEnabled = protection.GetStatus().Supported;
        }
    }

    private void RenderProtection(ProtectionStatus status)
    {
        var current = new ProtectionSettings(status.Enabled, status.CpuCritical, status.GpuCritical);
        if (lastProtection != current)
        {
            ProtectEnabled.IsChecked = current.Enabled;
            ProtectCpu.Text = current.CpuCritical.ToString();
            ProtectGpu.Text = current.GpuCritical.ToString();
            lastProtection = current;
        }
        ProtectEnabled.IsEnabled = ProtectCpu.IsEnabled = ProtectGpu.IsEnabled = SaveProtection.IsEnabled = status.Supported;
        ProtectionState.Text = status.State switch
        {
            "unsupported" => "Керування вентиляторами для цієї моделі недоступне.",
            "off" => "Вимкнено.",
            "watching" => "Перевіряє температури приблизно кожні 2 с.",
            "maximum" => "Термозахист тримає максимальні оберти.",
            "recovering" => "Чекає 30 с стабільного охолодження.",
            "manualMaximum" => "Вентилятори вже в режимі «Максимум».",
            "critical" or "restricted" => "Поточний профіль Control Center обмежує керування.",
            "unconfirmed" => "Control Center ще не підтвердив зміну режиму.",
            _ => "Очікування показників датчиків."
        };
    }

    private void SystemClicked(object sender, RoutedEventArgs args) => SetTheme("system");
    private void DayClicked(object sender, RoutedEventArgs args) => SetTheme("day");
    private void NightClicked(object sender, RoutedEventArgs args) => SetTheme("night");

    private void SetTheme(string mode)
    {
        themeMode = mode;
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        settings.SetValue("Theme", mode);
        day = IsDayTheme();
        ApplyTheme();
    }

    private bool IsDayTheme()
    {
        if (themeMode != "system")
            return themeMode == "day";
        using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return personalize?.GetValue("AppsUseLightTheme") is not 0;
    }

    private void OnSystemPreference(object sender, UserPreferenceChangedEventArgs args)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(UpdateSystemTheme);
    }

    private void UpdateSystemTheme()
    {
        var systemDay = IsDayTheme();
        if (systemDay == day)
            return;
        day = systemDay;
        ApplyTheme();
    }

    internal void StopWatchingTheme() => SystemEvents.UserPreferenceChanged -= OnSystemPreference;

    private void ApplyTheme()
    {
        var near = ColorOf(day ? "#DFEEFC" : "#25354F");
        var far = ColorOf(day ? "#F5F8FC" : "#171C2E");
        Resources["Back"] = new LinearGradientBrush(near, far, 145);
        Brush("Panel", day ? "#F8FBFF" : "#253249");
        Brush("Field", day ? "#E8F0FA" : "#2C3C54");
        Brush("Line", day ? "#C8D6E6" : "#3C4F6A");
        Brush("Ink", day ? "#19283A" : "#F1F5FC");
        Brush("Muted", day ? "#46586E" : "#B8C5DB");
        Brush("Primary", day ? "#075DB6" : "#1468B8");
        Brush("Accent", day ? "#075DB6" : "#9BB8FF");
        Brush("Thermal", day ? "#A74317" : "#FFC28F");
        UpdateButtons();
        ApplyTitleBar();
    }

    private void ApplyTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dark = day ? 0 : 1;
        if (handle != 0)
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private void UpdateButtons()
    {
        SelectStyle(OverviewTab, tab == 0);
        SelectStyle(ProcessTab, tab == 1);
        SelectStyle(SettingsTab, tab == 2);
        SelectStyle(SystemTheme, themeMode == "system");
        SelectStyle(DayTheme, themeMode == "day");
        SelectStyle(NightTheme, themeMode == "night");
        SelectStyle(SortCpu, !ramSort);
        SelectStyle(SortRam, ramSort);
    }

    private void SelectStyle(Button button, bool selected) =>
        button.Style = (Style)FindResource(selected ? "StrongButton" : typeof(Button));

    private void Brush(string name, string hex) => Resources[name] = new SolidColorBrush(ColorOf(hex));
    private static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static string Percent(double? value) => value is { } number ? $"{number:F0} %" : "—";
    private static string Degrees(float? value) => value is { } number ? $"{number:F0} °C" : "—";
    private static string Bytes(double value) => value >= 1073741824 ? $"{value / 1073741824:F1} ГіБ" : $"{value / 1048576:F0} МіБ";
    private static string Components(System.Collections.Generic.List<ComponentTemperature>? entries) => entries is null
        ? "Зчитування датчиків…"
        : entries.Count == 0 ? "Драйвер не надав показників"
        : string.Join(Environment.NewLine, entries.Select(item => $"{item.Name}  ·  {Degrees(item.TemperatureC)}"));
    private static string SensorStatus(string status) => status switch
    {
        "driver_required" => "Для цього датчика може знадобитися PawnIO",
        "administrator_required" => "Драйвер потребує прав адміністратора",
        _ => "Датчик недоступний"
    };
    private sealed record ProcessRow(string Name, int Pid, string Cpu, string Memory);
}
