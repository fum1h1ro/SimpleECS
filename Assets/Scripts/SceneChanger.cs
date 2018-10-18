using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class SceneChanger : MonoBehaviour {
	static SceneChanger _instance = null;
	Text _sceneName;


	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	static void Initialize() {
		var prefab = Resources.Load<GameObject>("Canvas");
		GameObject.Instantiate(prefab);
	}

	void Awake() {
		if (_instance != null) {
			Destroy(gameObject);
		} else {
			_instance = this;
			DontDestroyOnLoad(gameObject);
		}
	}
	void OnDestroy() {
		if (_instance == this) {
			_instance = null;
		}
	}
	void Start() {
		_sceneName = transform.Find("SceneName").GetComponent<Text>();
		_sceneName.text = SceneManager.GetActiveScene().name;
		SceneManager.activeSceneChanged += (current, next) => { 
			_sceneName.text = next.name;
		};
	}
	public void NextScene() {
		int nscene = SceneManager.sceneCountInBuildSettings;
		var current = SceneManager.GetActiveScene();
		int currentIndex = current.buildIndex;
		int nextIndex = currentIndex + 1;
		if (nextIndex >= nscene) {
			nextIndex = 0;
		}
		SceneManager.LoadScene(nextIndex);
	}
}
