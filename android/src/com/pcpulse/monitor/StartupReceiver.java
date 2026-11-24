package com.pcpulse.monitor;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.app.NotificationManager;

public final class StartupReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent event) {
        String action = event.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action) && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)) {
            return;
        }
        SharedPreferences preferences = context.getSharedPreferences("pc_pulse", Context.MODE_PRIVATE);
        if (preferences.getBoolean(ThermalAlertService.ENABLED, false) &&
                !preferences.getString("pairing", "").isEmpty() &&
                context.getSystemService(NotificationManager.class).areNotificationsEnabled()) {
            try {
                context.startForegroundService(new Intent(context, ThermalAlertService.class));
            } catch (IllegalStateException | SecurityException ignored) {
                // Some devices only allow autostart after the user enables it in their Android settings.
            }
        }
    }
}
