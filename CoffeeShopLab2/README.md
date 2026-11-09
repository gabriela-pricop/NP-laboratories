# CoffeeShopLab2 — A Coffee Shop Server

A coffee shop over TCP. Clients order products, the server takes them from its
stock and prepares them with a fixed number of workers, and a provider brings
new products. Written against the socket API directly: `Socket` in C# and
`java.net.Socket` in Java, with no `TcpListener`, `TcpClient`, `NetworkStream`
or any library above them.

Tasks 1 to 4 are layers of one program, not four separate ones: the server you
start covers all of them.

## Contents

```
CoffeeShopLab2.sln
CoffeeShopLab2.Shared/     protocol and framing, used by both C# sides
  Menu.cs                  products and preparation times
  OrderItem.cs
  Protocol.cs              commands, parsing, formatting
  LineFramer.cs            bytes to messages
  LineSocket.cs            socket plus framing plus one send lock

CoffeeShopLab2.Server/
  Program.cs               arguments, start-up, shutdown
  ServerHost.cs            the listening socket and the accept loop
  ClientSession.cs         one connection: commands, answers, cleanup
  Shop.cs                  stock, queue and provider together
  Stock.cs                 the amounts, under one lock
  StockStore.cs            the stock saved to a file
  Order.cs                 one order and its state machine
  WorkerPool.cs            the workers
  ProviderRegistry.cs      who the provider is, and the alerts
  Log.cs

CoffeeShopLab2.Client/
  Program.cs               client and provider in C#

java-client/
  src/coffeeshop/CoffeeShopClient.java
  src/coffeeshop/LineFramer.java
```

## Build and run

### Server and C# client

Requires the .NET 10 SDK.

```bash
dotnet build
```

Server, listening on all interfaces on port 5050 by default:

```bash
dotnet run --project CoffeeShopLab2.Server
dotnet run --project CoffeeShopLab2.Server 6000
dotnet run --project CoffeeShopLab2.Server --loopback
dotnet run --project CoffeeShopLab2.Server --stock shop.state
dotnet run --project CoffeeShopLab2.Server --fresh
```

Client and provider are one program; the role is the first argument:

```bash
dotnet run --project CoffeeShopLab2.Client -- client   127.0.0.1 5050
dotnet run --project CoffeeShopLab2.Client -- provider 127.0.0.1 5050
```

### Java client

Requires a JDK (built and tested with JDK 21).

```bash
cd java-client
javac -d out src/coffeeshop/*.java
java -cp out coffeeshop.CoffeeShopClient client   127.0.0.1 5050
java -cp out coffeeshop.CoffeeShopClient provider 127.0.0.1 5050
```

### Commands

Typed into either client: `menu`, `order coffee 2 tea 1`,
`deliver coffee 20 croissant 10` (provider only), `ping`, `quit`, `help`.

Because the protocol is plain text, `nc localhost 5050` or
`telnet localhost 5050` also work as clients.

## Protocol

One message per line, terminated by `\n`, at most 1024 bytes.

To the server:

| Message | Meaning |
|---|---|
| `HELLO CLIENT` / `HELLO PROVIDER` | announce the role |
| `MENU` | products and stock |
| `ORDER coffee 2 tea 1` | place an order |
| `DELIVER coffee 20 croissant 10` | provider only |
| `PING` | keep-alive |
| `QUIT` | leave |

From the server:

| Message | Meaning |
|---|---|
| `WELCOME CLIENT` / `WELCOME PROVIDER` | role accepted |
| `MENU coffee=31 tea=12 croissant=4` | the stock |
| `ACCEPTED 7 11` | order 7, ready in 11 seconds |
| `REFUSED croissant 1` | not enough, 1 left |
| `READY 7` | sent later, unprompted, by a worker |
| `DELIVERED coffee=51 ...` | the stock after a delivery |
| `ALERT croissant 3` | to the provider, unprompted |
| `PONG` / `BYE` / `ERROR <text>` | |

## How it works

**Framing.** TCP is a byte stream and keeps no boundaries between messages, so
`LineFramer` holds a buffer and only hands a message out once its `\n` has
arrived; whatever follows stays for next time. A message split across two
receives is reassembled, three messages in one receive come out as three, and a
line longer than the limit is dropped up to the next `\n` instead of being
buffered without end.

**Answer now, announce later.** An order is answered immediately with its
number and preparation time; `READY` arrives on its own once a worker has
finished. A client that waited for a single reply would be blocked for the
whole preparation and could not order again or ask for the menu meanwhile.

