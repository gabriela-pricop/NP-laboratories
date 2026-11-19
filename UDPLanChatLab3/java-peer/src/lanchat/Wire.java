package lanchat;

import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;
import java.util.regex.Pattern;

public final class Wire {
    public static final int VERSION = 1;
    public static final int MAX_TEXT = 500;
    public static final int MAX_DATAGRAM = 4096;

    private static final Pattern NAME = Pattern.compile("^[A-Za-z0-9_.-]{1,20}$");
    private static final Pattern ID = Pattern.compile("^[A-Za-z0-9-]{1,64}$");
    private static final java.util.Set<String> TYPES =
            java.util.Set.of("join", "here", "taken", "beat", "leave", "chat", "pm");

    private Wire() {
    }

    public record Message(String type, String id, String name, String text, String to, String nonce, String data) {
        public static Message of(String type, String id, String name) {
            return new Message(type, id, name, null, null, null, null);
        }
    }

    public record Decoded(Message message, String reason) {
    }

    public static boolean isValidName(String name) {
        return name != null && NAME.matcher(name).matches();
    }

    public static boolean namesEqual(String a, String b) {
        return a.equalsIgnoreCase(b);
    }

    public static byte[] encode(Message m) {
        StringBuilder json = new StringBuilder(128);
        json.append("{\"v\":").append(VERSION);
        field(json, "type", m.type());
        field(json, "id", m.id());
        field(json, "name", m.name());
        field(json, "text", m.text());
        field(json, "to", m.to());
        field(json, "nonce", m.nonce());
        field(json, "data", m.data());
        json.append('}');
        return json.toString().getBytes(StandardCharsets.UTF_8);
    }

    private static void field(StringBuilder json, String key, String value) {
        if (value == null) {
            return;
        }
        json.append(",\"").append(key).append("\":\"");
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '"' -> json.append("\\\"");
                case '\\' -> json.append("\\\\");
                case '\n' -> json.append("\\n");
                case '\r' -> json.append("\\r");
                case '\t' -> json.append("\\t");
                default -> {
                    if (c < 0x20) {
                        json.append(String.format("\\u%04x", (int) c));
                    } else {
                        json.append(c);
                    }
                }
            }
        }
        json.append('"');
    }

    public static Decoded decode(byte[] buffer, int length) {
        if (length == 0) {
            return new Decoded(null, "empty");
        }
        if (length > MAX_DATAGRAM) {
            return new Decoded(null, "too long (" + length + " > " + MAX_DATAGRAM + " bytes)");
        }

        String text;
        try {
            text = StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(buffer, 0, length))
                    .toString();
        } catch (CharacterCodingException e) {
            return new Decoded(null, "not JSON");
        }

        Map<String, Object> obj;
        try {
            obj = new FlatParser(text).parse();
        } catch (IllegalArgumentException e) {
            return new Decoded(null, "not JSON");
        }

        if (!(obj.get("v") instanceof Long v) || v != VERSION) {
            return new Decoded(null, "missing or unknown version");
        }
        String type = str(obj, "type");
        if (type == null || !TYPES.contains(type)) {
            return new Decoded(null, "unknown type");
        }
        String id = str(obj, "id");
        if (id == null || !ID.matcher(id).matches()) {
            return new Decoded(null, "bad id");
        }
        String name = str(obj, "name");
        if (!isValidName(name)) {
            return new Decoded(null, "bad name");
        }

        String body = str(obj, "text");
        String to = str(obj, "to");
        String nonce = str(obj, "nonce");
        String data = str(obj, "data");

        if (type.equals("chat")) {
            if (body == null || body.isEmpty()) {
                return new Decoded(null, "chat without text");
            }
            if (body.length() > MAX_TEXT) {
                return new Decoded(null, "text too long (" + body.length() + " > " + MAX_TEXT + " characters)");
            }
        }
        if (type.equals("taken") && body != null && body.length() > MAX_TEXT) {
            return new Decoded(null, "reason too long");
        }
        if (type.equals("pm")) {
            if (!isValidName(to)) {
                return new Decoded(null, "private message without a valid receiver");
            }
            if (nonce == null || data == null) {
                return new Decoded(null, "private message without nonce or data");
            }
        }

        return new Decoded(new Message(type, id, name, body, to, nonce, data), "");
    }

    private static String str(Map<String, Object> obj, String key) {
        return obj.get(key) instanceof String s ? s : null;
    }

    private static final class FlatParser {
        private final String s;
        private int i;

        FlatParser(String s) {
            this.s = s;
        }

        Map<String, Object> parse() {
            Map<String, Object> result = new HashMap<>();
            skipSpaces();
            expect('{');
            skipSpaces();
            if (peek() == '}') {
                i++;
            } else {
                while (true) {
                    skipSpaces();
                    String key = string();
                    skipSpaces();
                    expect(':');
                    skipSpaces();
                    result.put(key, value());
                    skipSpaces();
                    char c = next();
                    if (c == '}') {
                        break;
                    }
                    if (c != ',') {
                        throw new IllegalArgumentException("expected , or }");
                    }
                }
            }
            skipSpaces();
            if (i != s.length()) {
                throw new IllegalArgumentException("trailing characters");
            }
            return result;
        }

        private Object value() {
            char c = peek();
            if (c == '"') {
                return string();
            }
            if (c == '-' || Character.isDigit(c)) {
                int start = i;
                while (i < s.length() && "+-0123456789.eE".indexOf(s.charAt(i)) >= 0) {
                    i++;
                }
                String number = s.substring(start, i);
                try {
                    return Long.parseLong(number);
                } catch (NumberFormatException e) {
                    return Double.parseDouble(number);
                }
            }
            for (String literal : new String[] {"true", "false", "null"}) {
                if (s.startsWith(literal, i)) {
                    i += literal.length();
                    return literal.equals("null") ? null : Boolean.valueOf(literal);
                }
            }
            throw new IllegalArgumentException("unsupported value");
        }

        private String string() {
            expect('"');
            StringBuilder out = new StringBuilder();
            while (true) {
                char c = next();
                if (c == '"') {
                    return out.toString();
                }
                if (c < 0x20) {
                    throw new IllegalArgumentException("control character in string");
                }
                if (c != '\\') {
                    out.append(c);
                    continue;
                }
                char e = next();
                switch (e) {
                    case '"', '\\', '/' -> out.append(e);
                    case 'b' -> out.append('\b');
                    case 'f' -> out.append('\f');
                    case 'n' -> out.append('\n');
                    case 'r' -> out.append('\r');
                    case 't' -> out.append('\t');
                    case 'u' -> {
                        if (i + 4 > s.length()) {
                            throw new IllegalArgumentException("short \\u escape");
                        }
                        out.append((char) Integer.parseInt(s.substring(i, i + 4), 16));
                        i += 4;
                    }
                    default -> throw new IllegalArgumentException("bad escape");
                }
            }
        }

        private void skipSpaces() {
            while (i < s.length() && Character.isWhitespace(s.charAt(i))) {
                i++;
            }
        }

        private char peek() {
            if (i >= s.length()) {
                throw new IllegalArgumentException("unexpected end");
            }
            return s.charAt(i);
        }

        private char next() {
            char c = peek();
            i++;
            return c;
        }

        private void expect(char c) {
            if (next() != c) {
                throw new IllegalArgumentException("expected " + c);
            }
        }
    }
}
