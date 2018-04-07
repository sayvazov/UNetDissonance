using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class PlayerAvatarScript : NetworkBehaviour {

	public GameObject VRTKRig;
	// Use this for initialization
	void Start () {
		
	}
	
	// Update is called once per frame
	void Update () {
		if (VRTKRig) {
			gameObject.transform.position = VRTKRig.transform.position;
			gameObject.transform.rotation = VRTKRig.transform.rotation;
		}
	}

}
