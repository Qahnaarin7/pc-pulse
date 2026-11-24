using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

sealed class PairingIdentity(X509Certificate2 certificate, byte[] token) : IDisposable
{
    public X509Certificate2 Certificate { get; } = certificate;
    public byte[] Token { get; } = token;
    public static string StateDirectory => Environment.GetEnvironmentVariable("PC_PULSE_STATE_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcPulse");

    public static PairingIdentity Load()
    {
        var directory = StateDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "identity.json");
        if (!File.Exists(path))
            CreateIdentity(path);

        var saved = JsonSerializer.Deserialize<SavedIdentity>(File.ReadAllBytes(path)) ??
            throw new InvalidDataException("Не вдалося прочитати ідентифікатор PC Pulse.");
        if (saved.Token.Length != 24)
            throw new InvalidDataException("Пошкоджений ключ PC Pulse.");

        // Windows Schannel needs access to the imported key in the user's key store.
        var certificate = X509CertificateLoader.LoadPkcs12(
            saved.Certificate, Encode(saved.Token), X509KeyStorageFlags.UserKeySet);
        return new PairingIdentity(certificate, saved.Token);
    }

    public string CreateCode(IPAddress address, int port)
    {
        Span<byte> data = stackalloc byte[62];
        address.GetAddressBytes().CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data[4..], (ushort)port);
        Certificate.GetCertHash(HashAlgorithmName.SHA256).CopyTo(data[6..]);
        Token.CopyTo(data[38..]);
        return "PCP1-" + Encode(data);
    }

    public string CreateCode(IReadOnlyList<MonitorAddress> addresses, int port)
    {
        if (addresses.Count is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(addresses));
        var data = new byte[56 + addresses.Count * 6];
        Certificate.GetCertHash(HashAlgorithmName.SHA256).CopyTo(data, 0);
        Token.CopyTo(data, 32);
        for (var index = 0; index < addresses.Count; index++)
        {
            var offset = 56 + index * 6;
            addresses[index].Address.GetAddressBytes().CopyTo(data, offset);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 4), (ushort)port);
        }
        return "PCP2-" + Encode(data);
    }

    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => Certificate.Dispose();

    private static void CreateIdentity(string path)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PC Pulse", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var token = RandomNumberGenerator.GetBytes(24);
        var saved = new SavedIdentity(certificate.Export(X509ContentType.Pfx, Encode(token)), token);

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(saved));
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance completed initialization.
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record SavedIdentity(byte[] Certificate, byte[] Token);
}
