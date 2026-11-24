package com.pcpulse.monitor;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.URI;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import javax.net.ssl.HttpsURLConnection;

final class RelayConnection {
    private static final int MAX_RESPONSE = 720_000;
    private final String origin;
    private final String deviceId;
    private final String clientKey;
    private final SecureEnvelope codec;

    RelayConnection(JSONObject pairing) throws Exception {
        origin = pairing.getString("origin");
        deviceId = pairing.getString("deviceId");
        clientKey = pairing.getString("clientKey");
        String encodedKey = pairing.getString("dataKey");
        URI location = new URI(origin);
        if (!"https".equals(location.getScheme()) || location.getHost() == null || location.getPort() != -1 ||
                location.getUserInfo() != null || location.getQuery() != null || location.getFragment() != null ||
                !location.getPath().isEmpty() || !deviceId.matches("[a-zA-Z0-9_-]{22}") ||
                !clientKey.matches("[a-zA-Z0-9_-]{43}") || !encodedKey.matches("[a-zA-Z0-9_-]{43}")) {
            throw new IllegalArgumentException("Код Cloudflare пошкоджений.");
        }
        codec = new SecureEnvelope(encodedKey);
    }

    JSONObject fetch(String path, String method) throws Exception {
        SecureEnvelope.Exchange exchange = codec.request(path, method);
        byte[] body = exchange.body;
        URL endpoint = new URL(origin + "/v1/devices/" + deviceId + "/request");
        HttpsURLConnection connection = (HttpsURLConnection) endpoint.openConnection();
        connection.setInstanceFollowRedirects(false);
        connection.setUseCaches(false);
        connection.setConnectTimeout(3500);
        connection.setReadTimeout(12_000);
        connection.setRequestMethod("POST");
        connection.setRequestProperty("Authorization", "Bearer " + clientKey);
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        connection.setDoOutput(true);
        connection.setFixedLengthStreamingMode(body.length);
        boolean consumed = false;
        try {
            try (OutputStream output = connection.getOutputStream()) {
                output.write(body);
            }
            int status = connection.getResponseCode();
            if (status != 200) {
                throw new IOException("Cloudflare relay HTTP " + status);
            }
            try (InputStream input = connection.getInputStream()) {
                JSONObject result = read(input);
                consumed = true;
                return codec.reply(exchange, result);
            }
        } catch (GeneralSecurityException | JSONException error) {
            throw new IOException("Unable to authenticate the relay response", error);
        } finally {
            if (!consumed) {
                connection.disconnect();
            }
        }
    }

    private static JSONObject read(InputStream input) throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream(4096);
        byte[] buffer = new byte[4096];
        int size;
        while ((size = input.read(buffer)) != -1) {
            if (output.size() + size > MAX_RESPONSE) {
                throw new IOException("Cloudflare response exceeds its limit");
            }
            output.write(buffer, 0, size);
        }
        return new JSONObject(output.toString(StandardCharsets.UTF_8.name()));
    }
}
