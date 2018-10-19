using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SampleMonoBehaviours : MonoBehaviour {
	const int Width = 100;
	const int Height = 100;
	const int ObjectCount = Width * Height;
	public bool _isRoot;
	Transform _transform;

	void Start() {
		_transform = transform;
		if (_isRoot) {
			var xOrigin = -(Width * 0.5f);
			var yOrigin = -(Height * 0.5f);
			for (int y = 0; y < Height; ++y) {
				for (int x = 0; x < Width; ++x) {
					var obj = GameObject.Instantiate(gameObject);
					var sample = obj.GetComponent<SampleMonoBehaviours>();
					sample._isRoot = false;
					var t = obj.transform;
					t.localPosition = new Vector3(xOrigin+x, 0, yOrigin+y);
					t.localRotation = Quaternion.identity;
				}
			}
			Destroy(gameObject);
		}
	}

	void Update() {
		var pos = _transform.localPosition;
		pos.y = 0.0f;
		var dist = pos.magnitude;
		pos.y = Mathf.Sin(Time.realtimeSinceStartup + dist);
		_transform.localPosition = pos;
	}
}
