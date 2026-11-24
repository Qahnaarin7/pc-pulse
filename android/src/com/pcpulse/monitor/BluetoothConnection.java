package com.pcpulse.monitor;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothSocket;
import android.content.Context;
import android.os.SystemClock;

import org.json.JSONObject;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

final class BluetoothConnection {
    private static final UUID SERVICE = UUID.fromString("c0c7c487-1f28-47c5-88f2-72af742ecb1c");
    private static final ScheduledExecutorService TIMEOUTS = Executors.newSingleThreadScheduledExecutor();
    private static final Map<String, Channel> SHARED = new HashMap<>();
    private final String address;
    private final String key;
    private Channel channel;

    BluetoothConnection(String address, String key) {
        if (!BluetoothAdapter.checkBluetoothAddress(address)) {
            throw new IllegalArgumentException("Адреса Bluetooth пошкоджена.");
        }
        this.address = address;
        if (!key.matches("[a-zA-Z0-9_-]{43}")) {
            throw new IllegalArgumentException("Ключ Bluetooth пошкоджений.");
        }
        this.key = key;
    }

    JSONObject fetch(Context context, String path, String method) throws Exception {
        Channel active;
        synchronized (SHARED) {
            if (channel == null) {
                channel = SHARED.computeIfAbsent(address + key, ignored -> new Channel(address, key));
                channel.users++;
            }
            active = channel;
        }
        return active.fetch(context, path, method);
    }

    void close() {
        Channel inactive = null;
        synchronized (SHARED) {
            if (channel != null && --channel.users == 0) {
                SHARED.remove(address + key);
                inactive = channel;
            }
            channel = null;
        }
        if (inactive != null) {
            inactive.close();
        }
    }

    private static final class Channel {
        private final String address;
        private final SecureEnvelope codec;
        private volatile BluetoothSocket socket;
        private long nextAttempt;
        private int users;

        Channel(String address, String key) {
            this.address = address;
            codec = new SecureEnvelope(key);
        }

        synchronized JSONObject fetch(Context context, String path, String method) throws Exception {
            if (context == null || SystemClock.elapsedRealtime() < nextAttempt) {
                throw new IOException("Bluetooth з’єднання поки недоступне.");
            }
            BluetoothSocket current = socket;
            int seconds = 6;
            try {
                if (current == null) {
                    BluetoothManager manager = context.getSystemService(BluetoothManager.class);
                    BluetoothAdapter radio = manager == null ? null : manager.getAdapter();
                    if (radio == null || !radio.isEnabled()) {
                        throw new IOException("Увімкніть Bluetooth на телефоні.");
                    }
                    BluetoothDevice device = radio.getRemoteDevice(address);
                    seconds = device.getBondState() == BluetoothDevice.BOND_BONDED ? 6 : 25;
                    current = device.createRfcommSocketToServiceRecord(SERVICE);
                    socket = current;
                }
                BluetoothSocket active = current;
                ScheduledFuture<?> timeout = TIMEOUTS.schedule(() -> closeSocket(active), seconds, TimeUnit.SECONDS);
                try {
                    if (!current.isConnected()) {
                        current.connect();
                    }
                    SecureEnvelope.Exchange exchange = codec.request(path, method);
                    DataOutputStream output = new DataOutputStream(current.getOutputStream());
                    output.writeInt(exchange.body.length);
                    output.write(exchange.body);
                    output.flush();
                    DataInputStream input = new DataInputStream(current.getInputStream());
                    int size = input.readInt();
                    if (size <= 0 || size > 720_000) {
                        throw new IOException("Неприпустимий розмір відповіді Bluetooth.");
                    }
                    byte[] response = new byte[size];
                    input.readFully(response);
                    return codec.reply(exchange, new JSONObject(new String(response, StandardCharsets.UTF_8)));
                } finally {
                    timeout.cancel(false);
                }
            } catch (PcConnection.ConflictException | PcConnection.RejectedException | PcConnection.ServerResponseException result) {
                throw result;
            } catch (Exception error) {
                nextAttempt = SystemClock.elapsedRealtime() + 15_000;
                close();
                if (error instanceof IOException) {
                    throw error;
                }
                throw new IOException("Bluetooth не підтвердив підключення.", error);
            }
        }

        void close() {
            BluetoothSocket previous = socket;
            socket = null;
            if (previous != null) {
                closeSocket(previous);
            }
        }
    }

    private static void closeSocket(BluetoothSocket current) {
        try { current.close(); }
        catch (IOException ignored) { }
    }
}
