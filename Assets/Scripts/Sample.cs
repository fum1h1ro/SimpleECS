using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SimpleECS;

public unsafe class Sample : MonoBehaviour {
	struct PositionData : IComponentData {
		public Vector3 pos;
	}
	struct RotationData : IComponentData {
		public Quaternion rot;
	}
	class MoveSystem : ComponentSystem {
		NativeArray<PositionData> positionData;
		public float Time;
		//
		public MoveSystem(ArcheType* archeType) : base(archeType) {
		}
		public override void PreChunkIteration(Chunk* chunk) {
			positionData = GetNativeArray<PositionData>(chunk);
		}
		public override void OnUpdate(int count) {
			for (int idx = 0; idx < count; ++idx) {
				var data = positionData[idx];
				data.pos.y = 0.0f;
				var dist = data.pos.magnitude;
				data.pos.y = Mathf.Sin(Time + dist);
				positionData[idx] = data;
			}
		}
	}
	class MakeMatrixSystem : ComponentSystem {
		NativeArray<PositionData> positionData;
		NativeArray<RotationData> rotationData;
		public Matrix4x4[] Matrices;
		//
		public MakeMatrixSystem(ArcheType* archeType) : base(archeType) {
		}
		public override void PreChunkIteration(Chunk* chunk) {
			positionData = GetNativeArray<PositionData>(chunk);
			rotationData = GetNativeArray<RotationData>(chunk);
		}
		public override void OnUpdate(int count) {
			for (int idx = 0; idx < count; ++idx) {
				Matrices[idx] = Matrix4x4.TRS(positionData[idx].pos, rotationData[idx].rot, Vector3.one);
			}
		}
	}

	World _world;
	Matrix4x4[] _matrices;
	MoveSystem _moveSystem;
	MakeMatrixSystem _makeMatrixSystem;
	public Mesh _mesh;
	public Material _material;
	const int Width = 30;
	const int Height = 30;
	const int ObjectCount = Width * Height;

	void Awake() {
		_world = new World();
		var archeType = _world.ArcheTypeManager.GetOrCreateArcheType(typeof(PositionData), typeof(RotationData));
		var xOrigin = -(Width * 0.5f);
		var yOrigin = -(Height * 0.5f);
		for (int y = 0; y < Height; ++y) {
			for (int x = 0; x < Width; ++x) {
				var entity = _world.EntityManager.Create(archeType);
				_world.EntityManager.SetComponentData<PositionData>(entity, new PositionData(){ pos = new Vector3(xOrigin+x, 0, yOrigin+y) });
				_world.EntityManager.SetComponentData<RotationData>(entity, new RotationData(){ rot = Quaternion.identity });
			}
		}
		_matrices = new Matrix4x4[ObjectCount];
		_moveSystem = new MoveSystem(_world.ArcheTypeManager.GetOrCreateArcheType(typeof(PositionData)));
		_makeMatrixSystem = new MakeMatrixSystem(archeType);
		_makeMatrixSystem.Matrices = _matrices;
	}
	void OnDestroy() {
		_world.Dispose();
	}
	void Update() {
		_moveSystem.Time = Time.realtimeSinceStartup;
		_world.Dispatch(_moveSystem);
		_world.Dispatch(_makeMatrixSystem);
		Graphics.DrawMeshInstanced(_mesh, 0, _material, _matrices, ObjectCount, null, ShadowCastingMode.Off, false);
	}
}
