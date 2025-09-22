using Coherence.Stats;
using Coherence.Transport;
using UnityEngine;
using Unity.Networking.Transport;

public class UtpTransportFactory : ITransportFactory
{

	private UtpTransport.DriverCreator m_driverCreator;
	internal UtpTransport.DriverCreator DriverCreator => m_driverCreator;

	public UtpTransportFactory (UtpTransport.DriverCreator driverCreator)
	{
		m_driverCreator = driverCreator;
	}


	public ITransport Create(ushort mtu, IStats stats, Coherence.Log.Logger logger)
	{
		logger.Info("UtpTransportFactory.Create()");
		var transport = new UtpTransport(this, stats, logger);
		return transport;
	}
}
