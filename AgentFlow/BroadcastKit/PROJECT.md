# BroadcastKit

## Purpose

`BroadcastKit` provides the process-local, topic-addressed messaging bus used by hosts,
plugins, and GUI services. Routing is by exact topic name, and every subscriber has an
independent bounded queue so a slow or failing handler cannot block other subscribers.

## Core API

- `TopicBus.PublishAsync(...)`: fire-and-forget publication. No reply is expected, and
  publishing to a topic without subscribers is a no-op.
- `TopicBus.Subscribe(...)`: registers a temporary or long-lived handler for one exact topic.
- `TopicBus.RequestAsync(...)`: publishes a request and waits for the first reply whose
  `CorrelationId` matches the request. The temporary reply subscription is registered before
  the request is published, preventing a fast responder from losing the reply.
- `TopicBus.ReplyAsync(request, payload)`: publishes a correlated reply to
  `request.ReplyTo`.

`RequestAsync` uses a five-second default timeout. A caller can pass a different
`TimeSpan`. Timeout, cancellation, handler failure, or bus disposal always removes the
pending request and temporary reply subscription.

## Message Contract

`TopicMessage` carries:

- `MessageId`: unique message identity.
- `Topic`: exact routing topic.
- `Payload`: host-defined payload object.
- `CorrelationId`: optional request/reply correlation identity.
- `ReplyTo`: request-only reply topic.
- `Sequence` and `Timestamp`: diagnostic ordering information.

`TopicBus` does not interpret payloads. Payload meaning is defined by the publisher and
subscriber for a specific topic.

## Request/Reply Example

```csharp
var reply = await bus.RequestAsync(
    "fact.command.request",
    "fact.command.reply",
    requestPayload,
    TimeSpan.FromSeconds(3));

await bus.ReplyAsync(requestMessage, responsePayload);
```

Responders must call `ReplyAsync` on the received request message. Do not reuse
`PublishAsync` for replies unless a caller intentionally implements the same correlation
and `ReplyTo` behavior.

## Concurrency and Lifecycle

- Multiple requests may be pending at once. Each request has its own correlation id and
  reply subscription, so responses are isolated.
- Only the first matching reply completes a request.
- `PendingRequestCount` exposes the current number of outstanding requests for diagnostics.
- `DisposeAsync` fails outstanding requests with `BroadcastDisposedException` and stops all
  subscription pumps.

## Build

```powershell
dotnet build "BroadcastKit.csproj" -c Debug -p:Platform=x64
```
