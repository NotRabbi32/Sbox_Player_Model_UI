using Sandbox;
using Sandbox.Network;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
public sealed class PlayerModelManager : Component, Global.IPlayerEvents 
{ 
	[Header( "Visual Templates" )]
	[Property] public Clothing HideBodyClothing { get; set; } 
	// The currently selected model for each player. 
	// Key = Steam ID / Connection ID. 
	// 
	// This lives on the host and survives player GameObject destruction, 
	// so the selection is still there when the player respawns.
	private readonly Dictionary<ulong, string> SelectedModels = new(); 
	private string LastAppliedIdent = ""; 
	private GameObject LastAppliedPlayer; 
	protected override void OnStart() 
	{ 
		var ownable = GameObject.Components.Get<Ownable>(); 

		Log.Info( $"PlayerModelManager started. " 
			+ $"NetworkActive={GameObject.Network.Active}, " 
			+ $"IsOwner={GameObject.Network.IsOwner}, " 
			+ $"NetworkOwnerId={GameObject.Network.OwnerId}, " 
			+ $"SpawnerOwner={(ownable?.Owner?.DisplayName ?? "NULL")}, " +
			$"" + $"SpawnerOwnerId={(ownable?.Owner?.Id.ToString() ?? "NULL")}" ); 
	} 

	/// <summary> 
	/// Called by the local UI when a player selects a model. 
	/// </summary>
	public void SelectModel( string packageIdent ) 
	{ 
		if ( string.IsNullOrWhiteSpace( packageIdent ) ) 
			return; 
		
		if ( !GameObject.Network.Active ) { 
			Log.Error( "PlayerModelManager isn't network active." ); 
			return; 
		} 
		Log.Info( $"Requesting model change: {packageIdent}" ); 
		RequestModelChange( packageIdent ); 
	} 
	/// <summary> 
	/// Runs on the host. 
	/// Determines which player made the request and remembers 
	/// their selected model. 
	/// </summary> 
	[Rpc.Host] 
	private void RequestModelChange( string packageIdent ) { 
		if ( string.IsNullOrWhiteSpace( packageIdent ) ) 
			return; 
		
		var caller = Rpc.Caller; 
		if ( caller == null ) 
		{ 
			Log.Error( "PlayerModelManager: Rpc.Caller was null." ); 
			return; 
		} 
		Log.Info( $"Model request received from {caller.DisplayName} " + $"({caller.Id})" ); 
		
		// --------------------------------------------------------- 
		// Find the PlayerController belonging to the caller. 
		// ---------------------------------------------------------
		var playerController = Scene .GetAllComponents<PlayerController>().FirstOrDefault( p => p.GameObject.IsValid() && p.GameObject.Network.Active && p.GameObject.Network.Owner == caller ); 
		
		if ( playerController == null ) 
		{ 
			Log.Error( $"Could not find PlayerController for {caller.DisplayName}." ); 
			return; 
		} 
		
		var playerObject = playerController.GameObject; if ( !playerObject.IsValid() ) return; 
		// --------------------------------------------------------- 
		// Remember the player's selection. 
		// We use the connection ID rather than the Player GameObject 
		// because the Player GameObject gets destroyed on death. 
		// ---------------------------------------------------------

		SelectedModels[caller.OwnerSteamId] = packageIdent; 
		Log.Info( $"Stored model selection for {caller.DisplayName}: " + $"{packageIdent}" ); 
		
		// --------------------------------------------------------- 
		// Apply immediately to the current player. 
		// ---------------------------------------------------------
		Log.Info( $"Changing {caller.DisplayName}'s model to {packageIdent}" ); 
		BroadcastModelChange( playerObject, packageIdent ); 
	} 
	
