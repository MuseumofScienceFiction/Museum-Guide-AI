using UnityEditor;
using UnityEngine;

public class GameSettings : MonoBehaviour {
	public void QuitApplication() {
#if UNITY_EDITOR
		// Stop playing the scene in the Unity editor
		EditorApplication.isPlaying = false;
#else
		// Quit the application in a build
		Application.Quit();
#endif
	}
}
