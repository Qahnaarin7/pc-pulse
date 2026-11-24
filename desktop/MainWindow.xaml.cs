using System;
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
    private readonly DesktopBridge bridge;
    private readonly DispatcherTimer refresh;
    private CancellationTokenSource? request;
    private DesktopStatus? status;
    private bool initializedRelay;
    private ProcessSnapshot? processes;
    private bool ramSort;
    private bool day;
    private string themeMode;
    private ProtectionSettings? lastProtection;
    private KeyboardThresholds? lastKeyboard;
    private int tab;
    private int settingsCategory;

    internal MainWindow(DesktopBridge bridge)
    {
        this.bridge = bridge;
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

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (WindowState == WindowState.Minimized)
            Dispatcher.BeginInvoke(() => { if (WindowState == WindowState.Minimized && IsLoaded) Close(); });
    }

    protected override void OnClosed(EventArgs args)
    {
        refresh.Stop();
        CancelRefresh();
        SystemEvents.UserPreferenceChanged -= OnSystemPreference;
        base.OnClosed(args);
    }

    private void RenderStatus(DesktopStatus current)
    {
        status = current;
        ServerStatus.Text = current.Server;
        var route = current.Phone;
        PhoneStatus.Text = route is null ? "Телефон від’єднано" : $"Телефон підключено · {route}";
        PhoneStatus.SetResourceReference(TextBlock.ForegroundProperty, route is null ? "Muted" : "Accent");
        if (tab == 2)
        {
            UpdateAddresses();
            RenderProtection(current.Protection);
            RenderKeyboard(current.Keyboard);
        }
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

    private void SettingsCategoryClicked(object sender, RoutedEventArgs args)
    {
        settingsCategory = int.Parse((string)((Button)sender).Tag);
        ConnectionSettings.Visibility = settingsCategory == 0 ? Visibility.Visible : Visibility.Collapsed;
        HardwareSettings.Visibility = settingsCategory == 1 ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSettings.Visibility = settingsCategory == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ScrollToTop();
        UpdateButtons();
    }

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
        var current = new CancellationTokenSource();
        request = current;
        try
        {
            if (tab == 0)
            {
                var reply = await bridge.SendAsync<DesktopOverview>(new DesktopCommand("overview"), current.Token);
                if (!current.IsCancellationRequested && IsVisible && reply is not null)
                {
                    RenderStatus(reply.Status);
                    RenderOverview(reply.Data);
                }
            }
            else if (tab == 1)
            {
                var reply = await bridge.SendAsync<DesktopProcesses>(new DesktopCommand("processes"), current.Token);
                if (!current.IsCancellationRequested && IsVisible && reply is not null)
                {
                    RenderStatus(reply.Status);
                    processes = reply.Data;
                    SortProcesses();
                }
            }
            else
            {
                var reply = await bridge.SendAsync<DesktopStatus>(new DesktopCommand("status"), current.Token);
                if (!current.IsCancellationRequested && IsVisible && reply is not null)
                    RenderStatus(reply);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!current.IsCancellationRequested)
            {
                if (tab == 0) UpdatedAt.Text = "Показники тимчасово недоступні.";
                else if (tab == 1) ProcessSummary.Text = "Список процесів тимчасово недоступний.";
                else ServerStatus.Text = "Агент тимчасово недоступний.";
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
        var protectedMode = status?.Protection.State is "maximum" or "critical" or "recovering";
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
            var cooling = await bridge.SendAsync<CoolingResponse>(new DesktopCommand("fans", maximum));
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
        var previous = ProcessList.SelectedItem as ProcessRow;
        var rows = sorted.Select(process => new ProcessRow(process.Name, process.Pid,
            process.CpuPercent is { } cpu ? $"{cpu:F1} %" : "—", Bytes(process.WorkingSetBytes), process.Started)).ToArray();
        ProcessList.ItemsSource = rows;
        if (previous?.Started is { } started)
            ProcessList.SelectedItem = rows.FirstOrDefault(row => row.Pid == previous.Pid && row.Started == started);
        var window = list.CpuWindowSeconds is { } seconds ? $" · CPU за {seconds:F1} с" : "";
        var metric = ramSort ? "Пам’ять" : "CPU";
        ProcessSummary.Text = $"{metric} · {rows.Length} процесів{window} · {list.SampledAt.LocalDateTime:HH:mm:ss}";
    }

    private void ProcessSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        TerminateProcessButton.IsEnabled = ProcessList.SelectedItem is ProcessRow { Started: > 0 };

    private async void TerminateProcessClicked(object sender, RoutedEventArgs args)
    {
        if (ProcessList.SelectedItem is not ProcessRow { Started: { } started } selected)
            return;
        var prompt = $"Завершити {selected.Name} (PID {selected.Pid})? Незбережені дані можуть бути втрачені.";
        if (MessageBox.Show(this, prompt, "Завершити процес", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        TerminateProcessButton.IsEnabled = false;
        try
        {
            var reply = await bridge.SendAsync<ProcessAction>(new DesktopCommand("terminate", Pid: selected.Pid, Started: started));
            if (reply?.Result == "ended")
            {
                CancelRefresh();
                Refresh();
            }
            else
            {
                var message = reply?.Result switch
                {
                    "changed" => "Процес змінився. Оновіть список і виберіть його знову.",
                    "protected" => "Цей процес захищено.",
                    _ => "Windows не підтвердила завершення процесу."
                };
                MessageBox.Show(this, message, "Завершити процес", MessageBoxButton.OK);
            }
        }
        catch (Exception)
        {
            MessageBox.Show(this, "Агент не зміг завершити процес.", "Завершити процес", MessageBoxButton.OK);
        }
        finally { TerminateProcessButton.IsEnabled = ProcessList.SelectedItem is ProcessRow { Started: > 0 }; }
    }

    private void UpdateAddresses()
    {
        var addresses = MonitorServer.PrivateAddresses();
        AddressList.Text = addresses.Count == 0 ? "Активних локальних IPv4-адрес поки немає."
            : string.Join("   ·   ", addresses.Select(a => $"{a.Adapter} {a.Address}"));
        var relay = status?.Relay;
        if (!initializedRelay && relay is not null)
        {
            RelayEnabled.IsChecked = relay.Enabled;
            RelayAddress.Text = relay.Origin;
            RelaySave.IsEnabled = true;
            initializedRelay = true;
        }
        CopyCodeButton.IsEnabled = status?.CanPair == true && addresses.Count > 0;
        CopyRelayCodeButton.IsEnabled = status?.CanPair == true && relay?.CanPair == true;
        RelayStatus.Text = relay?.State switch
        {
            "online" => "Cloudflare підключено. У PC Pulse на телефоні відкрийте «Налаштування», вставте код і збережіть підключення.",
            "connecting" => "Підключення до Cloudflare…",
            "offline" => "Cloudflare не відповів. Перевірте адресу й ключ реєстрації.",
            _ => "Cloudflare вимкнено. Відкрийте налаштування ретранслятора, щоб додати Worker."
        };
        var bluetooth = status?.Bluetooth;
        BluetoothEnabled.IsEnabled = bluetooth is not null;
        BluetoothEnabled.IsChecked = bluetooth?.Enabled == true;
        CopyBluetoothCodeButton.IsEnabled = bluetooth?.State == "ready";
        BluetoothState.Text = bluetooth?.State switch
        {
            "ready" => "Готово. Скопіюйте код у застосунок Android.",
            "starting" => "Запуск Bluetooth…",
            "unavailable" => "Адаптер недоступний. Увімкніть Bluetooth у Windows і повторіть спробу.",
            _ => "Вимкнено."
        };
    }

    private async void CopyCodeClicked(object sender, RoutedEventArgs args) => await CopyPairingCode(false, CopyFeedback);
    private async void CopyRelayCodeClicked(object sender, RoutedEventArgs args) => await CopyPairingCode(true, CopyRelayFeedback);
    private async void CopyBluetoothCodeClicked(object sender, RoutedEventArgs args) =>
        await CopyPairingCode(false, CopyBluetoothFeedback, "pair-bluetooth");

    private async Task CopyPairingCode(bool cloud, TextBlock feedback, string command = "pair")
    {
        try
        {
            var code = await bridge.SendAsync<string>(new DesktopCommand(command, cloud));
            if (code is null)
                return;
            Clipboard.SetText(code);
            feedback.Text = "Код скопійовано. Вставте його в PC Pulse на телефоні.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            feedback.Text = "Буфер обміну зайнятий. Спробуйте ще раз.";
        }
        catch (Exception)
        {
            feedback.Text = "Не вдалося отримати код від агента.";
        }
    }

    private async void BluetoothClicked(object sender, RoutedEventArgs args)
    {
        BluetoothEnabled.IsEnabled = false;
        try
        {
            var result = await bridge.SendAsync<DesktopStatus>(new DesktopCommand("bluetooth", BluetoothEnabled.IsChecked == true));
            if (result is not null)
                RenderStatus(result);
        }
        catch (Exception) { BluetoothState.Text = "Агент не підтвердив зміну Bluetooth."; }
        finally { BluetoothEnabled.IsEnabled = true; }
    }

    private async void SaveRelayClicked(object sender, RoutedEventArgs args)
    {
        RelaySave.IsEnabled = false;
        try
        {
            var result = await bridge.SendAsync<DesktopStatus>(new DesktopCommand("relay", RelayEnabled.IsChecked == true,
                Address: RelayAddress.Text, Enrollment: RelayEnrollment.Password.Trim()));
            RelayEnrollment.Clear();
            RelayFeedback.Text = "Налаштування збережено.";
            if (result is not null)
                RenderStatus(result);
        }
        catch (ArgumentException error)
        {
            RelayFeedback.Text = error.Message;
        }
        catch (Exception)
        {
            RelayFeedback.Text = "Не вдалося зберегти налаштування Cloudflare.";
        }
        finally { RelaySave.IsEnabled = true; }
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
            var configured = await bridge.SendAsync<ProtectionStatus>(new DesktopCommand("thermal", Protection: requested));
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
            SaveProtection.IsEnabled = status?.Protection.Supported == true;
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

    private void RenderKeyboard(KeyboardStatus value)
    {
        var settings = value.Settings;
        if (lastKeyboard != settings)
        {
            KeyboardEnabled.IsChecked = settings.Enabled;
            CpuWarm.Text = settings.CpuWarm.ToString();
            CpuHot.Text = settings.CpuHot.ToString();
            GpuWarm.Text = settings.GpuWarm.ToString();
            GpuHot.Text = settings.GpuHot.ToString();
            lastKeyboard = settings;
        }
        KeyboardEnabled.IsEnabled = CpuWarm.IsEnabled = CpuHot.IsEnabled = GpuWarm.IsEnabled = GpuHot.IsEnabled =
            SaveKeyboard.IsEnabled = value.Supported;
        KeyboardState.Text = value.State switch
        {
            "green" => "Зелений · нижче жовтих порогів",
            "yellow" => "Жовтий · підвищена температура",
            "red" => "Червоний · досягнуто червоного порога",
            "unsupported" => "На цьому ПК керування підсвіткою недоступне.",
            "off" => "Вимкнено. Клавіатура використовує збережений колір.",
            "unavailable" => "Драйвер поки не підтвердив зміну кольору.",
            _ => "Очікування температури…"
        };
    }

    private async void SaveKeyboardClicked(object sender, RoutedEventArgs args)
    {
        if (!int.TryParse(CpuWarm.Text, out var cpuWarm) || !int.TryParse(CpuHot.Text, out var cpuHot) ||
            !int.TryParse(GpuWarm.Text, out var gpuWarm) || !int.TryParse(GpuHot.Text, out var gpuHot))
        {
            KeyboardFeedback.Text = "Введіть цілі числа в усіх чотирьох полях.";
            return;
        }
        var settings = new KeyboardThresholds(KeyboardEnabled.IsChecked == true, cpuWarm, cpuHot, gpuWarm, gpuHot);
        if (!settings.IsValid)
        {
            KeyboardFeedback.Text = "Жовтий поріг має бути нижчим. Межі CPU 35–100 °C і GPU 35–95 °C.";
            return;
        }
        SaveKeyboard.IsEnabled = false;
        try
        {
            var result = await bridge.SendAsync<KeyboardStatus>(new DesktopCommand("keyboard", Keyboard: settings));
            if (result is not null)
                RenderKeyboard(result);
            KeyboardFeedback.Text = result is null ? "ПК не підтвердив налаштування." : "Пороги збережено.";
        }
        catch (Exception) { KeyboardFeedback.Text = "Не вдалося зберегти пороги."; }
        finally { SaveKeyboard.IsEnabled = status?.Keyboard.Supported == true; }
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


    private void ApplyTheme()
    {
        var near = ColorOf(day ? "#EBF1F8" : "#1D252F");
        var far = ColorOf(day ? "#F8FAFD" : "#11151D");
        Resources["Back"] = new LinearGradientBrush(near, far, 145);
        Brush("Panel", day ? "#FCFDFF" : "#212B36");
        Brush("Field", day ? "#F0F4FA" : "#293747");
        Brush("Line", day ? "#D5DFEA" : "#3A4858");
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
        SelectStyle(ConnectionCategory, settingsCategory == 0);
        SelectStyle(HardwareCategory, settingsCategory == 1);
        SelectStyle(AppearanceCategory, settingsCategory == 2);
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
    private sealed record ProcessRow(string Name, int Pid, string Cpu, string Memory, ulong? Started);
}
