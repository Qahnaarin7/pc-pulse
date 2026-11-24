package com.pcpulse.monitor;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.util.Base64;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.Proxy;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Locale;
import java.util.Objects;

import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLPeerUnverifiedException;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

final class PcConnection {
    private static final int BASE64 = Base64.URL_SAFE | Base64.NO_WRAP | Base64.NO_PADDING;
    private static final int MAX_RESPONSE = 2 * 1024 * 1024;
    private final String[] addresses;
    private volatile int preferred;
    private final RelayConnection relay;
    private volatile boolean mobile;
    private volatile boolean usingRelay;
    private Network activeNetwork;
    private final String token;
    private final byte[] pin;
    private SSLSocketFactory sockets;

    private PcConnection(String[] addresses, String token, byte[] pin) {
        this(addresses, token, pin, null);
    }

    private PcConnection(String[] addresses, String token, byte[] pin, RelayConnection relay) {
        this.addresses = addresses;
        this.token = token;
        this.pin = pin;
        this.relay = relay;
    }

    static PcConnection fromCode(String code) {
        if (code.startsWith("PCP3-")) {
            if (!code.matches("PCP3-[a-zA-Z0-9_-]{160,3900}")) {
                throw new IllegalArgumentException("Вставте весь код Cloudflare із Windows.");
            }
            try {
                JSONObject fields = new JSONObject(new String(Base64.decode(code.substring(5), BASE64), StandardCharsets.UTF_8));
                RelayConnection cloud = new RelayConnection(fields);
                String direct = fields.isNull("direct") ? "" : fields.getString("direct");
                if (direct.isEmpty()) {
                    return new PcConnection(new String[0], "", new byte[0], cloud);
                }
                if (!direct.startsWith("PCP1-") && !direct.startsWith("PCP2-")) {
                    throw new IllegalArgumentException();
                }
                PcConnection lan = fromCode(direct);
                return new PcConnection(lan.addresses, lan.token, lan.pin, cloud);
            } catch (Exception error) {
                throw new IllegalArgumentException("Код Cloudflare пошкоджений.", error);
            }
        }
        if (!code.matches("PCP[12]-[a-zA-Z0-9_-]{83,123}")) {
            throw new IllegalArgumentException("Вставте весь код PC Pulse із Windows.");
        }
        byte[] data = Base64.decode(code.substring(5), BASE64);
        if (code.startsWith("PCP1-") && data.length == 62) {
            return new PcConnection(new String[] {endpoint(data, 0)},
                    Base64.encodeToString(Arrays.copyOfRange(data, 38, 62), BASE64),
                    Arrays.copyOfRange(data, 6, 38));
        }
        if (!code.startsWith("PCP2-") || data.length < 62 || data.length > 92 || (data.length - 56) % 6 != 0) {
            throw new IllegalArgumentException("Код підключення пошкоджений.");
        }
        String[] addresses = new String[(data.length - 56) / 6];
        for (int i = 0; i < addresses.length; i++) {
            addresses[i] = endpoint(data, 56 + i * 6);
        }
        return new PcConnection(addresses, Base64.encodeToString(Arrays.copyOfRange(data, 32, 56), BASE64),
                Arrays.copyOfRange(data, 0, 32));
    }

    private static String endpoint(byte[] data, int offset) {
        int port = (Byte.toUnsignedInt(data[offset + 4]) << 8) + Byte.toUnsignedInt(data[offset + 5]);
        if (port == 0) {
            throw new IllegalArgumentException("Код підключення пошкоджений.");
        }
        return String.format(Locale.ROOT, "%d.%d.%d.%d:%d",
                Byte.toUnsignedInt(data[offset]), Byte.toUnsignedInt(data[offset + 1]),
                Byte.toUnsignedInt(data[offset + 2]), Byte.toUnsignedInt(data[offset + 3]), port);
    }

    String address() {
        return usingRelay || addresses.length == 0 ? "Cloudflare" : addresses[preferred];
    }

    boolean hasCloudflare() {
        return relay != null;
    }

