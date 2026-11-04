// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Subscriber.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Threading.Channels;

namespace BroadcastKit;

internal sealed class Subscriber
{
    public required Guid Id { get; init; }
    public required long Generation { get; init; }
    public required Func<BroadcastMessage, Task> Handler { get; init; }
    public required Channel<BroadcastMessage> Queue { get; init; }
    public required CancellationTokenSource Cancellation { get; init; }

    public Task PumpTask { get; set; } = Task.CompletedTask;
    public volatile bool Active = true;
}
