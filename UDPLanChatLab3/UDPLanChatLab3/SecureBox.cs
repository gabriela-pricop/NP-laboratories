using System.Security.Cryptography;
using System.Text;

namespace UDPLanChatLab3;

public sealed class SecureBox
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("UDPLanChatLab3/v1");
    private const int Iterations = 200_000;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] key;

    public SecureBox(string passphrase)
    {
        key = Rfc2898DeriveBytes.Pbkdf2(passphrase, Salt, Iterations, HashAlgorithmName.SHA256, KeySize);
    }

    public (string Nonce, string Data) Seal(string text, string from, string to)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plain = Encoding.UTF8.GetBytes(text);
        byte[] output = new byte[plain.Length + TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), AssociatedData(from, to));

        return (Convert.ToBase64String(nonce), Convert.ToBase64String(output));
    }

    public bool TryOpen(string nonceBase64, string dataBase64, string from, string to, out string text)
    {
        text = "";
        try
        {
            byte[] nonce = Convert.FromBase64String(nonceBase64);
            byte[] data = Convert.FromBase64String(dataBase64);
            if (nonce.Length != NonceSize || data.Length < TagSize)
                return false;

            int length = data.Length - TagSize;
            byte[] plain = new byte[length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, data.AsSpan(0, length), data.AsSpan(length), plain, AssociatedData(from, to));

            text = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] AssociatedData(string from, string to) =>
        Encoding.UTF8.GetBytes($"pm|{from.ToLowerInvariant()}|{to.ToLowerInvariant()}");
}