    void updateNetwork(Context context) {
        if (relay == null) {
            return;
        }
        ConnectivityManager manager = context.getSystemService(ConnectivityManager.class);
        Network current = manager.getActiveNetwork();
        NetworkCapabilities active = current == null ? null : manager.getNetworkCapabilities(current);
        mobile = active != null && active.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) &&
                !active.hasTransport(NetworkCapabilities.TRANSPORT_WIFI);
        if (!Objects.equals(activeNetwork, current)) {
            activeNetwork = current;
            usingRelay = false;
        }
    }

    JSONObject fetch(boolean processes, boolean sensors) throws Exception {
        String path = processes ? "/v1/processes" : "/v1/overview?sensors=" + sensors;
        return fetch(path, "GET");
    }

    JSONObject temperatures() throws Exception {
        return fetch("/v1/temperatures", "GET");
    }

    JSONObject setCoolingMode(boolean maximum) throws Exception {
        return fetch("/v1/cooling/" + (maximum ? "maximum" : "automatic"), "POST");
    }

    JSONObject protectionStatus() throws Exception {
        return fetch("/v1/thermal-protection", "GET");
    }

    JSONObject saveProtection(boolean enabled, int cpuCritical, int gpuCritical) throws Exception {
        return fetch("/v1/thermal-protection?enabled=" + enabled
                + "&cpuCritical=" + cpuCritical + "&gpuCritical=" + gpuCritical, "POST");
    }

    private JSONObject fetch(String path, String method) throws Exception {
        int first = preferred;
        IOException problem = null;
        boolean triedRelay = relay != null && (mobile || usingRelay || addresses.length == 0);
        if (triedRelay) {
            try {
                JSONObject response = relay.fetch(path, method);
                usingRelay = true;
                return response;
            } catch (RejectedException | ConflictException | ServerResponseException error) {
                throw error;
            } catch (IOException error) {
                problem = error;
            }
        }
        for (int i = 0; i < addresses.length; i++) {
            if (Thread.currentThread().isInterrupted()) {
                throw new java.io.InterruptedIOException();
            }
            int candidate = (first + i) % addresses.length;
            try {
                JSONObject response = fetch(addresses[candidate], path, method);
                preferred = candidate;
                usingRelay = false;
                return response;
            } catch (RejectedException | ConflictException | ServerResponseException error) {
                throw error;
            } catch (IOException error) {
                if (problem == null || error instanceof javax.net.ssl.SSLException) {
                    problem = error;
                }
            }
        }
        if (relay != null && !triedRelay) {
            JSONObject response = relay.fetch(path, method);
            usingRelay = true;
            return response;
        }
        throw problem == null ? new IOException("No available PC address") : problem;
    }

    static JSONObject decodeReply(int status, String body) throws Exception {
        if (status == 401) {
            throw new RejectedException();
        }
        if (status == 409) {
            throw new ConflictException();
        }
        if (status != 200) {
            throw new ServerResponseException(status);
        }
        return new JSONObject(body);
    }

    private JSONObject fetch(String address, String path, String method) throws Exception {
        HttpsURLConnection connection = (HttpsURLConnection)
                new URL("https://" + address + path).openConnection(Proxy.NO_PROXY);
        connection.setSSLSocketFactory(socketFactory());
        connection.setHostnameVerifier((host, session) -> {
            try {
                return matches((X509Certificate) session.getPeerCertificates()[0]);
            } catch (GeneralSecurityException | SSLPeerUnverifiedException error) {
                return false;
            }
        });
        connection.setConnectTimeout(2200);
        connection.setReadTimeout(5000);
        connection.setRequestMethod(method);
        connection.setInstanceFollowRedirects(false);
        connection.setUseCaches(false);
        connection.setRequestProperty("Authorization", "Bearer " + token);
        boolean consumed = false;
        try {
            int status = connection.getResponseCode();
            if (status == 401) {
                throw new RejectedException();
            }
            if (status == 409) {
                throw new ConflictException();
            }
            if (status != 200) {
                throw new ServerResponseException(status);
            }
            try (InputStream stream = connection.getInputStream()) {
                ByteArrayOutputStream buffer = new ByteArrayOutputStream(4096);
                byte[] chunk = new byte[4096];
                int size;
                while ((size = stream.read(chunk)) != -1) {
                    if (buffer.size() + size > MAX_RESPONSE) {
                        throw new IOException("Response is too large");
                    }
                    buffer.write(chunk, 0, size);
                }
                consumed = true;
                return new JSONObject(buffer.toString(StandardCharsets.UTF_8.name()));
            }
        } finally {
            if (!consumed) {
                connection.disconnect();
            }
        }
    }

    private SSLSocketFactory socketFactory() throws GeneralSecurityException {
        if (sockets == null) {
            SSLContext tls = SSLContext.getInstance("TLS");
            tls.init(null, new TrustManager[] { new X509TrustManager() {
                @Override public void checkClientTrusted(X509Certificate[] chain, String authType)
                        throws CertificateException {
                    throw new CertificateException("Client certificates are unsupported");
                }

                @Override public void checkServerTrusted(X509Certificate[] chain, String authType)
                        throws CertificateException {
                    try {
                        if (chain == null || chain.length == 0 || !matches(chain[0])) {
                            throw new CertificateException("Incorrect PC identity");
                        }
                        chain[0].checkValidity();
                    } catch (GeneralSecurityException error) {
                        throw new CertificateException("Unable to verify PC identity", error);
                    }
                }

                @Override public X509Certificate[] getAcceptedIssuers() {
                    return new X509Certificate[0];
                }
            } }, null);
            sockets = tls.getSocketFactory();
        }
        return sockets;
    }

    private boolean matches(X509Certificate certificate) throws GeneralSecurityException {
        return MessageDigest.isEqual(pin,
                MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded()));
    }

    static final class RejectedException extends IOException {}
    static final class ConflictException extends IOException {}
    static final class ServerResponseException extends IOException {
        final int status;
        ServerResponseException(int status) {
            super("HTTP " + status);
            this.status = status;
        }
    }
}
