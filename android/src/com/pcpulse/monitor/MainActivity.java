package com.pcpulse.monitor;

import android.Manifest;
import android.app.Activity;
import android.app.NotificationManager;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Insets;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.net.ConnectivityManager;
import android.net.Network;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.BaseAdapter;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.LocalTime;
import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;

import javax.net.ssl.SSLException;

public final class MainActivity extends Activity {
    private static final int MATCH = ViewGroup.LayoutParams.MATCH_PARENT;
    private static final int WRAP = ViewGroup.LayoutParams.WRAP_CONTENT;
    private static final int NOTIFICATION_PERMISSION = 19;
    private static final Locale UK = Locale.forLanguageTag("uk-UA");
    private static final DateTimeFormatter CLOCK = DateTimeFormatter.ofPattern("HH:mm:ss");

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService network = Executors.newSingleThreadExecutor();
    private final Runnable poll = this::requestSnapshot;
    private final Runnable networkChanged = () -> {
        if (this.resumed && !this.setup) {
            restartPolling();
        }
    };
    private final ConnectivityManager.NetworkCallback connectivity = new ConnectivityManager.NetworkCallback() {
        @Override public void onAvailable(Network network) {
            handler.removeCallbacks(networkChanged);
            handler.postDelayed(networkChanged, 350);
        }
    };
    private boolean connectivityRegistered;
    private SharedPreferences preferences;
    private PulsePalette palette;
    private boolean day;
    private PcConnection pc;
    private Future<?> request;
    private int generation;
    private int failures;
    private boolean resumed;
    private boolean setup;
    private boolean processes;
    private boolean sensors;
    private boolean sortByRam;
    private boolean hasData;
    private String lastUpdate;
    private double processWindow;
    private String host;
    private String search = "";
    private LinearLayout root;
    private TextView hostView;
    private TextView statusView;
    private TextView reconnect;
    private TextView processSummary;
    private MetricCard cpu;
    private MetricCard memory;
    private MetricCard temperature;
    private LinearLayout gpuContainer;
    private TextView diskTemperatures;
    private TextView memoryTemperatures;
    private LinearLayout coolingCard;
    private LinearLayout coolingButtons;
    private TextView coolingReading;
    private TextView coolingFeedback;
    private String displayedCoolingMode;
    private boolean displayedCanControl;
    private ProcessAdapter processAdapter;
    private ListView processList;
    private List<ProcessInfo> allProcesses = Collections.emptyList();
    private TextView alertFeedback;
    private Switch alertSwitch;

    @Override protected void onCreate(Bundle state) {
        preferences = getSharedPreferences("pc_pulse", MODE_PRIVATE);
        day = preferences.getBoolean("day_theme", false);
        palette = PulsePalette.forDay(day);
        setTheme(day ? R.style.PulseThemeDay : R.style.PulseTheme);
        super.onCreate(state);
        sensors = preferences.getBoolean("sensors", true);
        try {
            pc = PcConnection.fromCode(preferences.getString("pairing", ""));
        } catch (IllegalArgumentException ignored) {
            pc = null;
        }
        processes = state != null && state.getBoolean("processes");
        sortByRam = state != null && state.getBoolean("sortByRam");
        setup = pc == null || state != null && state.getBoolean("setup");
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        if (Build.VERSION.SDK_INT >= 30) {
            getWindow().setDecorFitsSystemWindows(false);
        }
        render();
        if (Build.VERSION.SDK_INT >= 30) {
            int flags = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS |
                    WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
            WindowInsetsController controller = getWindow().getDecorView().getWindowInsetsController();
            if (controller != null) {
                controller.setSystemBarsAppearance(day ? flags : 0, flags);
            }
        }
    }

    @Override protected void onSaveInstanceState(Bundle state) {
        state.putBoolean("processes", processes);
        state.putBoolean("sortByRam", sortByRam);
        state.putBoolean("setup", setup);
        super.onSaveInstanceState(state);
    }

    @Override protected void onResume() {
        super.onResume();
        resumed = true;
        if (!setup) {
            setStatus("Підключення…", palette.muted);
            restartPolling();
        }
    }

    @Override protected void onStart() {
        super.onStart();
        try {
            getSystemService(ConnectivityManager.class).registerDefaultNetworkCallback(connectivity);
            connectivityRegistered = true;
        } catch (RuntimeException ignored) {
            // Periodic reconnect is available even when Android cannot supply network callbacks.
        }
    }

    @Override protected void onStop() {
        if (connectivityRegistered) {
            getSystemService(ConnectivityManager.class).unregisterNetworkCallback(connectivity);
            connectivityRegistered = false;
        }
        handler.removeCallbacks(networkChanged);
        super.onStop();
    }

    @Override protected void onPause() {
        resumed = false;
        stopPolling();
        super.onPause();
    }

    @Override protected void onDestroy() {
        network.shutdownNow();
        super.onDestroy();
    }

    @Override public void onBackPressed() {
        if (setup && pc != null) {
            openDashboard();
        } else {
            super.onBackPressed();
        }
    }

    private void render() {
        root = column();
        root.setBackground(new GradientDrawable(GradientDrawable.Orientation.TL_BR,
                new int[] {palette.start, palette.background, palette.end}));
        root.setFocusableInTouchMode(true);
        if (Build.VERSION.SDK_INT >= 30) {
            root.setOnApplyWindowInsetsListener((view, insets) -> {
                Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                Insets keyboard = insets.getInsets(WindowInsets.Type.ime());
                view.setPadding(bars.left, bars.top, bars.right, Math.max(bars.bottom, keyboard.bottom));
                return insets;
            });
        }
        setContentView(root);
        addHeader();
        if (setup) {
            renderSetup();
        } else {
            renderDashboard();
        }
    }

