package lanchat;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;
import java.util.Base64;
import java.util.Locale;
import javax.crypto.Cipher;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.PBEKeySpec;
import javax.crypto.spec.SecretKeySpec;

public final class SecureBox {
    private static final byte[] SALT = "UDPLanChatLab3/v1".getBytes(StandardCharsets.UTF_8);
    private static final int ITERATIONS = 200_000;
    private static final int NONCE_SIZE = 12;
    private static final int TAG_BITS = 128;

    private final SecretKeySpec key;
    private final SecureRandom random = new SecureRandom();

    public SecureBox(String passphrase) throws GeneralSecurityException {
        var spec = new PBEKeySpec(passphrase.toCharArray(), SALT, ITERATIONS, 256);
        byte[] bytes = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).getEncoded();
        key = new SecretKeySpec(bytes, "AES");
    }

    public String[] seal(String text, String from, String to) throws GeneralSecurityException {
        byte[] nonce = new byte[NONCE_SIZE];
        random.nextBytes(nonce);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key, new GCMParameterSpec(TAG_BITS, nonce));
        cipher.updateAAD(associatedData(from, to));
        byte[] data = cipher.doFinal(text.getBytes(StandardCharsets.UTF_8));
        Base64.Encoder b64 = Base64.getEncoder();
        return new String[] {b64.encodeToString(nonce), b64.encodeToString(data)};
    }

    public String open(String nonceB64, String dataB64, String from, String to) {
        try {
            byte[] nonce = Base64.getDecoder().decode(nonceB64);
            byte[] data = Base64.getDecoder().decode(dataB64);
            if (nonce.length != NONCE_SIZE || data.length < TAG_BITS / 8) {
                return null;
            }
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(TAG_BITS, nonce));
            cipher.updateAAD(associatedData(from, to));
            return new String(cipher.doFinal(data), StandardCharsets.UTF_8);
        } catch (IllegalArgumentException | GeneralSecurityException e) {
            return null;
        }
    }

    private static byte[] associatedData(String from, String to) {
        return ("pm|" + from.toLowerCase(Locale.ROOT) + "|" + to.toLowerCase(Locale.ROOT))
                .getBytes(StandardCharsets.UTF_8);
    }
}
