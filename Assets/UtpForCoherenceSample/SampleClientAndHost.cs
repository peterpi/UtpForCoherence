using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Core;
using UnityEngine.UIElements;
using Coherence;
using Coherence.Toolkit;
using Coherence.Toolkit.ReplicationServer;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Coherence.Connection;
using Coherence.Toolkit.Relay;
using Unity.Networking.Transport.Utilities;
using System.Threading;
using Coherence.Transport;
using Unity.Services.Relay.Models;
using Unity.Services.Authentication;
using System.Linq.Expressions;
using System.Linq;
using Newtonsoft.Json;

public class SampleClientAndHost : MonoBehaviour
{
	[SerializeField]
	private UIDocument m_ui;

	// The parent VisualElement for our buttons.
	private VisualElement m_rootVE;

	[SerializeField]
	private VisualTreeAsset m_hostOrJoin;

	[SerializeField]
	private VisualTreeAsset m_inGameMenu; // ... which consists of a single button "Quit".

	[SerializeField]
	private VisualTreeAsset m_searchResults;

	[SerializeField]
	private VisualTreeAsset m_searchResultRow;

	private CoherenceBridge m_bridge;

	// This is a cancellation source that is cancelled in our OnDestroy().
	// This allows tasks to be cancelled on exiting playmode,
	// even without having the in-game quit buttons clicked.
	//
	// This is necessary to support "fast enter play mode"
	// were the domain is not reloaded.
	private CancellationTokenSource m_destroyCancellation = new();


	private void Awake()
	{
		m_bridge = GetComponent<CoherenceBridge>();
	}


	// Start is called once before the first execution of Update after the MonoBehaviour is created
	void Start()
	{
		Init(); // which will eventually call SetupHostAndJoinButtons().
	}

	private void OnDestroy()
	{
		m_destroyCancellation.Cancel();
		m_destroyCancellation.Dispose();
	}


	private async void Init()
	{
		m_rootVE = m_ui.rootVisualElement;
		await UnityServices.InitializeAsync();
		var authSrv = Unity.Services.Authentication.AuthenticationService.Instance;
		await authSrv.SignInAnonymouslyAsync();
		Debug.Log($"Hello \"{authSrv.PlayerName}\" with id \"{authSrv.PlayerId}\".");
		SetupHostAndJoinButtons();
	}


	private void SetupHostAndJoinButtons()
	{
		m_rootVE.Clear();
		VisualElement hostAndJoin = m_hostOrJoin.Instantiate();
		hostAndJoin.Q<Button>("host").clicked += DoHostSession;
		hostAndJoin.Q<Button>("quickJoin").clicked += () => DoClient();
		hostAndJoin.Q<Button>("search").clicked += Search;
		m_rootVE.Add(hostAndJoin);
	}

	private void SetupInGameMenu(CancellationTokenSource cancel)
	{
		m_rootVE.Clear();
		VisualElement menu = m_inGameMenu.Instantiate();
		Button quit = menu.Q<Button>("quit");
		quit.clicked += cancel.Cancel;
		m_rootVE.Add(menu);
		quit.Focus();
	}


	private async void DoHostSession()
	{
		var lobbySrv = Unity.Services.Lobbies.LobbyService.Instance;

		// Cancellation requested by the user via the on-screen quit button.
		using CancellationTokenSource userCancel = new();

		// The tasks in this method can be cancelled either by the user or by the destruction of this MonoBehaviour:
		using CancellationTokenSource combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token, m_destroyCancellation.Token);
		CancellationToken cancel = combinedCancellation.Token;

		// Some resources that we need to gracefully tidy up in the "finally" below.
		string lobbyId = null;
		Task lobbyHeartbeat = null;
		IReplicationServer rs = null;

