using Coherence.Toolkit.Relay;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Error;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
public class UtpRelay : IRelay
{

	public CoherenceRelayManager RelayManager { get; set; }


	public delegate void DriverCreator(ref NetworkDriver driver, ref NetworkPipeline pipeline);

	private NetworkEndpoint m_ep; // The endpoint that we'll bind to before calling Listen() and Accept().
	private DriverCreator m_driverCreator;
	private NetworkDriver m_driver;
	private NetworkPipeline m_pipeline;
	private Coherence.Log.Logger m_log;

	private Dictionary<NetworkConnection, UtpRelayConnection> m_relayConnectionsByNetworkConnection = new();


	// We schedule a send job each time one of our UtpRelayConnection objects gets SendMessageToClient called on it.
	// Each invocation is dependent on the previous one having completed.
	private JobHandle m_lastSend;


	public UtpRelay(
		NetworkEndpoint ep,
		Coherence.Log.Logger logger,
		DriverCreator dc = null)
	{
		m_ep = ep;
		m_driverCreator = dc != null ? dc : DefaultCreateDriver;
		m_log = logger;
	}


	private void DefaultCreateDriver (ref NetworkDriver driver, ref NetworkPipeline pipeline)
	{
		NetworkSettings ns = default;
		ns.WithFragmentationStageParameters(payloadCapacity: Coherence.Common.ConnectionSettings.DEFAULT_MTU);
		driver = NetworkDriver.Create(ns);
		pipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage));
	}

	// This is called when the hosting client connects to the replication server.
	// We create the NetworkDriver and NetworkPipeline, and start listening.
	public void Open ()
	{
		int ret = 0;
		Debug.Assert(!m_driver.IsCreated);
		m_driverCreator(ref m_driver, ref m_pipeline);
		if (!m_driver.IsCreated)
			throw new System.Exception("Caller-supplied DriverCreator did not result in m_driver.IsCreated.");
		ret = m_driver.Bind(m_ep);
		if (ret != 0)
			throw new System.Exception($"Failed to bind to local endpoint {m_ep}.  Failed with {ret} (0x{ret:x8}).");
		ret = m_driver.Listen();
		if (ret != 0)
			throw new System.Exception($"NetworkDriver failed to Listen() after binding to {m_ep}.  Error code was {ret} (0x{ret:x8}).");
	}


	// This is called BEFORE (but on the same frame as) ReceiveMessagesFromClient and SendMessageToClient on the individual IRelayConnection instances.
	public void Update()
	{
		Debug.Assert(m_driver.IsCreated); // Does Update() get called outside of an Open()/Close() pair?
		// Make sure that m_driver is ready with any data received this frame:
		m_driver.ScheduleUpdate(m_lastSend).Complete();
		AcceptConnections();
		ProcessDriverEvents();
	}

	private void AcceptConnections()
	{
		// A loop so that in theory we could accept more than one new connection per frame.
		while (true)
		{
			NetworkConnection nc = m_driver.Accept();
			if (nc == default)
				break;
			m_log.Info($"UtpRelay accepts new connection from {nc}.");

			var conn = new UtpRelayConnection(this, nc);
			m_relayConnectionsByNetworkConnection.Add(nc, conn);
			RelayManager.OpenRelayConnection(conn);
		}
	}

	private void ProcessDriverEvents()
	{
		// Connections that experience a disconnect event get queued up here and processed at the end,
		// after all the PopEvent calls have completed.
		using var disconnections = new NativeList<NetworkConnection>(Allocator.Temp);
		// TODO Store the disconnect reason as well.

		while (true)
		{
			NetworkEvent.Type evt = m_driver.PopEvent(out NetworkConnection nc, out DataStreamReader reader);
			if (evt == NetworkEvent.Type.Empty)
				break;

			bool found = m_relayConnectionsByNetworkConnection.TryGetValue(nc, out var relayConn);
			Debug.Assert(found && relayConn != null);
			Debug.Assert(relayConn.NetworkConnection == nc);
			switch (evt)
			{
				case NetworkEvent.Type.Data:
					relayConn.TakeReaderData(reader);
					break;
				case NetworkEvent.Type.Disconnect:
					disconnections.Add(nc);
					break;
				default:
					Debug.LogError($"Unhandled event type {evt}.  Please report a bug.");
					break;
			}
		}


		foreach (NetworkConnection nc in disconnections)
		{
			m_relayConnectionsByNetworkConnection.TryGetValue(nc, out var disconnectedRelayConnection);
			RelayManager.CloseAndRemoveRelayConnection(disconnectedRelayConnection);
		}
	}



	// Called from UtpRelayConnection.OnConnectionClosed().
	internal void HandleConnectionClosure (UtpRelayConnection caller)
	{
		NetworkConnection nc = caller.NetworkConnection;
		bool found = m_relayConnectionsByNetworkConnection.Remove(nc, out UtpRelayConnection removedConnection);
		bool match = removedConnection == caller; // We assert for this below.
#if COHERENCE_LOG_DEBUG
		m_log.Debug($"{GetType()} removed UtpRelayConnection for NetworkConnection {nc} (found = {found}, match = {match}).");
#endif
		Debug.Assert(found);
		Debug.Assert(removedConnection == caller);
		m_lastSend.Complete(); // Any current jobs must have completed before calling NetworkDriver.Disconnect().
		int ret = m_driver.Disconnect(nc);
		bool ok = ret == 0;
		if (!ok)
			m_log.Warning($"{GetType()}: Ignoring error code {ret} from NetworkDriver.Disconnect on {nc}.");
		// Disconnect requires a full ScheduleUpdate() to complete before it can be considered done
		// (not just a not just a ScheduleFlushSend()).
		m_lastSend = m_driver.ScheduleUpdate(m_lastSend);
	}

	// This is called when the host disconnects from the replication server.
	public void Close ()
	{
		m_log.Debug("Close");

		// All the individual connections should have had OnConnectionClosed called on them by now.
		Debug.Assert(m_relayConnectionsByNetworkConnection.Count == 0);

		// Allow the NetworkDriver to complete any work and then dispose it.
		m_lastSend.Complete();
		m_driver.Dispose();
	}


	// UtpRelayConnection calls this from its SendMessageToClient.
	// It's more convenient to handle the request here.
	internal void SendForConnection (UtpRelayConnection caller, ReadOnlySpan<byte> payload)
	{
		NetworkConnection nc = caller.NetworkConnection;
		int len = payload.Length;
#if COHERENCE_LOG_TRACE
		m_log.Trace($"{GetType()} send {len}.");
#endif

		// Temp Copy:  This is a hack.
		//
		// NetworkDriver.BeginSend() hands us back (as an "out" parameter) an instance of the Unity class DataStreamWriter.
		// This is what is subsequently used by the caller to actually provide the data to be sent.
		//
		// DataStreamWriter has overloads for its method WriteBytes() taking either a Span<byte> or a NativeArray<byte>,
		// but NOT the ReadOnlySpan<byte> that Coherence passes to us.
		//
		// Therefore we have to create a temporary NativeArray copy, and *that* is what we pass to WriteBytes.
		using var tempBuf = new NativeArray<byte>(len, Allocator.Temp);
		payload.CopyTo(tempBuf.AsSpan());


		// Any previous send job must be complete before calling BeginSend.
		m_lastSend.Complete();


		// BeginSend / EndSend / AbortSend.
		int ret = m_driver.BeginSend(m_pipeline, nc, out var writer, len);
		if (ret != 0)
		{
			StatusCode sc = (StatusCode)ret;
#if COHERENCE_LOG_DEBUG
			Debug.Log($"{GetType()} Failed to BeginSend for {len} byte(s) on connection {nc}: {sc}");
#endif
			return;
		}
		bool ok = writer.WriteBytes(tempBuf);
		if (!ok)
		{
#if COHERENCE_LOG_DEBUG
			m_log.Debug($"{GetType()} failed to WriteBytes for {tempBuf.Length} byte(s).  Aborting send.");
#endif
			m_driver.AbortSend(writer);
			return;
		}
		ret = m_driver.EndSend(writer);
		int expected = tempBuf.Length;
		if (ret != expected)
		{
#if COHERENCE_LOG_DEBUG
			m_log.Debug($"{GetType()}: EndSend of {tempBuf.Length} returned {ret}, expected {expected}.");
#endif
		}

		// The data hasn't actually been sent yet; it's sitting in internal buffers.
		//
		// So when to dispatch a flush send job?
		//
		// We'd like to send any data THIS FRAME, before the ScheduleUpdate() call in next frame's call to our Update() method.
		//
		// An IRelay does NOT get any sort of notification that all SendMessageToClient calls have been issued for the current frame.
		// If it did then that would be the perfect place to schedule the NetworkDriver send job there.
		//
		// The next-best option is to schedule a flush on EVERY call.
		// The documentation for ScheduleFlushSend is that it is very cheap to schedule multiple times.
		m_lastSend = m_driver.ScheduleFlushSend(m_lastSend);
	}

}