    private void addHeader() {
        LinearLayout header = row();
        header.setPadding(dp(20), dp(10), dp(20), dp(10));
        ImageView mark = new ImageView(this);
        mark.setImageDrawable(getDrawable(R.drawable.ic_pulse));
        header.addView(mark, new LinearLayout.LayoutParams(dp(42), dp(42)));
        TextView title = label("PC Pulse", 23, palette.text, true);
        LinearLayout.LayoutParams titleParams = weighted();
        titleParams.leftMargin = dp(12);
        header.addView(title, titleParams);
        if (!setup) {
            header.addView(button("Налаштування", false, () -> {
                setup = true;
                stopPolling();
                render();
            }), new LinearLayout.LayoutParams(WRAP, WRAP));
        }
        root.addView(header, new LinearLayout.LayoutParams(MATCH, WRAP));
    }

    private void renderSetup() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        root.addView(scroll, new LinearLayout.LayoutParams(MATCH, 0, 1));
        LinearLayout content = column();
        content.setPadding(dp(20), dp(20), dp(20), dp(28));
        scroll.addView(content, new ScrollView.LayoutParams(MATCH, WRAP));
        add(content, label(pc == null ? "Підключіть комп’ютер" : "Налаштування", 30, palette.text, true), 0);
        if (pc == null) {
            add(content, label("Скопіюйте код із PC Pulse на Windows. Підключення можливе через Wi-Fi або приватну VPN.", 16, palette.muted, false), 10);
        } else {
            add(content, button("До огляду", false, this::openDashboard), 12);
            LinearLayout appearance = card();
            add(appearance, label("Тема", 17, palette.text, true), 0);
            LinearLayout choices = row();
            addTabs(choices, "День", day, () -> changeTheme(true), "Ніч", !day, () -> changeTheme(false));
            add(appearance, choices, 9);
            add(content, appearance, 14);
            renderAlertSettings(content);
        }

        LinearLayout form = card();
        add(content, form, 24);
        add(form, label("Код підключення", 15, palette.text, true), 0);
        EditText input = input("Код із Windows");
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE
                | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        input.setTypeface(Typeface.MONOSPACE);
        input.setMaxLines(4);
        input.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        input.setContentDescription("Код підключення");
        add(form, input, 8);
        add(form, button("Вставити код", false, () -> {
            ClipboardManager clipboard = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
            ClipData clip = clipboard.getPrimaryClip();
            if (clip != null && clip.getItemCount() > 0) {
                input.setText(clip.getItemAt(0).coerceToText(this));
                input.setSelection(input.length());
            }
        }), 8);
        Switch sensorSwitch = new Switch(this);
        sensorSwitch.setText("Сенсори на екрані огляду");
        sensorSwitch.setTextColor(palette.text);
        sensorSwitch.setTextSize(15);
        sensorSwitch.setMinHeight(dp(50));
        sensorSwitch.setChecked(sensors);
        styleSwitch(sensorSwitch);
        add(form, sensorSwitch, 10);

