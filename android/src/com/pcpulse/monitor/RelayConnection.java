package com.pcpulse.monitor;

import android.util.Base64;

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
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.UUID;

import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import javax.net.ssl.HttpsURLConnection;

final class RelayConnection {
    private static final int FLAGS = Base64.URL_SAFE | Base64.NO_WRAP | Base64.NO_PADDING;
    private static final int MAX_RESPONSE = 720_000;
    private static final SecureRandom RANDOM = new SecureRandom();
    private final String origin;
    private final String deviceId;
    private final String clientKey;
    private final SecretKeySpec dataKey;

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
        byte[] decoded = Base64.decode(encodedKey, FLAGS);
        if (decoded.length != 32) {
            throw new IllegalArgumentException("Ключ Cloudflare пошкоджений.");
        }
        dataKey = new SecretKeySpec(decoded, "AES");
    }

    JSONObject fetch(String path, String method) throws Exception {
        String id = UUID.randomUUID().toString().replace("-", "");
        JSONObject intent = new JSONObject()
                .put("method", method).put("path", path).put("at", System.currentTimeMillis());
        JSONObject envelope = new JSONObject().put("id", id)
                .put("data", seal(id, intent.toString().getBytes(StandardCharsets.UTF_8)));
        byte[] body = envelope.toString().getBytes(StandardCharsets.UTF_8);
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
                if (!id.equals(result.optString("id"))) {
                    throw new IOException("Relay response does not match the request");
                }
                JSONObject reply = new JSONObject(new String(open(id, result.getString("data")), StandardCharsets.UTF_8));
                consumed = true;
                return PcConnection.decodeReply(reply.getInt("status"), reply.getString("body"));
            }
        } catch (GeneralSecurityException | JSONException error) {
            throw new IOException("Unable to authenticate the relay response", error);
        } finally {
            if (!consumed) {
                connection.disconnect();
            }
        }
    }

    private String seal(String id, byte[] plaintext) throws GeneralSecurityException {
        byte[] nonce = new byte[12];
        RANDOM.nextBytes(nonce);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, dataKey, new GCMParameterSpec(128, nonce));
        cipher.updateAAD(associated("request", id));
        byte[] ciphertext = cipher.doFinal(plaintext);
        byte[] sealed = Arrays.copyOf(nonce, nonce.length + ciphertext.length);
        System.arraycopy(ciphertext, 0, sealed, nonce.length, ciphertext.length);
        return Base64.encodeToString(sealed, FLAGS);
    }

    private byte[] open(String id, String payload) throws GeneralSecurityException {
        byte[] sealed;
        try {
            sealed = Base64.decode(payload, FLAGS);
        } catch (IllegalArgumentException error) {
            throw new GeneralSecurityException("Invalid encoding", error);
        }
        if (sealed.length < 28) {
            throw new GeneralSecurityException("Incomplete encrypted response");
        }
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, dataKey, new GCMParameterSpec(128, sealed, 0, 12));
        cipher.updateAAD(associated("response", id));
        return cipher.doFinal(sealed, 12, sealed.length - 12);
    }

    private static byte[] associated(String direction, String id) {
        return ("pcpulse/relay:v1:" + direction + ":" + id).getBytes(StandardCharsets.US_ASCII);
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
