using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using VRTK;

public class PlayerConnectionScript : NetworkBehaviour {

	public GameObject playerAvatarPrefab;
	public GameObject VRTKLocalRig;
	// Use this for initialization
	void Start () {
		if (!isLocalPlayer)
			return;
		VRTKLocalRig = GameObject.FindGameObjectWithTag ("VRTKLocal");
		CmdSpawnAvatar ();
	}
	
	// Update is called once per frame
	void Update () {
		
	}

	[Command]
	void CmdSpawnAvatar()
	{
		Debug.Log ("Created Avatar on Server");
		GameObject thisAvatar = Instantiate (playerAvatarPrefab);
		NetworkServer.SpawnWithClientAuthority (thisAvatar, connectionToClient);
		RpcSetVRTKRig (thisAvatar);
	}

	[ClientRpc]
	void RpcSetVRTKRig(GameObject avatar)
	{
		PlayerAvatarScript script = avatar.GetComponent<PlayerAvatarScript> ();
		script.VRTKRig = VRTKLocalRig;
	}
}
