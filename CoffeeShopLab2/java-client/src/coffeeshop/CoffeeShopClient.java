package coffeeshop;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.ConnectException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Locale;

public final class CoffeeShopClient {

    private static final String DEFAULT_HOST = "127.0.0.1";
    private static final int DEFAULT_PORT = 5050;
    private static final int MAX_LINE_LENGTH = 1024;

    private final Socket socket;
    private final OutputStream output;
    private final InputStream input;
    private final Object sendGate = new Object();

    private volatile boolean running = true;

    private CoffeeShopClient(Socket socket) throws IOException {
        this.socket = socket;
        this.output = socket.getOutputStream();
        this.input = socket.getInputStream();
    }

    public static void main(String[] args) {
        if (args.length > 0 && (args[0].equals("-h") || args[0].equals("--help"))) {
            usage();
            return;
        }

        String role = args.length > 0 ? args[0].toLowerCase(Locale.ROOT) : "client";

        if (!role.equals("client") && !role.equals("provider")) {
            System.err.println("unknown role '" + args[0] + "'");
            usage();
            System.exit(1);
        }

        String host = args.length > 1 ? args[1] : DEFAULT_HOST;
        int port = DEFAULT_PORT;

        if (args.length > 2) {
            try {
                port = Integer.parseInt(args[2]);
            } catch (NumberFormatException ex) {
                System.err.println("'" + args[2] + "' is not a port number");
                System.exit(1);
            }
        }

        Socket socket = new Socket();

        try {
            socket.connect(new InetSocketAddress(host, port));
        } catch (ConnectException ex) {
            System.err.println("nobody is listening on " + host + ":" + port + ", is the server started?");
            System.exit(1);
        } catch (IOException ex) {
            System.err.println("cannot connect to " + host + ":" + port + ": " + ex.getMessage());
            System.exit(1);
        }

        try {
            socket.setTcpNoDelay(true);
            socket.setKeepAlive(true);

            CoffeeShopClient client = new CoffeeShopClient(socket);
            System.out.println("connected to " + host + ":" + port + " as " + role);
            System.out.println("type 'help' for the commands, 'quit' to leave");
            client.run(role);
        } catch (IOException ex) {
            System.err.println("connection failed: " + ex.getMessage());
            System.exit(1);
        }
    }

    private void run(String role) throws IOException {
        Thread receiver = new Thread(this::receiveLoop, "receiver");
        receiver.setDaemon(true);
        receiver.start();

        send("HELLO " + role.toUpperCase(Locale.ROOT));
        inputLoop();
    }

    private void receiveLoop() {
        LineFramer framer = new LineFramer(MAX_LINE_LENGTH);
        byte[] chunk = new byte[4096];

        while (running) {
            LineFramer.Message message = framer.take();

            if (message.kind == LineFramer.Kind.LINE) {
                System.out.println("<< " + describe(message.text));
                continue;
            }

            if (message.kind == LineFramer.Kind.TOO_LONG) {
                System.out.println("<< a message from the server was too long and was dropped");
                continue;
            }

            int count;

            try {
                count = input.read(chunk);
            } catch (IOException ex) {
                count = -1;
            }

            if (count <= 0) {
                if (running) {
                    System.out.println();
                    System.out.println("the server is gone, closing");
                }

                running = false;
                closeQuietly();
                System.exit(0);
                return;
            }

            framer.feed(chunk, count);
        }
    }

    private void inputLoop() {
        BufferedReader keyboard = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));

        while (running) {
            String line;

            try {
                line = keyboard.readLine();
            } catch (IOException ex) {
                break;
            }

            if (line == null) {
                sendQuietly("QUIT");
                break;
            }

            line = line.trim();

            if (line.isEmpty()) {
                continue;
            }

            if (line.equalsIgnoreCase("help")) {
                help();
                continue;
            }

            if (line.length() > MAX_LINE_LENGTH) {
                System.out.println("too long, the server accepts at most " + MAX_LINE_LENGTH + " characters");
                continue;
            }

            int space = line.indexOf(' ');
            String command = space < 0 ? line : line.substring(0, space);
            String rest = space < 0 ? "" : line.substring(space + 1);
            String message = space < 0
                    ? command.toUpperCase(Locale.ROOT)
                    : command.toUpperCase(Locale.ROOT) + " " + rest;

            try {
                send(message);
            } catch (IOException ex) {
                System.out.println("could not send, the connection is gone");
                break;
            }

            if (command.equalsIgnoreCase("QUIT")) {
                sleep(200);
                break;
            }
        }

        running = false;
        closeQuietly();
    }

    private void send(String line) throws IOException {
        byte[] bytes = (line + "\n").getBytes(StandardCharsets.UTF_8);

        synchronized (sendGate) {
            output.write(bytes);
            output.flush();
        }
    }

    private void sendQuietly(String line) {
        try {
            send(line);
        } catch (IOException ex) {
            return;
        }
    }

    private void closeQuietly() {
        try {
            socket.close();
        } catch (IOException ex) {
            return;
        }
    }

    private static void sleep(int milliseconds) {
        try {
            Thread.sleep(milliseconds);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private static String describe(String line) {
        String[] tokens = line.split(" ");

        if (tokens.length >= 3 && tokens[0].equals("ACCEPTED")) {
            return "order #" + tokens[1] + " accepted, ready in " + tokens[2] + "s";
        }

        if (tokens.length >= 3 && tokens[0].equals("REFUSED")) {
            return "refused, only " + tokens[2] + " " + tokens[1] + " left";
        }

        if (tokens.length >= 2 && tokens[0].equals("READY")) {
            return "*** order #" + tokens[1] + " is ready ***";
        }

        if (tokens.length >= 3 && tokens[0].equals("ALERT")) {
            return "*** low stock: " + tokens[1] + ", " + tokens[2] + " left ***";
        }

        return line;
    }

    private static void usage() {
        System.out.println("usage: java -cp out coffeeshop.CoffeeShopClient [client|provider] [host] [port]");
        System.out.println("  defaults: client " + DEFAULT_HOST + " " + DEFAULT_PORT);
    }

    private static void help() {
        System.out.println("  menu                     the products and the stock");
        System.out.println("  order coffee 2 tea 1     place an order");
        System.out.println("  deliver coffee 20        provider only, bring products");
        System.out.println("  ping                     check the connection");
        System.out.println("  quit                     leave");
    }
}
