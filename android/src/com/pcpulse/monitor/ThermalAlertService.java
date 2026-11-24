package com.pcpulse.monitor;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;
import android.os.SystemClock;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public final class ThermalAlertService extends Service {
    static final String ENABLED = "thermal_alerts";
    static final String[] THRESHOLD_KEYS = {"cpu_warning", "cpu_critical", "gpu_warning", "gpu_critical"};
    static final int[] DEFAULT_THRESHOLDS = {90, 95, 80, 85};
    static final int MIN_THRESHOLD = 30;
    static final int MAX_THRESHOLD = 110;
    private static final String MONITOR_CHANNEL = "thermal_monitor";
    private static final String WARNING_CHANNEL = "thermal_warning";
    private static final String CRITICAL_CHANNEL = "thermal_critical";
    private static final String STOP = "com.pcpulse.monitor.STOP_ALERTS";
    private static final int MONITOR_ID = 7100;
    private static final int CPU_ID = 7101;
    private static final int FIRST_GPU_ID = 7200;
    private static final long INTERVAL_SECONDS = 5;
    private static final double HYSTERESIS = 3;
    private final Map<Integer, Integer> levels = new HashMap<>();
    private final ScheduledExecutorService network = Executors.newSingleThreadScheduledExecutor();
    private SharedPreferences preferences;
    private NotificationManager notifications;
    private PcConnection pc;
    private String pairing;
    private boolean scheduled;
    private volatile String serviceStatus;
    private long nextAttempt;
    private int failures;
    private int[] thresholds;

    @Override public void onCreate() {
        super.onCreate();
        preferences = getSharedPreferences("pc_pulse", MODE_PRIVATE);
        notifications = getSystemService(NotificationManager.class);
        notifications.createNotificationChannel(new NotificationChannel(
                MONITOR_CHANNEL, "Фоновий моніторинг", NotificationManager.IMPORTANCE_LOW));
        notifications.createNotificationChannel(new NotificationChannel(
                WARNING_CHANNEL, "Температура · попередження", NotificationManager.IMPORTANCE_DEFAULT));
        notifications.createNotificationChannel(new NotificationChannel(
                CRITICAL_CHANNEL, "Температура · критично", NotificationManager.IMPORTANCE_HIGH));
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && STOP.equals(intent.getAction())) {
            preferences.edit().putBoolean(ENABLED, false).apply();
            stopForeground(STOP_FOREGROUND_REMOVE);
            stopSelf();
            return START_NOT_STICKY;
        }
        if (!preferences.getBoolean(ENABLED, false)) {
            stopSelf();
            return START_NOT_STICKY;
        }

        Notification active = monitoring(serviceStatus == null ? "Очікування показників ПК" : serviceStatus);
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(MONITOR_ID, active, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
        } else {
            startForeground(MONITOR_ID, active);
        }
        if (!scheduled) {
            scheduled = true;
            network.scheduleWithFixedDelay(this::poll, 0, INTERVAL_SECONDS, TimeUnit.SECONDS);
        }
        return START_STICKY;
    }

    @Override public IBinder onBind(Intent intent) {
        return null;
    }

    @Override public void onDestroy() {
        network.shutdownNow();
        super.onDestroy();
    }

    private void poll() {
        if (!preferences.getBoolean(ENABLED, false) || Thread.currentThread().isInterrupted()) {
            return;
        }

        String currentPairing = preferences.getString("pairing", "");
        int[] configured = loadThresholds(preferences);
        if (!currentPairing.equals(pairing) || !java.util.Arrays.equals(configured, thresholds)) {
            levels.clear();
            thresholds = configured;
            nextAttempt = 0;
            if (!currentPairing.equals(pairing)) {
                pc = null;
            }
        }
        if (SystemClock.elapsedRealtime() < nextAttempt) {
            return;
        }

        try {
            if (pc == null) {
                pc = PcConnection.fromCode(currentPairing);
                pairing = currentPairing;
            }
            pc.updateNetwork(this);
            JSONObject json = pc.temperatures();
            if (network.isShutdown()) {
                return;
            }
            failures = 0;
            double cpu = json.optDouble("cpuTemperatureC");
            boolean hasTemperature = evaluate(CPU_ID, "CPU", cpu, thresholds[0], thresholds[1]);
            JSONArray gpus = json.optJSONArray("gpus");
            if (gpus != null) {
                for (int index = 0; index < gpus.length(); index++) {
                    JSONObject gpu = gpus.optJSONObject(index);
                    if (gpu != null) {
                        hasTemperature |= evaluate(FIRST_GPU_ID + index, gpu.optString("name", "GPU"),
                                gpu.optDouble("temperatureC"), thresholds[2], thresholds[3]);
                    }
                }
            }
            updateStatus(hasTemperature ? "Перевірка датчиків кожні 5 с" : "Температурні датчики недоступні");
        } catch (Exception error) {
            if (!network.isShutdown()) {
                failures = Math.min(failures + 1, 3);
                nextAttempt = SystemClock.elapsedRealtime() + (5_000L << (failures - 1));
                updateStatus("ПК недоступний · очікуємо з’єднання");
            }
        }
    }

    private boolean evaluate(int id, String name, double temperature, int warning, int critical) {
        if (!Double.isFinite(temperature)) {
            return false;
        }
        int previous = levels.getOrDefault(id, 0);
        int level = classify(temperature, warning, critical, previous);
        levels.put(id, level);
        if (level > previous) {
            int threshold = level == 2 ? critical : warning;
            String title = level == 2 ? "Критична температура" : "Висока температура";
            String detail = String.format(Locale.forLanguageTag("uk-UA"), "%s · %.0f °C · поріг %d °C",
                    name, temperature, threshold);
            if (level == 2) {
                notifications.cancel(id * 2 + 1);
            }
            notifications.notify(id * 2 + level, new Notification.Builder(this, level == 2 ? CRITICAL_CHANNEL : WARNING_CHANNEL)
                    .setSmallIcon(R.drawable.ic_notification)
                    .setContentTitle(title)
                    .setContentText(detail)
                    .setStyle(new Notification.BigTextStyle().bigText(detail))
                    .setContentIntent(openApp())
                    .setAutoCancel(true)
                    .build());
        }
        return true;
    }

    static int classify(double temperature, int warning, int critical, int previous) {
        if (temperature >= critical || previous == 2 && temperature >= critical - HYSTERESIS) {
            return 2;
        }
        if (temperature >= warning || previous >= 1 && temperature >= warning - HYSTERESIS) {
            return 1;
        }
        return 0;
    }

    static int[] loadThresholds(SharedPreferences preferences) {
        int[] values = DEFAULT_THRESHOLDS.clone();
        for (int i = 0; i < values.length; i++) {
            values[i] = preferences.getInt(THRESHOLD_KEYS[i], values[i]);
        }
        return values;
    }

    private void updateStatus(String status) {
        if (!status.equals(serviceStatus)) {
            serviceStatus = status;
            notifications.notify(MONITOR_ID, monitoring(status));
        }
    }

    private Notification monitoring(String status) {
        Intent stop = new Intent(this, ThermalAlertService.class).setAction(STOP);
        PendingIntent cancel = PendingIntent.getService(this, 1, stop,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this, MONITOR_CHANNEL)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle("PC Pulse · температура")
                .setContentText(status)
                .setContentIntent(openApp())
                .setOnlyAlertOnce(true)
                .setOngoing(true)
                .addAction(new Notification.Action.Builder(null, "Зупинити", cancel).build())
                .build();
    }

    private PendingIntent openApp() {
        Intent launch = new Intent(this, MainActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        return PendingIntent.getActivity(this, 0, launch, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }
}
