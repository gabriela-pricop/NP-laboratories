package coffeeshop;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;

public final class LineFramer {

    public enum Kind {
        NONE,
        LINE,
        TOO_LONG
    }

    public static final class Message {

        public final Kind kind;
        public final String text;

        private Message(Kind kind, String text) {
            this.kind = kind;
            this.text = text;
        }

        static final Message NONE = new Message(Kind.NONE, "");
        static final Message TOO_LONG = new Message(Kind.TOO_LONG, "");

        static Message line(String text) {
            return new Message(Kind.LINE, text);
        }
    }

    private static final byte TERMINATOR = (byte) '\n';

    private final int maxLength;
    private byte[] buffer;
    private int length;
    private boolean discarding;

    public LineFramer(int maxLength) {
        this.maxLength = maxLength;
        this.buffer = new byte[Math.min(maxLength + 64, 4096)];
        this.length = 0;
        this.discarding = false;
    }

    public void feed(byte[] data, int count) {
        if (count <= 0) {
            return;
        }

        ensureCapacity(length + count);
        System.arraycopy(data, 0, buffer, length, count);
        length += count;
    }

    public Message take() {
        int newline = indexOfTerminator();

        if (newline < 0) {
            if (length > maxLength) {
                length = 0;
                discarding = true;
            }

            return Message.NONE;
        }

        if (discarding) {
            consume(newline + 1);
            discarding = false;
            return Message.TOO_LONG;
        }

        if (newline > maxLength) {
            consume(newline + 1);
            return Message.TOO_LONG;
        }

        int lineLength = newline;

        if (lineLength > 0 && buffer[lineLength - 1] == (byte) '\r') {
            lineLength--;
        }

        String text = new String(buffer, 0, lineLength, StandardCharsets.UTF_8);
        consume(newline + 1);
        return Message.line(text);
    }

    private int indexOfTerminator() {
        for (int i = 0; i < length; i++) {
            if (buffer[i] == TERMINATOR) {
                return i;
            }
        }

        return -1;
    }

    private void consume(int count) {
        int remaining = length - count;

        if (remaining > 0) {
            System.arraycopy(buffer, count, buffer, 0, remaining);
        }

        length = remaining;
    }

    private void ensureCapacity(int needed) {
        if (needed <= buffer.length) {
            return;
        }

        int size = buffer.length;

        while (size < needed) {
            size *= 2;
        }

        buffer = Arrays.copyOf(buffer, size);
    }
}
