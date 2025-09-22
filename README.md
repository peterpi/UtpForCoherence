
# UTP For Coherence

## What?

This repository contains a Unity package containing `ITransport` and `IRelay` (etc.) plugins for the [Coherence.io](https://coherence.io/) networking middleware product, allowing client-hosted applications it to communicate using the [Unity Transport Package](https://docs.unity3d.com/Packages/com.unity.transport@2.5/manual/index.html).

It was developed on Unity 6.0 for Coherence 1.8 with UTP 2.5.


## Why?

The Unity Transport Package has some features that make it an attractive trasnport layer for higher-level networking products such as Coherence, especially in a serverless peer-to-peer scenario.  It is the transport layer for both of Unity's own networking libraries Netcode for GameObjects and Netcode for Entities.

Some features of UTP are:

* Easy interoperability with Unity's Lobby (sessions and matchmaking) and Relay services.
* An optional simulator stage for simulating various network conditions.
* UTP itself has a pluggable backend (via `INetworkInterface`) for communication over nonstandard channels (e.g. platform-specific libraries on consoles)


## How?



To use this package with Coherence, first see Coherence's own documentation on [Peer-to-peer](https://docs.coherence.io/hosting/client-hosting) titles, as well as the page on [Implementing Client-hosting](https://docs.coherence.io/hosting/client-hosting/implementing-client-hosting).  This package contains implementations of the  `IRelay`, and `IRelayConnection` interfaces for the hosting client, as well as the `ITransport`, `ITransportFactory` interfaces for non-hosting clients.

See the example project's `SampleClientAndHost.cs` C# file ([here](Assets/UtpForCoherenceSample/SampleClientAndHost.cs)) for an example of a client-hosted session that uses Unity's Lobby and Relay services for "Quick Match" matchmaking between NAT-protected peers.

The general steps are as follows:

### On the Hosting Client

On the client that will act as a hosting client, create an `UtpRelay` instance.  Pass in a callback function that will create the NetworkDriver (NetworkDriver.Create) and the pipeline.

Pass the `UtpRelay` instance in to CoherenceBridge.SetRelay.  Then create and start the replication server as usual, and then call `CoherenceBridge.Connect` to connect to the locally-hosted relay.  As part of that process the `NetworkDriver` callback that you passed in to UtpRelay will be called.

### On the Connecting Client

On peers that wish to connect to the hosting client create a `UtpTransportFactory`, again passing in a callback that will create and configure the NetworkDriver and pipeline.  Pass the factory in to `CoherenceBridge.SetTransport()` before calling `CoherenceBridge.Connect()`.  Your callback function will be called as part of the connection process.

## License

This package is distributed under a friendly MIT-Zero license ([here](Packages/org.peterpi.utpforcoherence/LICENSE.md)).  You can use it in your title with no payment.  No attribution is legally necessary, but a word of thanks is gratefully received.