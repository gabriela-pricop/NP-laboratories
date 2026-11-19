# UDPLanChatLab3 — A Local Network Chat

A chat for the local network over UDP, with no server: every running copy is a
peer that both sends and receives. Written against the socket API directly:
`Socket` with `SocketType.Dgram` in C#, `DatagramSocket` / `MulticastSocket` in
Java. There is no `UdpClient` and no library for chat, messaging, RPC or
discovery.

Tasks 1 to 4 are layers of one program, not four separate ones. The peer you
start covers all of them.

## Contents

```
UDPLanChatLab3.sln
UDPLanChatLab3/
  Program.cs          start-up: arguments, log, key, sockets, join, shutdown
  Options.cs          command line, choice of the network card
  ChatPeer.cs         the two sockets, the receive loops, sending
  Message.cs          message types, JSON encoding, validation (Wire)
  Membership.cs       join / here / taken / beat / leave, the 15-second expiry
  PeerTable.cs        who is online: name, endpoint, last heard from
  SecureBox.cs        PBKDF2 key derivation, AES-256-GCM
  ConsoleUi.cs        commands, showing messages while the user types
  DatagramLog.cs      the log file

java-peer/            the same chat in Java (bonus)
  src/lanchat/LanChat.java     sockets, membership, commands, log
  src/lanchat/Wire.java        the same JSON format and validation
  src/lanchat/SecureBox.java   the same PBKDF2 + AES-GCM
```

## Build and run

