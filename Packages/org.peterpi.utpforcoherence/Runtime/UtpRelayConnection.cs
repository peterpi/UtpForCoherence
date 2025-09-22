using Coherence.Toolkit.Relay;
using Unity.Networking.Transport;
using System.Collections.Generic;
using System;
using UnityEngine;
using Unity.Collections;
using Unity.Networking.Transport.Error;
using System.Threading;
using NUnit.Framework.Constraints;

internal class UtpRelayConnection : IRelayConnection
{
	private UtpRelay m_parent;

	private readonly NetworkConnection m_connection;
	internal NetworkConnection NetworkConnection => m_connection;


	struct QueuedPacket
	{
		public NativeArray<byte> payload;
	}

	private NativeQueue<QueuedPacket> m_receivedPackets; // Instantiated in OnConnectionOpened, disposed in OnConnectionClosed()


	internal UtpRelayConnection (
		UtpRelay parent,
		NetworkConnection nc)
	{
		m_parent = parent;
		m_connection = nc;
	}

	~UtpRelayConnection()
	{
		if (m_receivedPackets.IsCreated)
		{
			Debug.LogError($"A UtpRelayConnection object was GC'd without previously having OnConnectionClosed().  Please report a bug.");
			DiscardReceivedPackets();
			Debug.Assert(!m_receivedPackets.IsCreated);
		}
	}

	public void OnConnectionOpened()
	{
		m_receivedPackets = new(Allocator.Persistent);
	}

	private void DiscardReceivedPackets()
	{
		if (!m_receivedPackets.IsCreated)
			return;
		// Discard anything in m_receivedPackets before disposing it.
		while (m_receivedPackets.TryDequeue(out QueuedPacket qp))
			qp.payload.Dispose();
		m_receivedPackets.Dispose();
	}

	public void OnConnectionClosed()
	{
		DiscardReceivedPackets();
		// Our parent also has some tidying up to do.
		m_parent.HandleConnectionClosure(this);
	}




	// This is called from UtpRelay.Update()
	//
	// Very shortly afterwards there will be a call to our ReceiveMessagesFromClient
	// where we will pass on the data.
	internal void TakeReaderData(DataStreamReader reader)
	{
		NativeArray<byte> copy = new(reader.Length, Allocator.Temp);
		reader.ReadBytes(copy);
		QueuedPacket p = new() { payload = copy };
		m_receivedPackets.Enqueue(p);
	}



	public void ReceiveMessagesFromClient(List<ArraySegment<byte>> buffer)
	{
		// Provide the data that was previously passed to us in TakeReaderData.
		while (m_receivedPackets.TryDequeue(out QueuedPacket p))
		{
			int len = p.payload.Length;
			byte[] managed = new byte[len]; // TODO Recycle these.
			p.payload.CopyTo(managed);
			buffer.Add(managed);
			p.payload.Dispose();
		}
	}


	public void SendMessageToClient(ReadOnlySpan<byte> packetData)
	{
		// It's more convenient for the parent UtpRelay to handle this.
		m_parent.SendForConnection(this, packetData);
	}



}