**The stock.** One lock, and checking and subtracting happen inside it in
`Stock.TryTake`. Two clients ordering the last croissant at the same moment
cannot both get it: whichever takes the lock second finds 0 and is refused.
Deliveries from the provider take the same lock, so an order and a delivery
landing together never lose each other's change.

**Producer and consumer.** The producers are the sessions, one per connected
client, writing accepted orders into an unbounded channel. The consumers are
the workers. The channel is FIFO, so a free worker always takes the oldest
waiting order.

**The workers.** One per logical processor, each preparing one whole order at a
time. Preparation is `await Task.Delay(seconds, token)` rather than
`Thread.Sleep`: a sleeping thread serves nobody, and `Task.Delay` takes a
cancellation token, which is what lets a worker stop the instant its client
disappears. The count is a capacity limit standing for a fixed number of people
behind the counter, not a CPU sizing decision — the work is pure waiting, so
the workers hold no threads between await points.

**Two senders, one socket.** A worker sends `READY` on a connection whose
session may be writing an answer at that moment, so `LineSocket` serialises
sends with a semaphore and two messages never interleave into one corrupt line.
Receiving needs no such lock, because only the session reads.

**Exactly once.** Once an order is accepted its products have already left the
stock, so every path must either deliver them or return them, never both and
never neither. `Order` holds a small state machine under its own lock
(`TryStartPreparing`, `TryComplete`, `TryCancel`). A worker finishing and a
client vanishing in the same microsecond both call into it and only one wins;
only the call that actually cancels returns the products. Whatever was prepared
is lost, so the next order that takes those products waits for their whole
preparation again.

**Cancelled orders still in the queue** are not pulled out of the channel. The
worker that eventually takes one sees it is no longer waiting and drops it.

**Disappearing clients.** A closed connection or a killed process is noticed at
once: receive returns 0, or throws `ConnectionReset`. A machine whose network
is simply gone sends nothing at all and is not noticed at once, so TCP
keep-alive is enabled on accepted sockets and turns that silence into a reset
after a while. When the provider disappears the shop goes on with the stock it
has.

**Invalid input.** An unknown command, a missing or wrong parameter, a quantity
that is not a positive number, an over-long message and random bytes all get an
`ERROR` line and nothing else is disturbed. Invalid UTF-8 decodes to
replacement characters and then fails to parse, which is the same path.

**Receiving and typing at once.** `READY` and `ALERT` arrive whenever the
server decides, not as answers to anything typed, so both clients receive on
their own and print the moment a message arrives, even while the user is
in the middle of a line. In C# that is a receive loop next to a
`Console.ReadLine` on a pool thread; in Java a daemon receiver thread next to
the main thread reading the keyboard.

## Bonus items

**Two computers in the same network.** The server binds `IPAddress.Any`, so it
already accepts connections from other machines. Start it without
`--loopback`, find its address (`ipconfig` on Windows,
`ipconfig getifaddr en0` on macOS, `hostname -I` on Linux) and pass it to the
client:

```bash
dotnet run --project CoffeeShopLab2.Client -- client 192.168.1.42 5050
```

**The stock survives a restart.** The server writes the amounts to
`stock.state` after every change, through a temporary file that is then moved
into place, so a crash mid-write cannot leave a half-written file. On start-up
it loads that file if it is complete and readable, and otherwise opens with a
random 10 to 50 of each product. `--stock <file>` chooses another file and
`--fresh` ignores the saved one. Stop the server after some orders, start it
again, and `menu` shows the amounts it closed with.

**Two languages.** `java-client/` is the same client and provider written in
Java on `java.net.Socket`, with its own copy of the framing. It speaks the same
text protocol as the C# server, so a Java client and a C# client can both be
connected and ordering at the same time.

## A demonstration run

1. One client: `menu`, then `order coffee 1 croissant 2`. The acceptance comes
   back instantly, the ready message eight seconds later.
2. More clients than the machine has cores, all ordering at once. The log shows
   every worker busy and the rest of the orders queued, each one taken oldest
   first. Then empty one product and have two clients order the last unit at
   the same moment: one is accepted, the other refused.
3. Start the provider. It is told at once about anything already below 5. Order
   a product down past 5 from a client and the alert arrives at the provider
   unprompted. `deliver coffee 20`, then order again.
4. A client orders something long (`order coffee 5`, 20 seconds) and is killed
   while a worker is preparing it. The log shows the worker stopping and the
   products going back; `menu` from another client confirms the amounts.
5. Garbage: `BREW coffee`, `order coffee -1`, `order banana 2`, a
   2000-character line. Each gets an error and nothing else is affected.
6. Run the Java client and the C# client against the same server at once.
7. Stop the server and start it again: the stock is the one it closed with.
