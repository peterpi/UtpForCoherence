using UnityEngine;
using System.Collections.Generic;
using Coherence.Common;
using Coherence.Transport;
using Coherence.Connection;
using Coherence.Brook;
using Unity.Networking.Transport;
using System.Net;
using Coherence.Brook.Octet;
using System.Threading;
using Unity.Collections;
using System;
using Unity.Jobs;
using Coherence.Log;
using Unity.Networking.Transport.Error;
using Coherence.Stats;
using Unity.Networking.Transport.Utilities;


public class UtpTransport : ITransport
{

	private Coherence.Log.Logger m_log;

	private readonly UtpTransportFactory m_parent;

	// These are all created in Open()
	private NetworkDriver m_driver;
	private NetworkPipeline m_pipeline;
	private NetworkConnection m_connection;


	private IStats m_stats;

	public event System.Action OnOpen;
	public event System.Action<ConnectionException> OnError;

	public delegate void DriverCreator(ref NetworkDriver driver, ref NetworkPipeline pipeline);
	// m_parent has one, shared by all UtpTransport instances.

	public TransportState State { get; private set; }

	public bool IsReliable => false; // It *could* be, if the pipeline was configured that way.

	public bool CanSend => true;

	public int HeaderSize => 0;

	public string Description => "Unity Transport Package com.unity.transport";


	// Any previous (and potentially *ongoing*) send jobs.
	// In Receive(), we call m_driver.ScheduleUpdate passing this in as a dependency.
	private JobHandle m_previousSend;

	/// <summary>
	/// Should we call NetworkDriver.ScheduleFlushSend().Complete() on the main thread
	/// after sending the packages with BeginSend/EndSend.
	/// 
	/// The documentation for ScheduleFlushSend says
	/// "It should be lightweight enough to schedule multiple times per
	/// tick to improve latency if there's a significant amount of packets being sent.".
	/// With that in mind, this property defaults to true.
	/// 
	/// </summary>
	public bool ScheduleAndCompleteOnMainThread { get; set; } = true;

	private struct QueuedPacket
	{
		public uint serialNo; // Useful for debugging.
		public NativeArray<byte> payload;
	};

	private NativeQueue<QueuedPacket> m_sendQueue = new(Allocator.Persistent);

	private uint m_nextSendSerial = 1;

	private static SynchronizationContext s_mainThread = SynchronizationContext.Current;

	internal UtpTransport(
		UtpTransportFactory parent,
		IStats stats,
		Coherence.Log.Logger log)
	{
		m_parent = parent;
		m_stats = stats;
		m_log = log;
		log.Info("New UtpTransport.");
		Debug.Assert(s_mainThread != null);
		State = TransportState.Closed;
	}

	private void DiscardSendQueue()
	{
		while (m_sendQueue.TryDequeue(out QueuedPacket qp))
			qp.payload.Dispose();
		m_sendQueue.Clear();
	}

	~UtpTransport()
	{
		DiscardSendQueue();
		m_sendQueue.Dispose();
	}

	public void Open(EndpointData ep, ConnectionSettings settings)
	{
		Debug.Assert(!m_driver.IsCreated);
		m_parent.DriverCreator(ref m_driver, ref m_pipeline);
		if (!m_driver.IsCreated)
			throw new System.Exception("User-supplied DriverCreator failed to create NetworkDriver");

		// If there are any problems in the remainder of this method then Dispose of m_driver so that our Status property goes back to Closed.
		// (See catch below)
		try
		{
			// Connection: Convert the Coherence EndpointData into a UTP NetworkEndpoint.
			NetworkEndpoint nep = NetworkEndpoint.Parse(ep.host, (ushort)ep.port, NetworkFamily.Ipv4);
#if COHERENCE_LOG_DEBUG
			m_log.Debug($"UtpTransport.Open:  Driver connecting to NetworkEndpoint {nep}, from Coherence EndpointData {ep.host}:{ep.port}.");
#endif
			m_connection = m_driver.Connect(nep);
			if (m_connection == default)
			{
				string err = $"Unexpected failure from NetworkDriver.Connect({nep}): Connection was {m_connection}.";
				m_log.Debug(err);
				throw new System.Exception(err);
			}
#if COHERENCE_LOG_DEBUG
			m_log.Debug($"{GetType()}: Connecting using local endpoint {m_driver.GetLocalEndpoint()}.");
#endif
			State = TransportState.Opening;
			// Don't invoke OnOpen yet.  Wait until the NetworkEvent comes out of the connection.
		}
		catch
		{
			Close();
			throw;
		}
	}

	public void Receive(List<(IInOctetStream, IPEndPoint)> buffer)
	{
		m_driver.ScheduleUpdate(m_previousSend).Complete();
		while (true)
		{
			var eventType = m_connection.PopEvent(m_driver, out var reader);
			switch (eventType)
			{
				case NetworkEvent.Type.Empty:
					return;
				case NetworkEvent.Type.Connect:
					State = TransportState.Open;
					OnOpen?.Invoke();
					break;
				case NetworkEvent.Type.Data:
#if COHERENCE_LOG_TRACE
					m_log.Trace($"{GetType()}: Recv {reader.Length} byte(s).");
#endif
					byte[] bytes = new byte[reader.Length]; // TODO Recycle these.
					reader.ReadBytes(bytes);
					Debug.Assert(!reader.HasFailedReads);
					var stream = new InOctetStream(bytes);  // TODO Recycle these.
					buffer.Add((stream, default));
					m_stats.TrackIncomingPacket((uint)reader.Length);
					break;
				case NetworkEvent.Type.Disconnect:
					// The documentation for Type.Disconnect says that a single byte is available,
					// holding a DisconnectReason.
					byte b = reader.ReadByte();
					DisconnectReason reason = (DisconnectReason)b;
#if COHERENCE_LOG_DEBUG
					m_log.Debug($"Got NetworkEvent.Type.Disconnect with reason {reason}.", ("reason", reason));
#endif
					OnError?.Invoke(new ConnectionException(reason.ToString())); // This ends up calling our own Close() method, which disposes m_driver.
					return;  // RETURN; not "break".  Fully exit this method right now.  Do not continue with the PopEvent loop since m_driver has now been disposed.
				default:
					Debug.Assert(false);
					break;
			}
		}
	}


