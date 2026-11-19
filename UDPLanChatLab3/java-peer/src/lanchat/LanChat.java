package lanchat;

import java.io.BufferedReader;
import java.io.FileWriter;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.PrintWriter;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.MulticastSocket;
import java.net.NetworkInterface;
import java.net.SocketAddress;
import java.net.SocketException;
import java.net.StandardSocketOptions;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.TreeMap;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import lanchat.Wire.Decoded;
import lanchat.Wire.Message;

public final class LanChat {
    private static final String DEFAULT_GROUP = "239.255.42.99";
    private static final long JOIN_WAIT_MS = 1000;
    private static final long HEARTBEAT_MS = 5000;
    private static final long SILENCE_MS = 15000;

    private final String name;
    private final String id = UUID.randomUUID().toString().replace("-", "");
    private final InetSocketAddress group;
    private final NetworkInterface nic;
    private final SecureBox box;
    private final Log log;
    private final MulticastSocket groupSocket;
    private final DatagramSocket directSocket;

    private final TreeMap<String, Peer> peers = new TreeMap<>();
    private final CountDownLatch refused = new CountDownLatch(1);
    private final ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread t = new Thread(r, "membership");
        t.setDaemon(true);
        return t;
    });
    private volatile boolean joined;
    private volatile boolean closed;
    private volatile String refusal = "";
    private long nextBeat;

    private record Peer(String name, String id, SocketAddress endpoint, long lastSeen) {
    }

    public static void main(String[] args) throws Exception {
        List<String> positional = new ArrayList<>();
        String iface = null;
        String groupAddress = DEFAULT_GROUP;
        for (int i = 0; i < args.length; i++) {
            if ((args[i].equals("--iface") || args[i].equals("--group")) && i + 1 < args.length) {
                if (args[i].equals("--iface")) {
                    iface = args[++i];
                } else {
                    groupAddress = args[++i];
                }
            } else {
                positional.add(args[i]);
            }
        }

        int port = positional.size() == 3 ? parsePort(positional.get(1)) : -1;
        if (positional.size() != 3 || !Wire.isValidName(positional.get(0)) || port < 0
                || positional.get(2).isEmpty()) {
            System.err.println("Usage: java -cp out lanchat.LanChat <name> <port> <passphrase> "
                    + "[--iface <local IPv4>] [--group <multicast IPv4>]");
            System.exit(1);
        }

        InetAddress local = iface != null ? InetAddress.getByName(iface) : pickInterface();
        NetworkInterface nic = NetworkInterface.getByInetAddress(local);
        if (nic == null) {
            System.err.println("No network interface has the address " + local.getHostAddress());
            System.exit(1);
        }

        System.out.println("* deriving the key from the passphrase...");
        var chat = new LanChat(positional.get(0), new InetSocketAddress(groupAddress, port), nic,
                new SecureBox(positional.get(2)));
        System.exit(chat.run(local));
    }

    private static int parsePort(String text) {
        try {
            int port = Integer.parseInt(text);
            return port >= 1 && port <= 65535 ? port : -1;
        } catch (NumberFormatException e) {
            return -1;
        }
    }

    private static InetAddress pickInterface() throws SocketException {
        InetAddress fallback = null;
        for (NetworkInterface n : Collections.list(NetworkInterface.getNetworkInterfaces())) {
            if (!n.isUp() || n.isLoopback() || !n.supportsMulticast() || n.isVirtual()) {
                continue;
            }
            for (InetAddress a : Collections.list(n.getInetAddresses())) {
                if (a instanceof Inet4Address) {
                    String label = n.getDisplayName().toLowerCase(Locale.ROOT);
                    boolean virtual = label.contains("virtual") || label.contains("vmware")
                            || label.contains("hyper-v") || label.contains("vpn");
                    if (!virtual) {
                        return a;
                    }
                    if (fallback == null) {
                        fallback = a;
                    }
                }
            }
        }
        return fallback != null ? fallback : InetAddress.getLoopbackAddress();
    }

    private LanChat(String name, InetSocketAddress group, NetworkInterface nic, SecureBox box) throws IOException {
        this.name = name;
        this.group = group;
        this.nic = nic;
        this.box = box;
        this.log = new Log("chat-" + name + ".log");

        groupSocket = new MulticastSocket(null);
        groupSocket.setReuseAddress(true);
        groupSocket.bind(new InetSocketAddress(group.getPort()));
        groupSocket.joinGroup(new InetSocketAddress(group.getAddress(), 0), nic);

        directSocket = new DatagramSocket(0);
        directSocket.setOption(StandardSocketOptions.IP_MULTICAST_IF, nic);
        directSocket.setOption(StandardSocketOptions.IP_MULTICAST_TTL, 1);
        directSocket.setOption(StandardSocketOptions.IP_MULTICAST_LOOP, true);
    }

    private int run(InetAddress local) throws Exception {
        say("* " + name + ": group " + group.getAddress().getHostAddress() + ":" + group.getPort()
                + " on interface " + local.getHostAddress() + ", private messages on port "
                + directSocket.getLocalPort());
        say("* log file: " + log.path);

        startReceiving(groupSocket, "group receiver");
        startReceiving(directSocket, "direct receiver");

        send(Message.of("join", id, name), group);
        if (refused.await(JOIN_WAIT_MS, TimeUnit.MILLISECONDS)) {
            send(Message.of("leave", id, name), group);
            say("* cannot join: " + refusal);
            close();
            return 2;
        }

        joined = true;
        nextBeat = System.currentTimeMillis() + HEARTBEAT_MS;
        timer.scheduleAtFixedRate(this::tick, 1, 1, TimeUnit.SECONDS);
        int others;
        synchronized (peers) {
            others = peers.size();
        }
        say("* you joined the chat as " + name + "; " + others + " other(s) online. Type /help for the commands.");

        Runtime.getRuntime().addShutdownHook(new Thread(this::leave));

        var in = new BufferedReader(new InputStreamReader(System.in,
                System.console() != null ? System.console().charset() : StandardCharsets.UTF_8));
        String line;
        while ((line = in.readLine()) != null) {
            line = line.stripTrailing();
            if (line.isEmpty()) {
                continue;
            }
            if (!line.startsWith("/")) {
                sendGeneral(line);
                continue;
            }
            String[] parts = line.split(" +", 3);
            switch (parts[0].toLowerCase(Locale.ROOT)) {
                case "/msg" -> {
                    if (parts.length == 3) {
                        sendPrivate(parts[1], parts[2]);
                    } else {
                        say("! usage: /msg name text");
                    }
                }
                case "/users" -> showUsers();
                case "/quit" -> {
                    leave();
                    return 0;
                }
                case "/help" -> say("  text | /msg name text | /users | /quit");
                default -> say("! unknown command " + parts[0] + "; /help lists the commands");
            }
        }
        leave();
        return 0;
    }

    private boolean send(Message m, SocketAddress to) {
        byte[] bytes = Wire.encode(m);
        log.write("SEND", to, m.type(), bytes.length, "");
        try {
            directSocket.send(new DatagramPacket(bytes, bytes.length, to));
            return true;
        } catch (IOException e) {
            log.write("ERR ", to, m.type(), 0, "send failed: " + e.getMessage());
            return false;
        }
    }

    private void sendGeneral(String text) {
        if (text.length() > Wire.MAX_TEXT) {
            say("! a message has at most " + Wire.MAX_TEXT + " characters (this one has " + text.length() + ")");
            return;
        }
        send(new Message("chat", id, name, text, null, null, null), group);
    }

    private void sendPrivate(String receiver, String text) throws Exception {
        if (text.length() > Wire.MAX_TEXT) {
            say("! a message has at most " + Wire.MAX_TEXT + " characters (this one has " + text.length() + ")");
            return;
        }
        if (Wire.namesEqual(receiver, name)) {
            say("! that is you");
            return;
        }
        Peer peer;
        synchronized (peers) {
            peer = peers.get(receiver.toLowerCase(Locale.ROOT));
        }
        if (peer == null) {
            say("! " + receiver + " is not online");
            return;
        }
        String[] sealed = box.seal(text, name, peer.name());
        if (send(new Message("pm", id, name, null, peer.name(), sealed[0], sealed[1]), peer.endpoint())) {
            say("[private to " + peer.name() + "] " + name + ": " + text);
        }
    }

    private void startReceiving(DatagramSocket socket, String threadName) {
        Thread t = new Thread(() -> receiveLoop(socket), threadName);
        t.setDaemon(true);
        t.start();
    }

    private void receiveLoop(DatagramSocket socket) {
        byte[] buffer = new byte[65536];
        while (!closed) {
            DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
            try {
                socket.receive(packet);
            } catch (IOException e) {
                if (closed) {
                    return;
                }
                continue;
            }
            SocketAddress from = packet.getSocketAddress();
            Decoded d = Wire.decode(packet.getData(), packet.getLength());
            if (d.message() == null) {
                log.write("DROP", from, "?", packet.getLength(), d.reason());
                continue;
            }
            log.write("RECV", from, d.message().type(), packet.getLength(), "");
            try {
                handle(d.message(), from);
            } catch (Exception e) {
                log.write("ERR ", from, d.message().type(), 0, "handling failed: " + e);
            }
        }
    }

    private void handle(Message m, SocketAddress from) {
        if (m.type().equals("chat")) {
            say("[general] " + m.name() + ": " + m.text());
        }
        if (m.id().equals(id)) {
            return;
        }

        switch (m.type()) {
            case "join" -> onJoin(m, from);
            case "here" -> touch(m, from);
            case "taken" -> {
                if (!joined) {
                    refusal = "the name " + name + " is already in use (" + m.name() + " says so)";
                    refused.countDown();
                }
            }
            case "beat" -> {
                if (touch(m, from) == Touch.NEW && joined) {
                    say("* " + m.name() + " joined the chat");
                }
            }
            case "leave" -> {
                boolean removed;
                synchronized (peers) {
                    Peer p = peers.get(key(m.name()));
                    removed = p != null && p.id().equals(m.id()) && peers.remove(key(m.name())) != null;
                }
                if (removed) {
                    say("* " + m.name() + " left the chat");
                }
            }
            case "chat" -> refresh(m, from);
            case "pm" -> {
                refresh(m, from);
                showPrivate(m, from);
            }
            default -> {
            }
        }
    }

    private void onJoin(Message m, SocketAddress from) {
        if (Wire.namesEqual(m.name(), name)) {
            send(new Message("taken", id, name, "name in use", null, null, null), from);
            return;
        }
        Touch result = touch(m, from);
        if (result == Touch.CONFLICT) {
            send(new Message("taken", id, name, "name in use", null, null, null), from);
            return;
        }
        send(Message.of("here", id, name), from);
        if (result == Touch.NEW && joined) {
            say("* " + m.name() + " joined the chat");
        }
    }

    private void showPrivate(Message m, SocketAddress from) {
        if (!Wire.namesEqual(m.to(), name)) {
            log.write("DROP", from, "pm", 0, "private message for " + m.to() + ", not for us");
            return;
        }
        String text = box.open(m.nonce(), m.data(), m.name(), m.to());
        if (text == null) {
            log.write("DROP", from, "pm", 0, "cannot decrypt (wrong passphrase or changed on the way)");
            say("! a private message from " + m.name() + " could not be decrypted");
            return;
        }
        if (text.isEmpty() || text.length() > Wire.MAX_TEXT) {
            log.write("DROP", from, "pm", 0, "private text empty or too long");
            return;
        }
        say("[private] " + m.name() + ": " + text);
    }

    private enum Touch { NEW, REFRESHED, CONFLICT }

    private static String key(String peerName) {
        return peerName.toLowerCase(Locale.ROOT);
    }

    private Touch touch(Message m, SocketAddress from) {
        synchronized (peers) {
            Peer known = peers.get(key(m.name()));
            if (known != null && !known.id().equals(m.id())) {
                return Touch.CONFLICT;
            }
            peers.put(key(m.name()), new Peer(m.name(), m.id(), from, System.currentTimeMillis()));
            return known == null ? Touch.NEW : Touch.REFRESHED;
        }
    }

    private void refresh(Message m, SocketAddress from) {
        synchronized (peers) {
            Peer known = peers.get(key(m.name()));
            if (known != null && known.id().equals(m.id())) {
                peers.put(key(m.name()), new Peer(m.name(), m.id(), from, System.currentTimeMillis()));
            }
        }
    }

    private void tick() {
        long now = System.currentTimeMillis();
        if (now >= nextBeat) {
            nextBeat = now + HEARTBEAT_MS;
            send(Message.of("beat", id, name), group);
        }
        List<Peer> gone = new ArrayList<>();
        synchronized (peers) {
            peers.values().removeIf(p -> {
                if (now - p.lastSeen() > SILENCE_MS) {
                    gone.add(p);
                    return true;
                }
                return false;
            });
        }
        for (Peer p : gone) {
            say("* " + p.name() + " left the chat (not heard from for " + SILENCE_MS / 1000 + " s)");
        }
    }

    private void showUsers() {
        List<Peer> list;
        synchronized (peers) {
            list = new ArrayList<>(peers.values());
        }
        long now = System.currentTimeMillis();
        say("* online (" + (list.size() + 1) + "):");
        say("    " + name + " (you)");
        for (Peer p : list) {
            say(String.format("    %-20s %-21s heard %d s ago", p.name(), text(p.endpoint()), (now - p.lastSeen()) / 1000));
        }
    }

    private synchronized void leave() {
        if (closed) {
            return;
        }
        timer.shutdownNow();
        if (joined) {
            send(Message.of("leave", id, name), group);
        }
        close();
    }

    private void close() {
        closed = true;
        try {
            groupSocket.leaveGroup(new InetSocketAddress(group.getAddress(), 0), nic);
        } catch (IOException ignored) {
        }
        groupSocket.close();
        directSocket.close();
        log.close();
    }

    private static String text(SocketAddress endpoint) {
        return endpoint.toString().replaceFirst("^.*/", "");
    }

    private static synchronized void say(String line) {
        System.out.println(line);
    }

    private static final class Log {
        private static final DateTimeFormatter TIME = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS");
        final String path;
        private final PrintWriter out;

        Log(String fileName) throws IOException {
            path = Path.of(fileName).toAbsolutePath().toString();
            out = new PrintWriter(new FileWriter(fileName, StandardCharsets.UTF_8, true), true);
            write("----", null, "start", 0, "");
        }

        synchronized void write(String direction, SocketAddress endpoint, String kind, int size, String note) {
            String ep = endpoint == null ? "-" : text(endpoint);
            String line = String.format("%s  %s  %-21s  %-6s  %5d B", LocalDateTime.now().format(TIME),
                    direction, ep, kind, size);
            out.println(note.isEmpty() ? line : line + "  " + note);
        }

        synchronized void close() {
            out.close();
        }
    }
}
