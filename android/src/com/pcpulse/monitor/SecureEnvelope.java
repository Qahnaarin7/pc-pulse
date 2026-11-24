package com.pcpulse.monitor;

import android.util.Base64;

import org.json.JSONObject;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.UUID;

import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

final class SecureEnvelope {
    private static final int FLAGS = Base64.URL_SAFE | Base64.NO_WRAP | Base64.NO_PADDING;
    private static final SecureRandom RANDOM = new SecureRandom();
    private final SecretKeySpec key;

    SecureEnvelope(String encoded) {
        if (!encoded.matches("[a-zA-Z0-9_-]{43}")) {
            throw new IllegalArgumentException("Ключ підключення пошкоджений.");
        }
        byte[] decoded = Base64.decode(encoded, FLAGS);
        if (decoded.length != 32) {
            throw new IllegalArgumentException("Ключ підключення пошкоджений.");
        }
        key = new SecretKeySpec(decoded, "AES");
    }

    Exchange request(String path, String method) throws Exception {
        String id = UUID.randomUUID().toString().replace("-", "");
        JSONObject intent = new JSONObject().put("method", method).put("path", path)
                .put("at", System.currentTimeMillis()).put("phone", true);
        byte[] nonce = new byte[12];
        RANDOM.nextBytes(nonce);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key, new GCMParameterSpec(128, nonce));
        cipher.updateAAD(associated("request", id));
        byte[] ciphertext = cipher.doFinal(intent.toString().getBytes(StandardCharsets.UTF_8));
        byte[] sealed = Arrays.copyOf(nonce, nonce.length + ciphertext.length);
        System.arraycopy(ciphertext, 0, sealed, nonce.length, ciphertext.length);
        JSONObject envelope = new JSONObject().put("id", id).put("data", Base64.encodeToString(sealed, FLAGS));
        return new Exchange(id, envelope.toString().getBytes(StandardCharsets.UTF_8));
    }

    JSONObject reply(Exchange exchange, JSONObject envelope) throws Exception {
        if (!exchange.id.equals(envelope.optString("id"))) {
            throw new IOException("Response does not match the request");
        }
        byte[] sealed;
        try {
            sealed = Base64.decode(envelope.getString("data"), FLAGS);
        } catch (IllegalArgumentException error) {
            throw new GeneralSecurityException("Invalid encoding", error);
        }
        if (sealed.length < 28) {
            throw new GeneralSecurityException("Incomplete encrypted response");
        }
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(128, sealed, 0, 12));
        cipher.updateAAD(associated("response", exchange.id));
        JSONObject reply = new JSONObject(new String(cipher.doFinal(sealed, 12, sealed.length - 12), StandardCharsets.UTF_8));
        return PcConnection.decodeReply(reply.getInt("status"), reply.getString("body"));
    }

    private static byte[] associated(String direction, String id) {
        return ("pcpulse/relay:v1:" + direction + ":" + id).getBytes(StandardCharsets.US_ASCII);
    }

    static final class Exchange {
        final String id;
        final byte[] body;

        Exchange(String id, byte[] body) {
            this.id = id;
            this.body = body;
        }
    }
}