	public void Send(IOutOctetStream stream)
	{
		ArraySegment<byte> payload = stream.Close();
		NativeArray<byte> copy = new NativeArray<byte>(payload.Count, Allocator.TempJob);
		for (int i = 0; i < payload.Count; ++i)
			copy[i] = payload[i]; // I couldn't find a suitable .CopyFrom() or .CopyTo() on payload nor on copy.

		lock (this)
		{
			QueuedPacket qp = new() { payload = copy, serialNo = m_nextSendSerial++ };
#if COHERENCE_LOG_TRACE
			m_log.Trace($"Queueing packet {qp.serialNo} for main-thread processing.");
#endif
			m_sendQueue.Enqueue(qp);
		}
		s_mainThread.Post(ProcessSendQueue_staticWrapper, this);
	}

	private static void ProcessSendQueue_staticWrapper(object o)
	{
		var self = o as UtpTransport;
#if COHERENCE_LOG_TRACE
		self.m_log.Trace($"{self.GetType()}: Main-thread processing for connection {self.m_connection}.");
#endif
		self.ProcessSendQueue();
	}

	private void ProcessSendQueue()
	{
		lock (this)
		{
			if (!m_sendQueue.IsCreated || m_sendQueue.Count == 0)
				return; // For example if we had Close() called between this method call being scheduled and now.

			while (m_sendQueue.TryDequeue (out QueuedPacket qp))
			{
				try
				{
					SendOne(qp);
				}
				finally
				{
					qp.payload.Dispose();
				}
			}
			m_previousSend = m_driver.ScheduleFlushSend(m_previousSend);
		}
	}

	private void SendOne(QueuedPacket qp)
	{
		int size = qp.payload.Length;
		if (!m_driver.IsCreated)
		{
#if COHERENCE_LOG_DEBUG
			Debug.Log($"{GetType()}: Omitting send of queued packet of {size} byte(s) since the NetworkDriver has been Disposed (or was never created).");
#endif
			return;
		}
		int ret = m_driver.BeginSend(m_pipeline, m_connection, out DataStreamWriter writer, size);
		bool ok = ret == 0;
		if (!ok)
		{
#if COHERENCE_LOG_DEBUG
			var status = (Unity.Networking.Transport.Error.StatusCode)ret;
			string msg = $"Ignoring error int {ret} (hex 0x{ret:x8}, status {status}) from NetworkDriver.BeginSend() for connection {m_connection} when sending {size} byte(s).  The data has not been sent and is now lost.";
			m_log.Debug(msg, ("ret", ret), ("status", status), ("connection", m_connection), ("size", size));
#endif
			// Beyond that, do nothing.
			// We do not report the error further up.  We do not invoke OnError(), etc.
			// We assume that this error is *temporary* in nature (e.g. buffers full)
			// and that subsequent Send() calls might succeed.
			//
			// It's not our responsibilty to declare that this error was fatal.
			//
			// To both the local client and the remote server this will appear just like a lost packet.
			//
			// If the problem persists for long enough then the connection will time-out anyway.
			return;
		}

		ok = writer.WriteBytes(qp.payload);
		if (!ok)
		{
#if COHERENCE_LOG_DEBUG
			m_log.Debug($"{GetType()} failed to WriteBytes for {size} byte(s).  Aborting send.");
#endif
			m_driver.AbortSend(writer);
			return;
		}

		ret = m_driver.EndSend(writer);
		int expected = size;
		if (ret != expected)
		{

#if COHERENCE_LOG_DEBUG
			var status = (Unity.Networking.Transport.Error.StatusCode)ret;
			string msg = $"Ignoring error int {ret} (hex 0x{ret:x8}, status {status}) from NetworkDriver.EndSend after sending {size} byte(s) on connection {m_connection}.  The data is probably lost.";
			m_log.Debug(msg, ("ret", ret), ("status", status), ("connection", m_connection), ("size", size));
#endif
			// See comment above about *not* declaring an error here.
		}
	}



	public void PrepareDisconnect()
	{
		// Do nothing. In particular DO NOT close m_connection.
		// Coherence sends data via this transport AFTER calling this method.
#if COHERENCE_LOG_DEBUG
		m_log.Debug($"{GetType()}: PrepareDisconnect.");
#endif
	}

	public void Close()
	{
#if COHERENCE_LOG_DEBUG
		m_log.Debug($"{GetType()}: Close().");
#endif
		lock (this)
		{
			DiscardSendQueue();
			if (m_driver.IsCreated)
			{
				m_driver.Disconnect(m_connection);
				m_previousSend = m_driver.ScheduleUpdate(m_previousSend); // Send the disconnect packet.
				m_previousSend.Complete();
				m_driver.Dispose();
				Debug.Assert(!m_driver.IsCreated); // That's my understanding of Dispose(); it makes IsCreated go false.
			}
			State = TransportState.Closed;
		}
	}

}