        TextView error = label("", 14, palette.thermal, false);
        error.setVisibility(View.GONE);
        add(form, error, 4);
        add(form, button(pc == null ? "Підключити" : "Зберегти підключення", true, () -> {
            String code = input.getText().toString().replaceAll("\\s+", "");
            try {
                if (!code.isEmpty() || pc == null) {
                    pc = PcConnection.fromCode(code);
                    host = null;
                    preferences.edit().putString("pairing", code).apply();
                }
            } catch (IllegalArgumentException invalid) {
                error.setText(invalid.getMessage());
                error.setVisibility(View.VISIBLE);
                return;
            }
            sensors = sensorSwitch.isChecked();
            preferences.edit().putBoolean("sensors", sensors).apply();
            ((InputMethodManager) getSystemService(INPUT_METHOD_SERVICE))
                    .hideSoftInputFromWindow(input.getWindowToken(), 0);
            openDashboard();
        }), 10);
        if (pc != null) {
            add(form, label("Поточна адреса · " + pc.address(), 13, palette.muted, false), 12);
        } else {
            add(content, label("Код дає доступ до показників ПК. Зберігайте його як пароль.", 14, palette.muted, false), 18);
        }
    }

    private void changeTheme(boolean daylight) {
        if (day != daylight) {
            preferences.edit().putBoolean("day_theme", daylight).apply();
            recreate();
        }
    }

    private void renderAlertSettings(LinearLayout content) {
        LinearLayout form = card();
        add(content, form, 16);
        add(form, label("Сповіщення про температуру", 18, palette.text, true), 0);
        alertSwitch = new Switch(this);
        alertSwitch.setText("Фоновий моніторинг");
        alertSwitch.setTextColor(palette.text);
        alertSwitch.setMinHeight(dp(52));
        alertSwitch.setChecked(preferences.getBoolean(ThermalAlertService.ENABLED, false));
        styleSwitch(alertSwitch);
        add(form, alertSwitch, 6);
        add(form, label("Перевірка кожні 15 с, поки ПК доступний.", 13, palette.muted, false), 0);

        int[] saved = ThermalAlertService.loadThresholds(preferences);
        EditText[] fields = new EditText[saved.length];
        LinearLayout headings = row();
        headings.addView(label("Датчик", 12, palette.muted, true), new LinearLayout.LayoutParams(dp(48), WRAP));
        addThresholdColumn(headings, label("Попередження · °C", 12, palette.muted, true));
        addThresholdColumn(headings, label("Критично · °C", 12, palette.muted, true));
        add(form, headings, 16);
        String[] names = {"CPU", "GPU"};
        for (int index = 0; index < names.length; index++) {
            LinearLayout line = row();
            line.addView(label(names[index], 14, palette.text, true), new LinearLayout.LayoutParams(dp(48), WRAP));
            for (int column = 0; column < 2; column++) {
                int offset = index * 2 + column;
                EditText degrees = input("");
                degrees.setSingleLine(true);
                degrees.setInputType(InputType.TYPE_CLASS_NUMBER);
                degrees.setFilters(new android.text.InputFilter[] {new android.text.InputFilter.LengthFilter(3)});
                degrees.setText(Integer.toString(saved[offset]));
                degrees.setContentDescription(names[index] + (column == 0 ? " попередження, °C" : " критично, °C"));
                fields[offset] = degrees;
                addThresholdColumn(line, degrees);
            }
            add(form, line, 8);
        }

        alertFeedback = label("", 13, palette.muted, false);
        alertFeedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        alertFeedback.setVisibility(View.GONE);
        add(form, alertFeedback, 8);
        add(form, button("Зберегти сповіщення", true, () -> saveAlerts(fields)), 10);
    }

    private void addThresholdColumn(LinearLayout row, View content) {
        LinearLayout.LayoutParams params = weighted();
        params.leftMargin = dp(8);
        row.addView(content, params);
    }

    private void saveAlerts(EditText[] fields) {
        int[] values = new int[fields.length];
        try {
            for (int index = 0; index < fields.length; index++) {
                values[index] = Integer.parseInt(fields[index].getText().toString().trim());
                if (values[index] < ThermalAlertService.MIN_THRESHOLD ||
                        values[index] > ThermalAlertService.MAX_THRESHOLD) {
                    throw new NumberFormatException();
                }
            }
        } catch (NumberFormatException invalid) {
            showAlertFeedback("Вкажіть цілі температури від 30 до 110 °C.", palette.thermal);
            return;
        }
        if (values[0] >= values[1] || values[2] >= values[3]) {
            showAlertFeedback("Критичний поріг має бути вищим за відповідне попередження.", palette.thermal);
            return;
        }
        SharedPreferences.Editor editor = preferences.edit();
        for (int index = 0; index < values.length; index++) {
            editor.putInt(ThermalAlertService.THRESHOLD_KEYS[index], values[index]);
        }
        editor.apply();
        View focused = getCurrentFocus();
        if (focused != null) {
            ((InputMethodManager) getSystemService(INPUT_METHOD_SERVICE))
                    .hideSoftInputFromWindow(focused.getWindowToken(), 0);
            focused.clearFocus();
        }
        boolean enabled = alertSwitch.isChecked();
        if (enabled && Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] {Manifest.permission.POST_NOTIFICATIONS}, NOTIFICATION_PERMISSION);
            return;
        }
        applyAlertSetting(enabled);
    }

    @Override public void onRequestPermissionsResult(int code, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(code, permissions, results);
        if (code == NOTIFICATION_PERMISSION) {
            applyAlertSetting(results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED);
            if (results.length == 0 || results[0] != PackageManager.PERMISSION_GRANTED) {
                showAlertFeedback("Увімкніть сповіщення PC Pulse в налаштуваннях Android.", palette.thermal);
            }
        }
    }

    private void applyAlertSetting(boolean enable) {
        Intent alerts = new Intent(this, ThermalAlertService.class);
        if (enable && !getSystemService(NotificationManager.class).areNotificationsEnabled()) {
            showAlertFeedback("Увімкніть сповіщення PC Pulse в налаштуваннях Android.", palette.thermal);
            return;
        }
        preferences.edit().putBoolean(ThermalAlertService.ENABLED, enable).apply();
        if (alertSwitch != null) {
            alertSwitch.setChecked(enable);
        }
        try {
            if (enable) {
                startForegroundService(alerts);
            } else if (!enable) {
                stopService(alerts);
            }
        } catch (IllegalStateException | SecurityException error) {
            preferences.edit().putBoolean(ThermalAlertService.ENABLED, false).apply();
            if (alertSwitch != null) {
                alertSwitch.setChecked(false);
            }
            showAlertFeedback("Android не запустив фоновий моніторинг.", palette.thermal);
            return;
        }
        showAlertFeedback(enable ? "Фоновий моніторинг увімкнено." : "Пороги збережено. Моніторинг вимкнено.", palette.accent);
    }

    private void showAlertFeedback(String text, int color) {
        if (alertFeedback != null) {
            alertFeedback.setText(text);
            alertFeedback.setTextColor(color);
            alertFeedback.setVisibility(View.VISIBLE);
        }
    }

    private void styleSwitch(Switch control) {
        int[][] states = {{android.R.attr.state_checked}, {}};
        control.setThumbTintList(new ColorStateList(states, new int[] {palette.accent, palette.muted}));
        control.setTrackTintList(new ColorStateList(states, new int[] {palette.primary, palette.border}));
    }

    private void openDashboard() {
        setup = false;
        render();
        restartPolling();
    }

    private void renderDashboard() {
        hasData = false;
        lastUpdate = null;
        LinearLayout summary = column();
        summary.setPadding(dp(20), dp(10), dp(20), dp(12));
        hostView = label(host == null ? pc.address() : host, 28, palette.text, true);
        hostView.setMaxLines(2);
        hostView.setEllipsize(TextUtils.TruncateAt.END);
        add(summary, hostView, 0);
        statusView = label("Підключення…", 14, palette.muted, false);
        statusView.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        LinearLayout connectionStatus = row();
        connectionStatus.addView(statusView, weighted());
        reconnect = button("Повторити", false, this::restartPolling);
        connectionStatus.addView(reconnect, new LinearLayout.LayoutParams(WRAP, WRAP));
        reconnect.setVisibility(View.GONE);
        add(summary, connectionStatus, 4);
        root.addView(summary, new LinearLayout.LayoutParams(MATCH, WRAP));

        LinearLayout tabs = row();
        tabs.setPadding(dp(20), 0, dp(20), dp(10));
        addTabs(tabs, "Огляд", !processes, () -> selectTab(false), "Процеси", processes, () -> selectTab(true));
        root.addView(tabs, new LinearLayout.LayoutParams(MATCH, WRAP));
        if (processes) {
            renderProcessList();
        } else {
            renderOverview();
        }
    }

    private void selectTab(boolean requestedProcesses) {
        if (processes == requestedProcesses) {
            return;
        }
        View focused = getCurrentFocus();
        if (focused != null) {
            ((InputMethodManager) getSystemService(INPUT_METHOD_SERVICE))
                    .hideSoftInputFromWindow(focused.getWindowToken(), 0);
        }
        processes = requestedProcesses;
        render();
        restartPolling();
    }

    private void renderOverview() {
        ScrollView scroll = new ScrollView(this);
        scroll.setClipToPadding(false);
        root.addView(scroll, new LinearLayout.LayoutParams(MATCH, 0, 1));
        LinearLayout content = column();
        content.setPadding(dp(20), dp(4), dp(20), dp(24));
        scroll.addView(content, new ScrollView.LayoutParams(MATCH, WRAP));

        LinearLayout cards = row();
        cpu = new MetricCard("Процесор", "Завантаження CPU", palette.accent);
        memory = new MetricCard("Пам’ять", "Використано RAM", palette.memory);
        LinearLayout.LayoutParams left = new LinearLayout.LayoutParams(0, MATCH, 1);
        left.rightMargin = dp(8);
        cards.addView(cpu.container, left);
        cards.addView(memory.container, new LinearLayout.LayoutParams(0, MATCH, 1));
        add(content, cards, 0);
        temperature = new MetricCard("Температура CPU", "Очікування даних", palette.thermal);
        add(content, temperature.container, 10);
        gpuContainer = column();
        add(content, gpuContainer, 10);
        add(gpuContainer, label("Графіка", 18, palette.text, true), 8);
        add(gpuContainer, label("Очікування даних", 14, palette.muted, false), 6);

        coolingCard = card();
        add(content, coolingCard, 16);
        add(coolingCard, label("Охолодження", 18, palette.text, true), 0);
        coolingReading = label("", 14, palette.muted, false);
        add(coolingCard, coolingReading, 6);
        coolingButtons = row();
        add(coolingCard, coolingButtons, 10);
        coolingFeedback = label("", 13, palette.muted, false);
        coolingFeedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        coolingFeedback.setVisibility(View.GONE);
        add(coolingCard, coolingFeedback, 7);
        coolingCard.setVisibility(View.GONE);
        displayedCoolingMode = null;

        add(content, label("Температура обладнання", 18, palette.text, true), 18);
        diskTemperatures = componentCard(content, "Накопичувачі");
        memoryTemperatures = componentCard(content, "Модулі RAM");
    }

    private TextView componentCard(LinearLayout content, String name) {
        LinearLayout component = card();
        add(component, label(name, 16, palette.text, true), 0);
        TextView values = label("Зчитування датчиків…", 14, palette.muted, false);
        add(component, values, 8);
        add(content, component, 8);
        return values;
    }

    private void updateOverview(JSONObject json) {
        host = json.optString("host", pc.address());
        hostView.setText(host);
        double cpuPercent = number(json, "cpuPercent");
        cpu.update(percent(cpuPercent), Double.isNaN(cpuPercent) ? "Очікування другого виміру" : "Завантаження CPU", cpuPercent);

        JSONObject ram = json.optJSONObject("memory");
        long total = ram == null ? 0 : ram.optLong("totalBytes");
        long used = ram == null ? 0 : ram.optLong("usedBytes");
        double memoryPercent = total <= 0 ? Double.NaN : used * 100d / total;
        memory.update(percent(memoryPercent), total <= 0 ? "Дані недоступні" :
                String.format(UK, "%.1f / %.1f ГіБ", used / 1073741824d, total / 1073741824d), memoryPercent);

        boolean hardwareEnabled = json.optBoolean("sensorsEnabled");
        double cpuTemperature = number(json, "cpuTemperatureC");
        String cpuTemperatureDetail = "CPU Package або ядра";
        if ("gigabyte_control_center".equals(json.optString("cpuTemperatureSource"))) {
            cpuTemperatureDetail = "Датчик CPU · Control Center";
        }
        if (!hardwareEnabled) {
            cpuTemperatureDetail = "Сенсори вимкнено";
        } else if (Double.isNaN(cpuTemperature)) {
            switch (json.optString("cpuTemperatureStatus")) {
                case "driver_required":
                    cpuTemperatureDetail = "Потрібен драйвер PawnIO";
                    break;
                case "administrator_required":
                    cpuTemperatureDetail = "Запустіть агент від адміністратора";
                    break;
                default:
                    cpuTemperatureDetail = "Показник недоступний";
            }
        }
        temperature.update(degrees(cpuTemperature), cpuTemperatureDetail, Double.NaN);
        updateCooling(json.optJSONObject("cooling"));
        JSONObject components = json.optJSONObject("componentTemperatures");
        updateComponents(diskTemperatures, components, "disks", hardwareEnabled, json.has("componentTemperatures"));
        updateComponents(memoryTemperatures, components, "memory", hardwareEnabled, json.has("componentTemperatures"));

        JSONArray cards = json.optJSONArray("gpus");
        int count = cards == null ? 0 : cards.length();
        if (count == 0) {
            while (gpuContainer.getChildCount() > 2) {
                gpuContainer.removeViewAt(gpuContainer.getChildCount() - 1);
            }
            TextView placeholder = (TextView) gpuContainer.getChildAt(1);
            placeholder.setVisibility(View.VISIBLE);
            placeholder.setText(hardwareEnabled ? "Показники GPU недоступні" : "Показники GPU вимкнено");
            return;
        }
        gpuContainer.getChildAt(1).setVisibility(View.GONE);
        while (gpuContainer.getChildCount() > count + 2) {
            gpuContainer.removeViewAt(gpuContainer.getChildCount() - 1);
        }
        for (int i = 0; i < count; i++) {
            LinearLayout card;
            if (i + 2 >= gpuContainer.getChildCount()) {
                card = card();
                add(card, label("", 15, palette.text, true), 0);
                LinearLayout metrics = row();
                metrics.addView(metricColumn("Завантаження", palette.accent), weighted());
                metrics.addView(metricColumn("Температура", palette.thermal), weighted());
                add(card, metrics, 9);
                add(gpuContainer, card, 8);
            } else {
                card = (LinearLayout) gpuContainer.getChildAt(i + 2);
            }
            JSONObject gpu = cards.optJSONObject(i);
            if (gpu != null) {
                ((TextView) card.getChildAt(0)).setText(gpu.optString("name", "GPU"));
                LinearLayout metrics = (LinearLayout) card.getChildAt(1);
                setText((TextView) ((LinearLayout) metrics.getChildAt(0)).getChildAt(0), percent(number(gpu, "loadPercent")));
                setText((TextView) ((LinearLayout) metrics.getChildAt(1)).getChildAt(0), degrees(number(gpu, "temperatureC")));
            }
        }
    }

    private LinearLayout metricColumn(String name, int accent) {
        LinearLayout column = column();
        add(column, label("—", 28, accent, true), 0);
        add(column, label(name, 12, palette.muted, false), 4);
        return column;
    }

    private void updateComponents(TextView view, JSONObject data, String key, boolean enabled, boolean compatible) {
        if (data == null) {
            setText(view, !enabled ? "Сенсори вимкнено"
                    : compatible ? "Зчитування датчиків…" : "Оновіть PC Pulse на Windows");
            return;
        }
        JSONArray entries = data.optJSONArray(key);
        if (entries == null || entries.length() == 0) {
            setText(view, "Драйвер не надав показників");
            return;
        }
        List<String> lines = new ArrayList<>(entries.length());
        for (int i = 0; i < entries.length(); i++) {
            JSONObject item = entries.optJSONObject(i);
            if (item != null) {
                lines.add(item.optString("name") + "   ·   " + degrees(number(item, "temperatureC")));
            }
        }
        setText(view, TextUtils.join("\n", lines));
    }

    private void updateCooling(JSONObject result) {
        coolingCard.setVisibility(result == null ? View.GONE : View.VISIBLE);
        if (result == null) {
            return;
        }
        String mode = result.optString("mode");
        String name;
        switch (mode) {
            case "automatic":
                name = "Автоматичний режим";
                break;
            case "maximum":
                name = "Максимальні оберти";
                break;
            case "custom":
                name = "Власний профіль Control Center";
                break;
            default:
                name = "Режим недоступний";
        }
        setText(coolingReading, name + "\nCPU " + rpm(result, "cpuFanRpm") + "  ·  GPU " + rpm(result, "gpuFanRpm"));
        boolean canControl = result.optBoolean("canControl");
        if (!mode.equals(displayedCoolingMode) || canControl != displayedCanControl) {
            displayedCoolingMode = mode;
            displayedCanControl = canControl;
            coolingButtons.removeAllViews();
            coolingButtons.setVisibility(canControl ? View.VISIBLE : View.GONE);
            if (canControl) {
                addTabs(coolingButtons, "Авто", "automatic".equals(mode), () -> changeCooling(false),
                        "Максимум", "maximum".equals(mode), () -> changeCooling(true));
            }
        }
        setCoolingButtonsEnabled(true);
    }

    private void changeCooling(boolean maximum) {
        String requested = maximum ? "maximum" : "automatic";
        if (requested.equals(displayedCoolingMode)) {
            return;
        }
        stopPolling();
        final int ticket = generation;
        final PcConnection connection = pc;
        showCoolingFeedback("Зміна режиму…", palette.muted);
        setCoolingButtonsEnabled(false);
        request = network.submit(() -> {
            try {
                JSONObject response = connection.setCoolingMode(maximum);
                handler.post(() -> {
                    if (generation != ticket) {
                        return;
                    }
                    updateCooling(response);
                    setCoolingButtonsEnabled(true);
                    showCoolingFeedback(maximum ? "Максимальні оберти увімкнено." : "Автоматичний режим увімкнено.", palette.accent);
                    restartPolling();
                });
            } catch (Exception error) {
                handler.post(() -> {
                    if (generation != ticket) {
                        return;
                    }
                    setCoolingButtonsEnabled(true);
                    showCoolingFeedback(error instanceof PcConnection.ConflictException
                            ? "Control Center не підтвердив режим."
                            : "Не вдалося зв’язатися з агентом.", palette.thermal);
                    restartPolling();
                });
            }
        });
    }

    private void setCoolingButtonsEnabled(boolean enabled) {
        for (int index = 0; index < coolingButtons.getChildCount(); index++) {
            coolingButtons.getChildAt(index).setEnabled(enabled);
        }
    }

    private void showCoolingFeedback(String message, int color) {
        coolingFeedback.setVisibility(View.VISIBLE);
        coolingFeedback.setTextColor(color);
        setText(coolingFeedback, message);
    }

    private static String rpm(JSONObject cooling, String key) {
        return cooling.isNull(key) ? "—" : cooling.optInt(key) + " об/хв";
    }

    private void renderProcessList() {
        LinearLayout content = column();
        content.setPadding(dp(20), dp(2), dp(20), dp(10));
        root.addView(content, new LinearLayout.LayoutParams(MATCH, 0, 1));

        EditText filter = input("Пошук за назвою або PID");
        filter.setContentDescription("Пошук за назвою або PID");
        filter.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        filter.setSingleLine(true);
        filter.setText(search);
        add(content, filter, 0);
        LinearLayout sort = row();
        add(content, sort, 8);
        showSortButtons(sort);
        processSummary = label("Очікування списку процесів", 13, palette.muted, false);
        add(content, processSummary, 8);

        ListView list = new ListView(this);
        processList = list;
        list.setDivider(null);
        list.setItemsCanFocus(false);
        LinearLayout.LayoutParams listParams = new LinearLayout.LayoutParams(MATCH, 0, 1);
        listParams.topMargin = dp(4);
        content.addView(list, listParams);
        processAdapter = new ProcessAdapter();
        list.setAdapter(processAdapter);
        allProcesses = Collections.emptyList();
        filter.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                search = s.toString();
                applyProcessFilter();
                list.setSelection(0);
            }
            @Override public void afterTextChanged(Editable s) {}
        });
    }

    private void showSortButtons(LinearLayout sort) {
        sort.removeAllViews();
        addTabs(sort, "За CPU", !sortByRam, () -> changeSort(sort, false),
                "За пам’яттю", sortByRam, () -> changeSort(sort, true));
    }

    private void changeSort(LinearLayout sort, boolean ram) {
        if (sortByRam != ram) {
            sortByRam = ram;
            showSortButtons(sort);
            applyProcessFilter();
            processList.setSelection(0);
        }
    }

    private void applyProcessFilter() {
        if (!hasData) {
            return;
        }
        String query = search.trim().toLowerCase(UK);
        List<ProcessInfo> visible = new ArrayList<>();
        for (ProcessInfo process : allProcesses) {
            if (query.isEmpty() || process.name.toLowerCase(UK).contains(query)
                    || Integer.toString(process.pid).contains(query)) {
                visible.add(process);
            }
        }
        Comparator<ProcessInfo> byRam = Comparator.comparingLong((ProcessInfo item) -> item.bytes).reversed();
        Comparator<ProcessInfo> byCpu = Comparator.comparingDouble((ProcessInfo item) ->
                Double.isNaN(item.cpu) ? -1 : item.cpu).reversed();
        visible.sort((sortByRam ? byRam : byCpu)
                .thenComparingInt(item -> item.pid));
        String count = query.isEmpty()
                ? "Процесів: " + visible.size()
                : "Знайдено: " + visible.size() + " із " + allProcesses.size();
        String sort = sortByRam ? "Пам’ять" : "CPU";
        String sampled = Double.isFinite(processWindow)
                ? String.format(UK, " · CPU за %.1f с", processWindow) : "";
        processSummary.setText(count + " · " + sort + sampled);
        processAdapter.submit(visible);
    }

    private void restartPolling() {
        stopPolling();
        failures = 0;
        if (resumed && pc != null && !setup) {
            handler.post(poll);
        }
    }

    private void stopPolling() {
        generation++;
        handler.removeCallbacks(poll);
        if (request != null) {
            request.cancel(true);
            request = null;
        }
    }

    private void requestSnapshot() {
        if (!resumed || setup || pc == null) {
            return;
        }
        final int ticket = generation;
        final boolean processTab = processes;
        final boolean readSensors = sensors;
        final PcConnection endpoint = pc;
        request = network.submit(() -> {
            try {
                JSONObject json = endpoint.fetch(processTab, readSensors);
                List<ProcessInfo> entries = processTab ? readProcesses(json) : null;
                String updatedAt = localTime(json.optString("sampledAt"));
                handler.post(() -> {
                    if (ticket != generation) {
                        return;
                    }
                    failures = 0;
                    hasData = true;
                    lastUpdate = updatedAt;
                    reconnect.setVisibility(View.GONE);
                    if (processTab) {
                        allProcesses = entries;
                        processWindow = number(json, "cpuWindowSeconds");
                        applyProcessFilter();
                    } else {
                        updateOverview(json);
                    }
                    setStatus("Оновлено о " + updatedAt, palette.accent);
                    handler.postDelayed(poll, 5_000);
                });
            } catch (Exception error) {
                handler.post(() -> {
                    if (ticket != generation) {
                        return;
                    }
                    failures = Math.min(4, failures + 1);
                    long retryDelay = Math.min(15_000L, 2_000L << (failures - 1));
                    String message = "ПК недоступний. Перевірте мережу й агент.";
                    if (error instanceof PcConnection.RejectedException) {
                        message = "Ключ відхилено. Вставте актуальний код ПК.";
                        retryDelay = 30_000L;
                    } else if (error instanceof SSLException) {
                        message = "Не вдалося підтвердити сертифікат ПК. Перевірте код і час.";
                        retryDelay = 30_000L;
                    }
                    setStatus(message + (lastUpdate == null ? "" : " Останні дані о " + lastUpdate + "."), palette.thermal);
                    reconnect.setVisibility(View.VISIBLE);
                    handler.postDelayed(poll, retryDelay);
                });
            }
        });
    }

    private static List<ProcessInfo> readProcesses(JSONObject json) throws Exception {
        JSONArray items = json.getJSONArray("processes");
        List<ProcessInfo> result = new ArrayList<>(items.length());
        for (int i = 0; i < items.length(); i++) {
            JSONObject item = items.getJSONObject(i);
            result.add(new ProcessInfo(item.getInt("pid"), item.getString("name"),
                    item.getLong("workingSetBytes"), number(item, "cpuPercent")));
        }
        return result;
    }

    private static String localTime(String timestamp) {
        try {
            return OffsetDateTime.parse(timestamp).atZoneSameInstant(ZoneId.systemDefault()).toLocalTime().format(CLOCK);
        } catch (DateTimeParseException invalid) {
            return LocalTime.now().format(CLOCK);
        }
    }

    private void setStatus(String value, int color) {
        statusView.setTextColor(color);
        setText(statusView, value);
    }

    private static double number(JSONObject json, String name) {
        return json.isNull(name) ? Double.NaN : json.optDouble(name);
    }

    private static String percent(double value) {
        return Double.isNaN(value) ? "—" : String.format(UK, "%.0f%%", value);
    }

    private static String degrees(double value) {
        return Double.isNaN(value) ? "—" : String.format(UK, "%.0f °C", value);
    }

    private LinearLayout column() {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        return layout;
    }

    private LinearLayout row() {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.HORIZONTAL);
        layout.setGravity(Gravity.CENTER_VERTICAL);
        return layout;
    }

    private LinearLayout card() {
        LinearLayout layout = column();
        layout.setPadding(dp(14), dp(12), dp(14), dp(12));
        layout.setBackground(surface(palette.panel, palette.border, 14));
        return layout;
    }

    private LinearLayout.LayoutParams weighted() {
        return new LinearLayout.LayoutParams(0, WRAP, 1);
    }

    private void add(LinearLayout parent, View view, int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(MATCH, WRAP);
        params.topMargin = dp(top);
        parent.addView(view, params);
    }

    private void addTabs(LinearLayout row, String leftText, boolean leftActive, Runnable leftAction,
                         String rightText, boolean rightActive, Runnable rightAction) {
        LinearLayout.LayoutParams left = weighted();
        left.rightMargin = dp(8);
        row.addView(button(leftText, leftActive, leftAction), left);
        row.addView(button(rightText, rightActive, rightAction), weighted());
    }

    private TextView label(String content, int size, int color, boolean strong) {
        TextView text = new TextView(this);
        text.setText(content);
        text.setTextSize(size);
        text.setTextColor(color);
        text.setIncludeFontPadding(false);
        text.setLineSpacing(dp(2), 1f);
        if (strong) {
            text.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        }
        return text;
    }

    private TextView button(String caption, boolean primary, Runnable action) {
        TextView control = label(caption, 14, primary ? 0xffffffff : palette.text, true);
        control.setGravity(Gravity.CENTER);
        control.setMinHeight(dp(48));
        control.setPadding(dp(10), dp(7), dp(10), dp(7));
        control.setBackground(new RippleDrawable(ColorStateList.valueOf(0x3089abff),
                surface(primary ? palette.primary : palette.field, primary ? 0xff4b8acc : palette.border, 8), null));
        control.setFocusable(true);
        control.setOnClickListener(view -> action.run());
        control.setAccessibilityDelegate(new View.AccessibilityDelegate() {
            @Override public void onInitializeAccessibilityNodeInfo(View host,
                    android.view.accessibility.AccessibilityNodeInfo info) {
                super.onInitializeAccessibilityNodeInfo(host, info);
                info.setClassName(android.widget.Button.class.getName());
            }
        });
        return control;
    }

    private EditText input(String hint) {
        EditText input = new EditText(this);
        input.setHint(hint);
        input.setHintTextColor(palette.muted);
        input.setTextColor(palette.text);
        input.setTextSize(15);
        input.setMinHeight(dp(48));
        input.setPadding(dp(12), dp(9), dp(12), dp(9));
        input.setBackground(surface(palette.field, palette.border, 8));
        return input;
    }

    private GradientDrawable surface(int color, int stroke, int radius) {
        GradientDrawable background = new GradientDrawable();
        background.setColor(color);
        background.setCornerRadius(dp(radius));
        background.setStroke(dp(1), stroke);
        return background;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static void setText(TextView view, String value) {
        if (!TextUtils.equals(view.getText(), value)) {
            view.setText(value);
        }
    }

    private final class MetricCard {
        final LinearLayout container;
        final TextView value;
        final TextView subtitle;
        final ProgressBar bar;

        MetricCard(String name, String detail, int accent) {
            container = card();
            GradientDrawable face = new GradientDrawable(GradientDrawable.Orientation.TL_BR, new int[] {
                    blend(palette.panel, accent, day ? 0.055 : 0.13), palette.panel
            });
            face.setCornerRadius(dp(16));
            face.setStroke(dp(1), blend(palette.border, accent, 0.22));
            container.setBackground(face);
            add(container, label(name, 14, palette.muted, true), 0);
            value = label("—", 39, accent, true);
            add(container, value, 8);
            subtitle = label(detail, 13, palette.muted, false);
            add(container, subtitle, 6);
            bar = new ProgressBar(MainActivity.this, null, android.R.attr.progressBarStyleHorizontal);
            bar.setMax(100);
            bar.setProgressTintList(ColorStateList.valueOf(accent));
            bar.setProgressBackgroundTintList(ColorStateList.valueOf(palette.border));
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(MATCH, dp(7));
            params.topMargin = dp(11);
            container.addView(bar, params);
            bar.setVisibility(View.GONE);
        }

        void update(String measurement, String detail, double progress) {
            setText(value, measurement);
            setText(subtitle, detail);
            bar.setVisibility(Double.isNaN(progress) ? View.GONE : View.VISIBLE);
            if (!Double.isNaN(progress)) {
                bar.setProgress((int) Math.round(progress));
            }
        }
    }

    private final class ProcessAdapter extends BaseAdapter {
        private List<ProcessInfo> entries = Collections.emptyList();

        void submit(List<ProcessInfo> values) {
            entries = values;
            notifyDataSetChanged();
        }

        @Override public int getCount() { return entries.size(); }
        @Override public ProcessInfo getItem(int index) { return entries.get(index); }
        @Override public long getItemId(int index) { return getItem(index).pid; }
        @Override public boolean hasStableIds() { return true; }
        @Override public boolean isEnabled(int index) { return false; }

        @Override public View getView(int index, View recycled, ViewGroup parent) {
            LinearLayout wrapper = recycled == null ? column() : (LinearLayout) recycled;
            if (recycled == null) {
                LinearLayout line = row();
                line.setPadding(dp(12), dp(9), dp(12), dp(9));
                line.setBackground(surface(palette.panel, palette.border, 12));
                LinearLayout description = column();
                TextView name = label("", 14, palette.text, true);
                name.setMaxLines(2);
                name.setEllipsize(TextUtils.TruncateAt.END);
                add(description, name, 0);
                add(description, label("", 12, palette.muted, false), 4);
                line.addView(description, weighted());
                TextView load = label("", 13, palette.accent, true);
                load.setGravity(Gravity.END);
                load.setPadding(dp(5), 0, dp(5), 0);
                line.addView(load, new LinearLayout.LayoutParams(dp(55), WRAP));
                TextView ram = label("", 13, palette.memory, false);
                ram.setGravity(Gravity.END);
                line.addView(ram, new LinearLayout.LayoutParams(dp(77), WRAP));
                add(wrapper, line, 5);
                wrapper.setTag(new ProcessViews(name, (TextView) description.getChildAt(1), load, ram));
            }
            ProcessViews views = (ProcessViews) wrapper.getTag();
            ProcessInfo item = getItem(index);
            views.cpu.setTextColor(sortByRam ? palette.muted : palette.accent);
            views.ram.setTextColor(sortByRam ? palette.memory : palette.muted);
            setText(views.name, item.name);
            setText(views.pid, "PID " + item.pid);
            setText(views.cpu, Double.isNaN(item.cpu) ? "—" : String.format(UK, "%.1f%%", item.cpu));
            setText(views.ram, item.bytes >= 1073741824L
                    ? String.format(UK, "%.1f ГіБ", item.bytes / 1073741824d)
                    : String.format(UK, "%.0f МіБ", item.bytes / 1048576d));
            return wrapper;
        }
    }

    private static int blend(int first, int second, double amount) {
        int red = (int) (((first >> 16) & 255) * (1 - amount) + ((second >> 16) & 255) * amount);
        int green = (int) (((first >> 8) & 255) * (1 - amount) + ((second >> 8) & 255) * amount);
        int blue = (int) ((first & 255) * (1 - amount) + (second & 255) * amount);
        return 0xff000000 | red << 16 | green << 8 | blue;
    }

    private static final class ProcessInfo {
        final int pid;
        final String name;
        final long bytes;
        final double cpu;

        ProcessInfo(int pid, String name, long bytes, double cpu) {
            this.pid = pid;
            this.name = name;
            this.bytes = bytes;
            this.cpu = cpu;
        }
    }

    private static final class ProcessViews {
        final TextView name;
        final TextView pid;
        final TextView cpu;
        final TextView ram;

        ProcessViews(TextView name, TextView pid, TextView cpu, TextView ram) {
            this.name = name;
            this.pid = pid;
            this.cpu = cpu;
            this.ram = ram;
        }
    }
}