		try
		{
			SetupInGameMenu(userCancel);

			// Start the Replication Server locally.
			var rsConfig = HostGetReplicationServerConfig();
			Debug.Log ($"Creating replication server at {rsConfig.UDPPort}, token {rsConfig.Token}.");
			rs = Coherence.Toolkit.ReplicationServer.Launcher.Create(rsConfig);
			rs.Start();

			// Create a Relay allocation.
			int maxPlayers = 4; // Arbitrary
			var relaySrv = Unity.Services.Relay.RelayService.Instance;
			var relayAlloc = await relaySrv.CreateAllocationAsync(maxPlayers);
			Debug.Log($"Relay server is {relayAlloc.AllocationId} at endpoint(s) {string.Join (", ", relayAlloc.ServerEndpoints.Select (x => x.ToString()))}.");

			//  Create a Coherence IRelay that uses the NetworkDriver, and tell the CoherenceBridge to use it.
			UtpRelay.DriverCreator createDriverForRelay = delegate (ref NetworkDriver driver, ref NetworkPipeline pipeline)
			{
				CreateDriverForUtpRelay(ref driver, ref pipeline, relayAlloc.ToRelayServerData("udp"));
			};
			Coherence.Toolkit.Relay.IRelay coherenceRelay = new UtpRelay(NetworkEndpoint.AnyIpv4, m_bridge.Logger, createDriverForRelay);
			m_bridge.SetRelay(coherenceRelay);

			// Connect the CoherenceBridge to the locally-running Replication Server.
			await HostConnectToLocalReplicationServer(cancel);

			// Finally create a joinable Lobby that includes the relay join code as a member-visible data object.
			// This must be the final step in this process, because we must be prepared for other users to quick-join
			// from the very moment the Lobby is created.  Therefore we need to be fully set-up.
			// (Relay listening, bridge connected, etc.)
			Debug.Log($"Querying Relay for join code.");
			string joinCode = await relaySrv.GetJoinCodeAsync(relayAlloc.AllocationId);
			Debug.Log($"Join code is {joinCode}.  Creating Lobby to receive other players...");
			lobbyId = await HostCreateLobbyWithRelayJoinCode(joinCode, maxPlayers);
			Debug.Log($"Created Lobby {lobbyId} holding relay join code {joinCode}.");
			lobbyHeartbeat = HostHeartbeatLobby(lobbyId, cancel);

			// Now just wait until the local user gets bored and requests to quit.
			while (true)
			{
				if (cancel.IsCancellationRequested)
					break;
				if (!m_bridge.IsConnected)
					throw new System.Exception("Lost connection to local replication server.");
				await Task.Yield();
			}
			Debug.Log("DoHostSession cancelled via token.");
		}
		catch (System.Exception x)
		{
			// Log it *now*, and then rethrow.
			// This makes the exception message appear before the "finally" code below,
			// which is more intuitive than catching it further up the callstack and printing it afterwards.
			// ("I've had this error, and now I'm cleaning up...")
			Debug.Log($"Exception: {x.Message}.  This exception will be logged again after our cleanup code below...");
			throw;
		}
		finally
		{
			Debug.Log($"Now cleaning up...");
			if (m_bridge.IsConnected)
				m_bridge.Disconnect(); // UtpRelay.Close(), I guuess?  TODO
			m_bridge.SetRelay(null);

			if (lobbyHeartbeat != null)
				await lobbyHeartbeat; // The Lobby will self-destruct by itself some time after we stop heartbeating it.

			if (rs != null)
			{
				Debug.Log($"Stopping replication server.");
				rs.Stop();
			}

			await HostDeleteLobbyIgnoringFailure(lobbyId);
			SetupHostAndJoinButtons();
		}
	}

	private ReplicationServerConfig HostGetReplicationServerConfig()
	{
		// See method StartReplicationServer from the Steam sample.
		var replicationConfig = new ReplicationServerConfig()
		{
			Mode = Mode.World,
			APIPort = (ushort)RuntimeSettings.Instance.WorldsAPIPort,
			UDPPort = 32001,
			SignallingPort = 32002,
			SendFrequency = 20,
			ReceiveFrequency = 60,
			Token = RuntimeSettings.Instance.ReplicationServerToken,
			DisableThrottling = true,
		};
		return replicationConfig;
	}


	// A UtpRelay.DriverCreator implementation that creates a NetworkDriver and NetworkPipeline,
	// taking the Relay allocation into account.
	void CreateDriverForUtpRelay (
		ref NetworkDriver driver,
		ref NetworkPipeline pipeline,
		RelayServerData relayServerData)
	{
		int mtu = Coherence.Brisk.Brisk.MaxMTU;

		NetworkSettings ns = default;
		ns.WithFragmentationStageParameters(mtu);
		ns.WithRelayParameters(ref relayServerData);

		driver = NetworkDriver.Create(ns);
		pipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage));
	}



	private async Task<string> HostCreateLobbyWithRelayJoinCode (string relayJoinCode, int maxPlayers)
	{
		var lobbySrv = Unity.Services.Lobbies.LobbyService.Instance;
		var authSrv = Unity.Services.Authentication.AuthenticationService.Instance;
		string me = authSrv.PlayerId;
		var memberVisible = Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Member;
		var options = new Unity.Services.Lobbies.CreateLobbyOptions()
		{
			Player = new(me),
			Data = new() { { "RelayJoinCode", new(memberVisible, relayJoinCode) } },
			IsPrivate = false,
		};
		string lobbyName = "x"; // This is unused in this example, but the Lobby service requires a non-empty string.
		Unity.Services.Lobbies.Models.Lobby lobby = await lobbySrv.CreateLobbyAsync(lobbyName, maxPlayers, options);
		return lobby.Id;
	}

	private async Task HostHeartbeatLobby (string lobbyId, CancellationToken cancel)
	{
		// Lobbies are considered "inactive" after 30 seconds of inactivity
		// and will no longer appear in search response or quick-join.
		//
		// https://docs.unity.com/ugs/manual/lobby/manual/heartbeat-a-lobby
		//
		// Therefore we heartbeat our lobby at intervals of less-than 30s.
		//
		var delay = System.TimeSpan.FromSeconds(20);
		var lobbySrv = Unity.Services.Lobbies.LobbyService.Instance;
		try
		{
			while (true)
			{
				await Task.Delay(delay, cancel);
				await lobbySrv.SendHeartbeatPingAsync(lobbyId);
			}
		}
		catch (TaskCanceledException) { }
	}


	private async Task HostConnectToLocalReplicationServer(CancellationToken cancel)
	{
		// Tell the Bridge to connect to the local replication server.
		EndpointData localRsEndpoint = new() // The is the endpoint of the replication server.
		{
			// See InitEndpoint in the steam sample.
			host = RuntimeSettings.Instance.LocalHost,
			port = RuntimeSettings.Instance.LocalWorldUDPPort,
			region = EndpointData.LocalRegion,
			schemaId = RuntimeSettings.Instance.SchemaID,
		};
		var (epOk, epError) = localRsEndpoint.Validate();
		if (!epOk)
			throw new System.Exception("Replication server endpoint invalid: \"{epError}\".");
		Debug.Log($"Connecting to replication server at {localRsEndpoint.host}:{localRsEndpoint.port}({localRsEndpoint.region}).");
		m_bridge.Connect(localRsEndpoint);
		while (m_bridge.IsConnecting)
		{
			await Task.Yield();
			cancel.ThrowIfCancellationRequested();
		}
		Debug.Log($"Connected");
	}


	private async Task HostDeleteLobbyIgnoringFailure(string lobbyId)
	{
		if (string.IsNullOrEmpty(lobbyId))
			return;
		var lobbySrv = Unity.Services.Lobbies.LobbyService.Instance;
		try
		{
			Debug.Log($"Deleting Lobby {lobbyId}.");
			await lobbySrv.DeleteLobbyAsync(lobbyId);
		}
		catch (System.Exception x)
		{
			Debug.LogWarning($"Ignoring {x.GetType()} while deleting Lobby.  Message was \"{x.Message}\".");
		}
	}




	// The common client functionality, shared between quick-join and manual join after searching.
	private async void DoClient (string lobbyId = null)
	{
		var lobbySrv = Unity.Services.Lobbies.LobbyService.Instance;
		using CancellationTokenSource userCancel = new();
		using CancellationTokenSource combinedCancel = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token, m_destroyCancellation.Token);
		CancellationToken cancel = combinedCancel.Token;
		SetupInGameMenu(userCancel);
		Unity.Services.Lobbies.Models.Lobby lobby = null;
		try
		{
			// If the caller provided a lobby id then join that lobby.
			// Otherwise quick-join to join a random free one.
			lobby = string.IsNullOrEmpty(lobbyId) ?
				await lobbySrv.QuickJoinLobbyAsync() :
				await lobbySrv.JoinLobbyByIdAsync(lobbyId);
			cancel.ThrowIfCancellationRequested();

			// The lobby has the Relay join code as a member-visible data item:
			bool hasJoinCode = lobby.Data.TryGetValue("RelayJoinCode", out var relayJoinDataObj);
			if (!hasJoinCode)
				throw new System.Exception($"Joined lobby {lobby.Id}, but it did not have a join code."); // This should never happen.
			string joinCode = relayJoinDataObj.Value;
			Debug.Log($"Joined Lobby {lobby.Id}, which holds join code {joinCode}.  Joining relay.");

			// Join the Relay, using the join code.
			var relaySrv = Unity.Services.Relay.RelayService.Instance;
			Unity.Services.Relay.Models.JoinAllocation relayJoinAlloc = await relaySrv.JoinAllocationAsync(joinCode);
			cancel.ThrowIfCancellationRequested();
			var relayServerData = relayJoinAlloc.ToRelayServerData("udp");
			Debug.Log($"Joined relay at {relayServerData.Endpoint} using join code {joinCode}.  Allocation is {relayJoinAlloc.AllocationId}.");

			// Make a UtpTransportFactory and tell the CoherenceBridge to use that for future connections.
			UtpTransport.DriverCreator createClientDriver = delegate (ref NetworkDriver driver, ref NetworkPipeline pipeline)
			{
				CreateDriverForUtpRelay(ref driver, ref pipeline, relayServerData);
			};
			var transportFactory = new UtpTransportFactory(createClientDriver);
			m_bridge.SetTransportFactory(transportFactory);

			// Tell the CoherenceBridge to connect to the endpoint of the UGS Relay server.
			// The CoherenceBridge thinks that this *is* the replication server.
			Coherence.Connection.EndpointData ep = RelayServerDataToCoherenceEndpoint(relayServerData);
			m_bridge.Connect(ep);

			// Have we observed any frames where m_bridge.IsConnected is true?
			bool hasBeenConnected = false;

			while (!cancel.IsCancellationRequested)
			{
				// If we have previously been in a connected state and now we are not in a connected state
				// then it means that the remote peer has disconnected us.
				hasBeenConnected |= m_bridge.IsConnected;
				if (hasBeenConnected && !m_bridge.IsConnected)
					throw new System.Exception("Disconnected by remote host.");
				await Task.Yield();
			}
			Debug.Log("Voluntary disconnect (user pressed button, or exited play mode).");
		}
		catch (System.Exception x)
		{
			Debug.LogException(x);
		}
		finally
		{
			m_bridge.Disconnect(); // Maybe it's disconnected already; that's OK.
			m_bridge.SetTransportFactory(new DefaultTransportFactory()); // Back to normal.
			if (lobby != null)
				await ClientLeaveLobby(lobby.Id);
			SetupHostAndJoinButtons();
		}
	}


	// Translate a UGS RelayServerData into a Coherence EndpointData.
	private Coherence.Connection.EndpointData RelayServerDataToCoherenceEndpoint (RelayServerData relayServerData)
	{
		// relayServerData.Endpoint.ToString() is of the form 123.456.789.123:12345, including the port number.
		// It's impossible to get the host part separately so we have to regex it here.
		string s = relayServerData.Endpoint.ToString();
		var relayEndpointFormat = new System.Text.RegularExpressions.Regex(@"(?<host>.*):(?<port>\d+)");
		var matchGroups = relayEndpointFormat.Match(s).Groups;
		string host = matchGroups["host"].Value;
		int port = int.Parse(matchGroups["port"].Value);
		Coherence.Connection.EndpointData epData = new() {
			host = host, port = port,
			region = EndpointData.LocalRegion,
			schemaId = RuntimeSettings.Instance.SchemaID
		};
		return epData;
	}




	private async Task ClientLeaveLobby (string lobbyId)
	{
		// Leave the lobby, but don't allow failures in the leaving process to prevent us from continuing.
		var authSrv = Unity.Services.Authentication.AuthenticationService.Instance;
		string me = authSrv.PlayerId;
		try
		{
			var lobbySrv = Unity.Services.Lobbies.LobbyService.Instance;
			await lobbySrv.RemovePlayerAsync(lobbyId, me);
		}
		catch (System.Exception x)
		{
			Debug.LogWarning($"Ignoring failure for client player {me} to leave lobby {lobbyId}: Got {x.GetType()} with {x.Message}");
		}
	}


	private async void Search ()
	{
		m_rootVE.Clear();
		var lobbySrv = Unity.Services.Lobbies.LobbyService.Instance;
		int maxNumResults = 10; // Prevent screen overflow.
		var response = await lobbySrv.QueryLobbiesAsync(new() { Count = maxNumResults });
		var results = response.Results;
		var ve = m_searchResults.Instantiate();
		var back = ve.Q<Button>("back");
		back.clicked += SetupHostAndJoinButtons;
		ve.Q<Label>("count").text = $"{results.Count} result(s)";
		var rowParent = ve.Q("rows");
		foreach (var result in results)
		{
			string id = result.Id;
			var row = m_searchResultRow.Instantiate();
			row.Q<Label>("uuid").text = id;
			row.Q<Button>("join").clicked += () => DoClient(id);
			rowParent.Add(row);
		}
		m_rootVE.Add(ve);
		// Focus the first join button if there are any response,
		// otherwise the back button.
		var buttonToFocus = results.Any() ? ve.Q<Button>("join") : back;

	}
	

}