	/// <summary> 
	/// Called whenever a Player GameObject is spawned. 
	/// This includes respawns because the old Player GameObject is 
	/// destroyed on death and a new one is created.
	/// </summary> 
	void Global.IPlayerEvents.OnPlayerSpawned( Player player ) { 
		if ( player == null || !player.IsValid() ) return; 

		if ( !Networking.IsHost ) return; 

		var playerObject = player.GameObject; 

		if ( playerObject == null || !playerObject.IsValid() ) return; 

		var connection = player.Network.Owner; 

		if ( connection == null ) 
		{ 
			Log.Warning( $"OnPlayerSpawned: {playerObject.Name} has no network owner." ); 
			return; 
		}

		// Has this player previously selected a custom model?
		if ( !SelectedModels.TryGetValue( connection.OwnerSteamId, out var packageIdent ) ) 
		{ 
			Log.Info( $"OnPlayerSpawned: {connection.DisplayName} has no " + $"saved model selection." ); 
			return; 
		} 
		
		if ( string.IsNullOrWhiteSpace( packageIdent ) ) return; 

		Log.Info( $"OnPlayerSpawned: Reapplying {packageIdent} " + $"to {connection.DisplayName}'s new Player GameObject." ); 

		// IMPORTANT: // The newly spawned player's hierarchy may not be completely 
		// initialized during the exact frame OnPlayerSpawned fires. 
		// BroadcastModelChange -> TryAssignModel already waits for 
		// the renderer and dresser, so this is safe. 
		BroadcastModelChange( playerObject, packageIdent ); 
	} 
	
	
	/// <summary> 
	/// Runs on every client and the host. 
	/// </summary> 
	[Rpc.Broadcast] 
	private void BroadcastModelChange( GameObject targetPlayer, string packageIdent ) { 
		if ( targetPlayer == null || !targetPlayer.IsValid() ) 
			return; 
		if ( string.IsNullOrWhiteSpace( packageIdent ) ) 
			return; 
		Log.Info( $"Applying model {packageIdent} to {targetPlayer.Name}" ); 
		_ = TryAssignModel( targetPlayer, packageIdent ); 
	} 
	
	private async Task TryAssignModel( GameObject targetPlayer, string packageIdent ) { 
		if ( targetPlayer == null || !targetPlayer.IsValid() ) 
			return;
	// The player can have just spawned, so wait for its 
	// renderer and dresser to exist. 
	for ( int i = 0; i < 120; i++ ) { 
			if ( !targetPlayer.IsValid() ) 
				return; 
			var renderer = targetPlayer.GetComponentInChildren<SkinnedModelRenderer>( true ); 
			var dresser = targetPlayer.GetComponentInChildren<Dresser>( true ); 
			if ( renderer != null && dresser != null ) { 
				var model = await DownloadAsset( packageIdent ); 
				if ( model == null ) return; 
				if ( !renderer.IsValid() || !dresser.IsValid() ) return; 
				renderer.Model = model; 
				await ForceStrip( dresser ); 
				LastAppliedIdent = packageIdent; 
				LastAppliedPlayer = targetPlayer; 
				Log.Info( $"Successfully applied {packageIdent} " + $"to {targetPlayer.Name}" ); 
				return; 
			} 
			await Task.Frame(); 
		} 
		Log.Error( $"Timed out waiting for player renderer/dresser on " + $"{targetPlayer.Name}" ); 
	} 
	
	public async Task<Model> DownloadAsset( string packageIdent ) { 
		try { 
			var package = await Package.Fetch( packageIdent, false ); 
			if ( package == null || package.Revision == null ) 
				return null; 
			if ( !package.IsMounted() ) 
				await package.MountAsync(); 
			var assetPath = package.PrimaryAsset; 
			if ( string.IsNullOrWhiteSpace( assetPath ) ) 
				return null; 
			if ( !assetPath.EndsWith( ".vmdl", System.StringComparison.OrdinalIgnoreCase ) ) { 
				Log.Error( $"Primary asset isn't a vmdl: {assetPath}" ); 
				return null; 
			} 
			return await Model.LoadAsync( assetPath ); 
		} 
		catch ( System.Exception ex ) 
		{ Log.Error( $"Failed to load {packageIdent}: {ex.Message}" ); 
			return null; 
		} 
	} 
	
	public async Task ForceStrip( Dresser dresser ) 
	{ 
		if ( dresser == null || !dresser.IsValid() ) return; 
		dresser.Source = Dresser.ClothingSource.Manual; dresser.Clothing.Clear(); 
		await dresser.Apply(); 
		if ( HideBodyClothing != null ) { 
			dresser.Clothing.Add( new ClothingContainer.ClothingEntry { Clothing = HideBodyClothing } ); 
			await dresser.Apply(); 
		} 
	} 
}