### C#

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet run --project UDPLanChatLab3 -- <name> <port> <passphrase> [--iface <local IPv4>] [--group <multicast IPv4>]
```

All the peers of one chat use the same port and the same passphrase:

```bash
dotnet run --project UDPLanChatLab3 -- Ana   5000 "secret words"
dotnet run --project UDPLanChatLab3 -- Ion   5000 "secret words"
dotnet run --project UDPLanChatLab3 -- Maria 5000 "secret words"
```

Several peers can run on one computer, each in its own terminal.

### Java

Requires a JDK (tested with JDK 21).

```bash
cd java-peer
javac -d out src/lanchat/*.java
java -cp out lanchat.LanChat Jon 5000 "secret words"
```

### Commands

| Command | What it does |
|---|---|
| `text` | sends text to everybody (the general channel) |
| `/msg name text` | sends text to name only (a private message) |
| `/users` | shows who is online, with endpoints |
| `/quit` | leaves the chat (Ctrl+C does the same) |
| `/help` | the list of commands |

### Choosing the network card

A computer often has several network cards: Wi-Fi, Ethernet, VirtualBox,
Hyper-V, VPN. The multicast group is joined and sent on **one** of them. By
default the C# peer picks the card that has a default gateway; the Java peer
picks the first one that is not obviously virtual. The chosen address is printed
at start-up. If it is the wrong card (for example a VirtualBox host-only adapter),
give the right one:

```bash
dotnet run --project UDPLanChatLab3 -- Ana 5000 "secret words" --iface 192.168.1.23
```

(`ipconfig` on Windows, `ipconfig getifaddr en0` on macOS, `hostname -I` on Linux.)
On two computers, also allow the program through the firewall (Windows asks the
first time; answer yes for private networks). If the Wi-Fi does not let multicast
through, use the hotspot of a phone.

## Protocol

Every message is one datagram holding one JSON object:

```json
{"v":1,"type":"chat","id":"9c1e0b6a...","name":"Ana","text":"hello everyone"}
```

| Field | Meaning |
|---|---|
| `v` | protocol version, always `1` |
| `type` | one of the types below |
| `id` | a random identifier of the running process (a GUID) |
| `name` | the author: 1–20 letters, digits, `_`, `-`, `.`; unique without case |
| `text` | `chat`: the message, at most 500 characters; `taken`: the reason |
| `to`, `nonce`, `data` | `pm` only: the receiver, the GCM nonce, ciphertext + tag (base64) |

| Type | Sent to | Meaning |
|---|---|---|
| `join` | group | "I am starting with this name" |
| `here` | the joining peer | "I am online" (the answer to `join`) |
| `taken` | the joining peer | "your name is in use" (the answer to `join`) |
| `beat` | group, every 5 s | "I am still here" |
| `leave` | group | "goodbye" |
| `chat` | group | a message of the general channel |
| `pm` | one peer | an encrypted private message |

The general channel uses the multicast group **239.255.42.99** (administratively
scoped, local network only) on the chat port, with TTL 1. Unknown extra fields
are ignored, so the format can grow.

## How it works

**Two sockets.** The *group socket* is bound to `0.0.0.0:<port>` with
`SO_REUSEADDR` and has joined the multicast group. It only receives. The *direct
socket* is bound to `0.0.0.0:<a port the system picks>`, different for every
peer. It sends everything and receives the answers to a join and the private
messages. Because every datagram leaves through the direct socket, its source
endpoint (IP + port) *is* the address to answer to. That is how peers learn where
to send private messages, with no port number inside the message.

**Why 0.0.0.0.** Bound to all addresses, the socket receives datagrams that
arrive on any card, and multicast datagrams, whose destination is the group
address and not the computer's IP. Bound to 127.0.0.1, nothing from other
computers would arrive. Bound to one IP, a change of network (or the multicast
destination address on some systems) would leave it deaf.

**Several peers on one computer.** `SO_REUSEADDR` (set before `Bind`) lets
several sockets bind the same port. Every socket that has joined the group gets
its own copy of a multicast datagram, so all the peers on one computer receive
the general channel. A *unicast* datagram to a shared port, however, reaches only
one of them. That is why answers and private messages go to the direct socket,
whose port belongs to one peer only.

**The general channel (Task 1).** A typed line becomes one `chat` datagram to the
group. Multicast loopback is on, so it comes back to the author too, and to the
other peers on the same computer. The author does not print it when sending. It
prints it once, when it comes back, like everybody else. The receive threads print
as soon as a datagram arrives, while the main thread waits in `ReadLine`.

**Recognising your own datagrams.** Every datagram carries the random `id` of the
process that sent it. A peer ignores its own `join`, `beat` and the rest by
comparing ids. Its own `chat` is the one message it does show.

**At most 500 characters.** One message is one datagram, and the whole datagram
must arrive in one piece. 500 Latin characters make a `chat` datagram of about
585 bytes, or about 860 for an encrypted `pm`. That is below the ~1 400 bytes that
fit an Ethernet frame without IP fragmentation. Even 500 non-Latin characters
(up to 3 bytes each in UTF-8) stay around 2 KB.
Over 65 507 bytes is impossible for UDP over IPv4: `SendTo` would fail with
`MessageSize`. Before that, a fragmented datagram is lost entirely if any
fragment is lost. The receiver drops anything over 4 096 bytes.

**Who is online (Task 2).** A new peer sends `join` to the group and waits one
second. Every peer that hears it answers *directly*, with `here` or `taken`. At
the end of the second, the answers are the list. Every 5 seconds each peer sends
`beat`, and the table records when each peer was last heard from. Once a second, a
timer removes the peers silent for 15 s (three missed heartbeats, so one lost
datagram does not remove anybody). `/quit` and Ctrl+C send `leave`. A killed
process sends nothing: UDP has no connection whose closing the others could
notice, so they find out only from the silence, 15 to 16 seconds later.

**Unique names.** A peer that receives a `join` with its own name, or with a name
it has in its table under another id, answers `taken`, and the newcomer exits.
A peer that is itself still joining also answers `taken` to a `join` with its own
name. So two peers that start with the same name at the same moment refuse each
other, and *neither* joins. A refused peer sends `leave` so that nobody keeps it
in their list. Names are compared without case. A peer that crashed is still in
the others' tables for up to 15 s, so restarting it with the same name within
that time is refused.

**Private messages (Task 3).** `/msg Ion text` looks Ion up in the table and sends
one `pm` datagram to Ion's direct endpoint only. Nobody else receives it, not even
peers on the same computer. A name that is not online gives an error at once,
without sending. A message sent to a peer that left a moment ago goes to a port
nobody listens on: it is lost, and the computer may answer with an ICMP *port
unreachable*. On Windows that makes the sender's next `ReceiveFrom` fail with
`ConnectionReset`, so this behaviour is switched off (`SIO_UDP_CONNRESET`) and the
receive loop ignores the error anyway. The sender is not told: there is no
acknowledgement. The receiver also checks that `to` is its own name.

**Secure private messages (Task 4).** The key is derived from the passphrase with
**PBKDF2-HMAC-SHA256**, 200 000 iterations, a fixed salt `"UDPLanChatLab3/v1"`
and 32 bytes of output. The fixed salt lets every peer derive the same key with
nothing exchanged, while the iterations make each guess at the passphrase
expensive. Each private message is encrypted with **AES-256-GCM** under a fresh
random 12-byte nonce from the system's cryptographic generator, sent in the clear
next to the ciphertext. The names `pm|from|to` are authenticated as associated
data. The 16-byte GCM tag means a datagram changed on the way, moved to another
pair of names, or encrypted with another passphrase fails to decrypt. It is then
dropped and logged, and the user is told. The general channel is not encrypted.

**Invalid datagrams.** Random bytes, invalid UTF-8, JSON that is not an object,
an unknown version or type, a bad name or id, a missing field, text over 500
characters and datagrams over 4 096 bytes are dropped and logged with the reason.
`TryDecode` never throws, and the handler of a valid message runs inside a
`try`/`catch` as a last guard, so nothing a datagram contains stops the chat.

**The log file.** `chat-<name>.log`, in the directory you run from. There is one
line per datagram:

```
2026-11-19 16:04:56.394  SEND  239.255.42.99:5000     chat       98 B
2026-11-19 16:04:56.394  RECV  192.168.1.23:45582     chat       98 B
2026-11-19 16:04:56.904  SEND  192.168.1.40:52630     pm        164 B
2026-11-19 16:05:00.472  DROP  192.168.1.77:36987     ?         300 B  not JSON
2026-11-19 16:05:00.472  DROP  192.168.1.77:36987     ?         566 B  text too long (501 > 500 characters)
```

## Bonus: two languages

`java-peer/` is the same chat in Java, with the same datagrams, the same
validation and the same PBKDF2 + AES-GCM parameters. C# and Java peers run in
one chat: they see each other in `/users`, share the general channel and send
encrypted private messages to each other. The JDK has no JSON parser, so
`Wire.java` holds a small one for flat objects. Unicode characters typed into a
Windows console come through in the Java peer only if the console can encode
them; `chcp 65001` before starting helps.

## A demonstration run

1. Three peers (`Ana`, `Ion` on one computer, `Maria` on another), all on port
   5000 with the same passphrase. Each start prints who is online. The others
   print `* Maria joined the chat`.
2. Ana types `hello everyone`. All three show `[general] Ana: hello everyone`,
   Ana included, once.
3. `/users` on any peer: the names and endpoints.
4. `/msg Ion see you at five` from Ana. Only Ion shows `[private] Ana: ...`.
   Maria's log has no line for it. `/msg nobody hi` gives an error.
5. Start another `Ana` (or `ana`): it is told the name is in use and exits.
6. `/quit` on Ion: the others show `* Ion left the chat` at once. Kill Maria's
   process (close the window). About 15 s later: `* Maria left the chat (not
   heard from for 15 s)`.
7. Start a peer with another passphrase and `/msg` someone: the receiver shows
   that the message could not be decrypted, and the log shows why.
8. Garbage: from another program, send random bytes or a 600-character `chat`
   to 239.255.42.99:5000. It shows up only as `DROP` lines in the logs.
9. Start the Java peer next to the C# ones and chat both ways.
