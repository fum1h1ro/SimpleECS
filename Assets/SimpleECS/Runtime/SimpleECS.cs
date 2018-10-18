using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Debug = UnityEngine.Debug;

namespace SimpleECS {
	public class World : IDisposable {
		EntityManager _entityManager;
		EntityDataManager _entityDataManager;
		ArcheTypeManager _archeTypeManager;
		ComponentBlockManager _componentBlockManager;
		//
		public World() {
			_entityDataManager = new EntityDataManager();
			_archeTypeManager = new ArcheTypeManager();
			_componentBlockManager = new ComponentBlockManager(_entityDataManager);
			_entityManager = new EntityManager(_entityDataManager, _archeTypeManager, _componentBlockManager);
		}
		public void Dispose() {
			_entityManager.Dispose();
			_componentBlockManager.Dispose();
			_archeTypeManager.Dispose();
			_entityDataManager.Dispose();
		}
		public EntityManager EntityManager {
			get { return _entityManager; }
		}
		public ArcheTypeManager ArcheTypeManager {
			get { return _archeTypeManager; }
		}
		public void Dispatch(ComponentSystem system) {
			_componentBlockManager.Dispatch(system);
		}
	}
	public struct Entity {
		public int Index;
	}
	public interface IComponentData {
	}
	public unsafe abstract class ComponentSystem {
		public ArcheType* ArcheType { get; private set; }
		internal ComponentBlock Block;

		protected ComponentSystem(ArcheType* archeType) {
			ArcheType = archeType;
		}
		protected NativeArray<T> GetNativeArray<T>(Chunk* chunk) where T: struct, IComponentData {
			var hash = typeof(T).GetHashCode();
			int componentIndex = ArcheType->GetIndex(hash);
			Assert.IsTrue(componentIndex >= 0);
			var buf = Block.GetComponentDataArray(chunk, componentIndex);
			var len = Block.GetComponentDataCount(chunk, componentIndex);
			var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(buf, len, Allocator.Persistent);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, AtomicSafetyHandle.GetTempUnsafePtrSliceHandle());
#endif
			return array;
		}
		public abstract void PreChunkIteration(Chunk* chunk);
		public abstract void OnUpdate(int count);
	}
	public class BitVector : IDisposable {
		NativeArray<ulong> _bits;
		int _capacity;

		public BitVector() {
			Capacity = 16;
		}
		public BitVector(int capacity) {
			Capacity = capacity;
		}
		public void Dispose() {
			if (_bits.IsCreated) _bits.Dispose();
		}
		public unsafe int Capacity {
			get { return _capacity; }
			set {
				if (_capacity >= value) return;
				int nblock = ((value-1) / 64 + 1);
				var newbits = new NativeArray<ulong>(nblock, Allocator.Persistent);
				if (_bits.IsCreated) {
					var oldptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_bits);
					var newptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(newbits);
					UnsafeUtility.MemCpy(newptr, oldptr, sizeof(ulong) * _bits.Length);
					_bits.Dispose();
				}
				_bits = newbits;
				_capacity = value;
			}
		}
		public void Set(int idx) {
			Assert.IsTrue(idx < _capacity);
			int blk = idx >> 6;
			int m = idx & 0x3f;
			_bits[blk] |= ((ulong)1<<m);
		}
		public void Clear(int idx) {
			Assert.IsTrue(idx < _capacity);
			int blk = idx >> 6;
			int m = idx & 0x3f;
			_bits[blk] &= ~((ulong)1<<m);
		}
		public bool Check(int idx) {
			Assert.IsTrue(idx < _capacity);
			int blk = idx >> 6;
			int m = idx & 0x3f;
			return (_bits[blk] & ((ulong)1<<m)) != 0;
		}
		public int FindCleared() {
			for (int blockIndex = 0; blockIndex < _bits.Length; ++blockIndex) {
				if (_bits[blockIndex] != 0xffffffffffffffff) {
					var block = _bits[blockIndex];
					int idx = blockIndex * 64;
					for (int bitIndex = 0; bitIndex < 64; ++bitIndex, ++idx) {
						if (idx >= _capacity) return -1;
						if ((block & ((ulong)1<<bitIndex)) == 0) {
							return (blockIndex << 6) + bitIndex;
						}
					}
				}
			}
			return -1;
		}
	}
	//
	public class EntityManager : IDisposable {
		int _capacity = 0;
		int _count = 0;
		int _lastUnused = 0;
		NativeArray<Entity> _entities;
		BitVector _occupation = new BitVector();
		EntityDataManager _dataManager;
		ArcheTypeManager _archeTypeManager;
		ComponentBlockManager _componentBlockManager;
		//
		internal EntityManager(EntityDataManager dataManager, ArcheTypeManager archeTypeManager, ComponentBlockManager componentBlockManager) {
			_dataManager = dataManager;
			_archeTypeManager = archeTypeManager;
			_componentBlockManager = componentBlockManager;
			Capacity = 16;
		}
		public void Dispose() {
			if (_entities.IsCreated) _entities.Dispose();
			_occupation.Dispose();
		}
		public unsafe int Capacity {
			get { return _capacity; }
			set {
				if (_capacity >= value) return;
				var newentities = new NativeArray<Entity>(value, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				if (_entities.IsCreated) {
					var oldptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_entities);
					var newptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(newentities);
					UnsafeUtility.MemCpy(newptr, oldptr, sizeof(Entity) * _capacity);
					_entities.Dispose();
				}
				var appendBegin = _capacity;
				_entities = newentities;
				_occupation.Capacity = value;
				_capacity = value;
				InitializeAppendArea(appendBegin);
				_dataManager.Capacity = value; // sync
			}
		}
		public int Count {
			get { return _count; }
		}
		unsafe void InitializeAppendArea(int begin) {
			var buf = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_entities);
			for (int i = begin; i < _capacity; ++i) {
				var p = (Entity*)buf + i;
				p->Index = -1;
			}
		}
		public unsafe Entity Create(ArcheType* archeType=null) {
			Entity entity = new Entity();
			Create(ref entity, archeType);
			return entity;
		}
		public unsafe void Create(ref Entity entity, ArcheType* archeType=null) {
			int idx = -1;
again:
			if (0 <= _lastUnused && _lastUnused < _capacity && !_occupation.Check(_lastUnused)) {
				idx = _lastUnused++;
			}
			if (idx < 0) {
				idx = _occupation.FindCleared();
			}
			if (idx < 0) {
				Capacity = Capacity * 2;
				goto again;
			}
			var buf = (Entity*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_entities);
			buf[idx].Index = idx;
			entity.Index = idx;
			_occupation.Set(idx);
			++_count;
			_dataManager.Clear(entity.Index);
			//
			if (archeType != null) {
				SetArcheType(entity, archeType);
			}
		}
		public void Destroy(Entity entity) {
			Destroy(ref entity);
		}
		public unsafe void Destroy(ref Entity entity) {
			Assert.IsTrue(0 <= entity.Index && entity.Index < _capacity);
			var data = _dataManager.Get(entity.Index);
			if (data->ArcheType != null) {
				_componentBlockManager.Free(data->ArcheType, data->Chunk, data->IndexInChunk);
			}
			int idx = entity.Index;
			var buf = (Entity*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_entities);
			buf[idx].Index = -1;
			entity.Index = -1;
			_occupation.Clear(idx);
			--_count;
			_lastUnused = idx;
		}
		public unsafe void AddComponent<T>(Entity entity) where T: struct, IComponentData {
			var data = _dataManager.Get(entity.Index);
			var type = typeof(T);
			if (data->ArcheType == null) {
				data->ArcheType = _archeTypeManager.GetOrCreateArcheType(type);
				data->IndexInChunk = _componentBlockManager.Allocate(data->ArcheType, &data->Chunk, entity.Index);
			} else {
				var newArcheType = _archeTypeManager.CreateArcheType(data->ArcheType, type);
				Chunk* newChunk = null;
				var newIndexInChunk = _componentBlockManager.Allocate(newArcheType, &newChunk, entity.Index);
				_componentBlockManager.Copy(newArcheType, newChunk, newIndexInChunk, data->ArcheType, data->Chunk, data->IndexInChunk);
				_componentBlockManager.Free(data->ArcheType, data->Chunk, data->IndexInChunk);
				data->ArcheType = newArcheType;
				data->Chunk = newChunk;
				data->IndexInChunk = newIndexInChunk;
			}
		}
		public unsafe void SetArcheType(Entity entity, ArcheType* newArcheType) {
			var data = _dataManager.Get(entity.Index);
			if (data->ArcheType == null) {
				data->ArcheType = newArcheType;
				data->IndexInChunk = _componentBlockManager.Allocate(data->ArcheType, &data->Chunk, entity.Index);
			} else {
				Chunk* newChunk = null;
				var newIndexInChunk = _componentBlockManager.Allocate(newArcheType, &newChunk, entity.Index);
				_componentBlockManager.Copy(newArcheType, newChunk, newIndexInChunk, data->ArcheType, data->Chunk, data->IndexInChunk);
				_componentBlockManager.Free(data->ArcheType, data->Chunk, data->IndexInChunk);
				data->ArcheType = newArcheType;
				data->Chunk = newChunk;
				data->IndexInChunk = newIndexInChunk;
			}
		}
		public unsafe void SetComponentData<T>(Entity entity, T componentData) where T: struct, IComponentData {
			var data = _dataManager.Get(entity.Index);
			if (data->ArcheType == null) return;
			var block = _componentBlockManager.GetOrCreateBlock(data->ArcheType);
			var hash = typeof(T).GetHashCode();
			int componentIndex = data->ArcheType->GetIndex(hash);
			Assert.IsTrue(componentIndex >= 0);
			var buf = block.GetComponentDataArray(data->Chunk, componentIndex);
			var len = block.GetComponentDataCount(data->Chunk, componentIndex);
			var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(buf, len, Allocator.Persistent);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, AtomicSafetyHandle.GetTempUnsafePtrSliceHandle());
#endif
			array[data->IndexInChunk] = componentData;
		}

		[Conditional("DEVELOPMENT_BUILD")]
		[Conditional("UNITY_EDITOR")]
		public unsafe void DumpEntity(Entity entity, int idx) {
			var sb = new System.Text.StringBuilder();
			sb.AppendFormat("Entity:{0}\n", entity.Index);
			var entityData = _dataManager.Get(entity.Index);
			sb.AppendFormat("  ArcheType: {0:x8}\n", (ulong)entityData->ArcheType);
			sb.AppendFormat("  Chunk: {0:x8}\n", (ulong)entityData->Chunk);
			sb.AppendFormat("  IndexInChunk: {0}\n", entityData->IndexInChunk);
			Debug.Log(sb.ToString());
		}
		[Conditional("DEVELOPMENT_BUILD")]
		[Conditional("UNITY_EDITOR")]
		public void Dump() {
			for (int i = 0; i < _entities.Length; ++i) {
				var entity = _entities[i];
				if (_occupation.Check(i)) {
					DumpEntity(_entities[i], i);
				}
			}
		}
	}
	internal unsafe class EntityDataManager : IDisposable {
		public struct EntityData {
			public ArcheType* ArcheType;
			public Chunk* Chunk;
			public int IndexInChunk;
		}
		int _capacity;
		NativeArray<EntityData> _entityData;
		//
		public EntityDataManager() {
			Capacity = 16;
		}
		public void Dispose() {
			if (_entityData.IsCreated) _entityData.Dispose();
		}
		public unsafe int Capacity {
			get { return _capacity; }
			set {
				if (_capacity >= value) return;
				var newdata = new NativeArray<EntityData>(value, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				if (_entityData.IsCreated) {
					var oldptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_entityData);
					var newptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(newdata);
					UnsafeUtility.MemCpy(newptr, oldptr, sizeof(EntityData) * _capacity);
					_entityData.Dispose();
				}
				var appendBegin = _capacity;
				_entityData = newdata;
				_capacity = value;
				InitializeAppendArea(appendBegin);
			}
		}
		void InitializeAppendArea(int appendBegin) {
			var ptr = (EntityData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_entityData);
			ptr += appendBegin;
			UnsafeUtility.MemClear(ptr, sizeof(EntityData) * (_capacity - appendBegin));
		}
		internal EntityData* Get(int idx) {
			var ptr = (EntityData*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_entityData);
			return ptr + idx;
		}
		internal void Clear(int idx) {
			var data = Get(idx);
			data->ArcheType = null;
			data->Chunk = null;
			data->IndexInChunk = -1;
		}
	}
	public unsafe struct ArcheType {
		public int Count;
		internal struct Pair {
			internal int Hash;
			internal int Size;
		}
		internal Pair* GetPair(int idx) {
			fixed (ArcheType* p = &this) {
				var pairArray = (Pair*)((byte*)p + sizeof(ArcheType));
				return &pairArray[idx];
			}
		}
		public int GetHash(int idx) {
			return GetPair(idx)->Hash;
		}
		public int GetSize(int idx) {
			return GetPair(idx)->Size;
		}
		public int GetIndex(int hash) {
			for (int i = 0; i < Count; ++i) {
				var pair = GetPair(i);
				if (pair->Hash == hash) return i;
			}
			return -1;
		}
		public int TotalSize {
			get {
				int r = sizeof(int);
				for (int i = 0; i < Count; ++i) {
					r += GetSize(i);
				}
				return r;
			}
		}
		internal bool Includes(ArcheType* other) {
			if (other->Count > Count) return false;
			for (int i = 0; i < other->Count; ++i) {
				var hashOther = other->GetHash(i);
				bool found = false;
				for (int j = 0; j < Count; ++j) {
					var hash = GetHash(j);
					if (hash == hashOther) {
						found = true;
						break;
					}
				}
				if (!found) return false;
			}
			return true;
		}
		internal bool IsEqual(Pair* pairArray, int count) {
			if (count != Count) return false;
			for (int i = 0; i < Count; ++i) {
				var hashA = GetHash(i);
				bool found = false;
				for (int j = 0; j < Count; ++j) {
					var hashB = pairArray[i].Hash;
					if (hashA == hashB) {
						found = true;
						break;
					}
				}
				if (!found) return false;
			}
			return true;
		}
		[Conditional("DEVELOPMENT_BUILD")]
		[Conditional("UNITY_EDITOR")]
		public void Dump() {
			var sb = new System.Text.StringBuilder();
			sb.AppendFormat("ArcheType: Count={0}\n", Count);
			for (int i = 0; i < Count; ++i) {
				var p = GetPair(i);
				sb.AppendFormat("  Hash:{0:X8} Size:{1}\n", p->Hash, p->Size);
			}
			Debug.Log(sb.ToString());
		}
	}
	public unsafe class ArcheTypeManager : IDisposable {
		ChunkAllocator _buffer;
		List<IntPtr> _archeTypes = new List<IntPtr>();

		internal ArcheTypeManager() {
			_buffer = new ChunkAllocator();
		}
		public void Dispose() {
			_buffer.Dispose();
		}
		public ArcheType* GetOrCreateArcheType(params Type[] types) {
			var archeType = FindArcheType(types);
			return (archeType == null)? CreateArcheType(types) : archeType;
		}
		internal ArcheType* CreateArcheType(params Type[] types) {
			return CreateArcheType(null, types);
		}
		internal ArcheType* CreateArcheType(ArcheType* baseArcheType, params Type[] types) {
			int newlen = (baseArcheType != null)? baseArcheType->Count + types.Length : types.Length;
			int newsize = sizeof(ArcheType) + (sizeof(ArcheType.Pair) * newlen);
			ArcheType.Pair* candidateArray = stackalloc ArcheType.Pair[newlen];
			if (baseArcheType != null) {
				for (int i = 0; i < baseArcheType->Count; ++i) {
					var p = &candidateArray[i];
					p->Hash = baseArcheType->GetHash(i);
					p->Size = baseArcheType->GetSize(i);
				}
			}
			int ofs = (baseArcheType != null)? baseArcheType->Count : 0;
			for (int i = ofs; i < newlen; ++i) {
				var t = types[i - ofs];
				Assert.IsTrue(t.IsValueType);
				Assert.IsNotNull(t.GetInterface("SimpleECS.IComponentData", false));
				var p = &candidateArray[i];
				p->Hash = t.GetHashCode();
				p->Size = Marshal.SizeOf(t);
			}
			var archeType = FindArcheType(candidateArray, newlen);
			if (archeType != null) return archeType;
			//
			var ptr = _buffer.Allocate(newsize, sizeof(int));
			archeType = (ArcheType*)ptr;
			archeType->Count = newlen;
			var pairArray = (ArcheType.Pair*)(ptr + sizeof(ArcheType));
			for (int i = 0; i < newlen; ++i) {
				pairArray[i] = candidateArray[i];
			}
			_archeTypes.Add((IntPtr)archeType);
			return archeType;
		}
		internal ArcheType* FindArcheType(ArcheType.Pair* pairArray, int count) {
			foreach (var archeTypePtr in _archeTypes) {
				var archeType = (ArcheType*)archeTypePtr;
				if (archeType->IsEqual(pairArray, count)) {
					return archeType;
				}
			}
			return null;
		}
		internal ArcheType* FindArcheType(params Type[] types) {
			int len = types.Length;
			ArcheType.Pair* pairArray = stackalloc ArcheType.Pair[len];
			for (int i = 0; i < len; ++i) {
				var t = types[i];
				pairArray[i].Hash = t.GetHashCode();
				pairArray[i].Size = Marshal.SizeOf(t);
			}
			return FindArcheType(pairArray, len);
		}
	}
	//
	internal unsafe class ComponentBlock : IDisposable {
		internal ArcheType* ArcheType;
		internal EntityDataManager _entityDataManager;
		internal ChunkAllocator _data;
		internal int _dataSize;
		internal int _countInChunk;
		internal int _usedCount;
		const int AlignmentSize = sizeof(ulong);

		internal ComponentBlock(ArcheType* ptr, EntityDataManager entityDataManager) {
			Assert.IsTrue(ptr != null);
			Assert.IsNotNull(entityDataManager);
			ArcheType = ptr;
			_entityDataManager = entityDataManager;
			_data = new ChunkAllocator();
			_dataSize = Util.AlignmentPow2(ArcheType->TotalSize, AlignmentSize);
			_countInChunk = _data.CalcAlignedCapacity(AlignmentSize) / _dataSize;
			_usedCount = -1;
		}
		public void Dispose() {
			_data.Dispose();
		}
		internal int Allocate(Chunk** chunk, int entityIndex) {
			if (_usedCount < 0 || _usedCount >= _countInChunk) {// * _data.Count) {
				_data.Allocate(_dataSize * _countInChunk, sizeof(ulong));
				_usedCount = 0;
			}
			int r = _usedCount++;
			*chunk = _data.CurrentChunk;
			SetEntityIndex(*chunk, r, entityIndex);
			return r;
		}
		internal void Free(Chunk* chunk, int indexInChunk, Chunk** newChunk, int* newIndexInChunk) {
			int lastComponentIndex = --_usedCount;
			if (_usedCount == 0) return;
			var lastChunk = _data.GetChunk(lastComponentIndex / _countInChunk);
			var lastIndexInChunk = lastComponentIndex % _countInChunk;

			int lastEntityIndex = GetEntityIndex(lastChunk, lastIndexInChunk);
			var entityData = _entityDataManager.Get(lastEntityIndex);

			CopyInternal(chunk, indexInChunk, entityData->Chunk, entityData->IndexInChunk);
			entityData->Chunk = chunk;
			entityData->IndexInChunk = indexInChunk;
		}
		internal void CopyInternal(Chunk* toChunk, int toIndexInChunk, Chunk* fromChunk, int fromIndexInChunk) {
			SetEntityIndex(toChunk, toIndexInChunk, GetEntityIndex(fromChunk, fromIndexInChunk));
			for (int i = 0; i < ArcheType->Count; ++i) {
				int size = ArcheType->GetSize(i);
				var fromBuf = GetComponentDataArray(fromChunk, i) + size * fromIndexInChunk;
				var toBuf = GetComponentDataArray(toChunk, i) + size * toIndexInChunk;
				UnsafeUtility.MemCpy(toBuf, fromBuf, size);
			}
		}
		internal int* GetEntityIndexArray(Chunk* chunk) {
			return (int*)chunk->AlignedTop(AlignmentSize);
		}
		internal int GetEntityIndex(Chunk* chunk, int indexInChunk) {
			return (GetEntityIndexArray(chunk))[indexInChunk];
		}
		internal void SetEntityIndex(Chunk* chunk, int indexInChunk, int entityIndex) {
			(GetEntityIndexArray(chunk))[indexInChunk] = entityIndex;
		}
		internal byte* GetComponentDataArray(Chunk* chunk, int componentIndex) {
			int offset = sizeof(int) * _countInChunk;
			for (int i = 0; i < componentIndex; ++i) {
				offset += ArcheType->GetSize(i) * _countInChunk;
				offset = Util.AlignmentPow2(offset, AlignmentSize);
			}
			return chunk->AlignedTop(AlignmentSize) + offset;
		}
		internal int GetComponentDataCount(Chunk* chunk, int componentIndex) {
			return _countInChunk;
		}
		internal void Dispatch(ComponentSystem system) {
			system.Block = this;
			int nchunk = (_usedCount / _countInChunk) + 1;
			for (int chunkIndex = 0; chunkIndex < nchunk; ++chunkIndex) {
				var chunk = _data.GetChunk(chunkIndex);
				system.PreChunkIteration(chunk);
				int nentity = Mathf.Min(_usedCount - _countInChunk * chunkIndex, _countInChunk);
				system.OnUpdate(nentity);
			}
			system.Block = null;
		}
	}
	internal unsafe class ComponentBlockManager : IDisposable {
		EntityDataManager _entityDataManager;
		Dictionary<ulong, ComponentBlock> _blocks = new Dictionary<ulong, ComponentBlock>();

		internal ComponentBlockManager(EntityDataManager entityDataManager) {
			Assert.IsNotNull(entityDataManager);
			_entityDataManager = entityDataManager;
		}
		public void Dispose() {
		}
		ComponentBlock FindBlock(ArcheType* archeType) {
			var ptr = (ulong)archeType;
			if (_blocks.ContainsKey(ptr)) return _blocks[ptr];
			return null;
		}
		ComponentBlock CreateBlock(ArcheType* archeType) {
			var ptr = (ulong)archeType;
			Assert.IsFalse(_blocks.ContainsKey(ptr));
			var block = new ComponentBlock(archeType, _entityDataManager);
			_blocks[ptr] = block;
			return block;
		}
		internal ComponentBlock GetOrCreateBlock(ArcheType* archeType) {
			var block = FindBlock(archeType);
			if (block != null) return block;
			return CreateBlock(archeType);
		}
		internal int Allocate(ArcheType* archeType, Chunk** chunk, int entityIndex) {
			var block = GetOrCreateBlock(archeType);
			return block.Allocate(chunk, entityIndex);
		}
		internal void Free(ArcheType* archeType, Chunk* chunk, int indexInChunk) {
			var block = FindBlock(archeType);
			Assert.IsNotNull(block);
			Chunk* newChunk;
			int newIndexInChunk;
			block.Free(chunk, indexInChunk, &newChunk, &newIndexInChunk);
		}
		internal void Copy(ArcheType* toArcheType, Chunk* toChunk, int toIndexInChunk, ArcheType* fromArcheType, Chunk* fromChunk, int fromIndexInChunk) {
			var fromBlock = FindBlock(fromArcheType);
			Assert.IsNotNull(fromBlock);
			var toBlock = GetOrCreateBlock(toArcheType);

			(toBlock.GetEntityIndexArray(toChunk))[toIndexInChunk] = (fromBlock.GetEntityIndexArray(fromChunk))[fromIndexInChunk];
			for (int i = 0; i < toArcheType->Count; ++i) {
				int toHash = toArcheType->GetHash(i);
				for (int j = 0; j < fromArcheType->Count; ++j) {
					int fromHash = fromArcheType->GetHash(j);
					if (toHash != fromHash) continue;
					int toSize = toArcheType->GetSize(i);
					int fromSize = fromArcheType->GetSize(j);
					Assert.AreEqual(toSize, fromSize);
					var toBuf = toBlock.GetComponentDataArray(toChunk, i) + toSize * toIndexInChunk;
					var fromBuf = fromBlock.GetComponentDataArray(fromChunk, j) + fromSize * fromIndexInChunk;
					UnsafeUtility.MemCpy(toBuf, fromBuf, toSize);
					break;
				}
			}
		}
		internal void Dispatch(ComponentSystem system) {
			foreach (var key in _blocks.Keys) {
				var block = _blocks[key];
				if (!block.ArcheType->Includes(system.ArcheType)) continue;
				block.Dispatch(system);
			}
		}
	}
	public unsafe struct Chunk {
		internal Chunk* Next;
		internal byte* Top {
			get {
				fixed (Chunk* p = &this) {
					return (byte*)p + sizeof(Chunk);
				}
			}
		}
		internal byte* AlignedTop(int alignment) {
			fixed (Chunk* p = &this) {
				return (byte*)p + Util.AlignmentPow2(sizeof(Chunk), alignment);
			}
		}
	}
	public unsafe struct ChunkAllocator : IDisposable {
		Chunk* _firstChunk;
		Chunk* _lastChunk;
		int _lastChunkUsedSize;
		int _allocCount;
		const int ChunkSize = 64 * 1024;
		const int ChunkAlignment = 64;
		//
		public void Dispose() {
			var p = _firstChunk;
			while (p != null) {
				var next = p->Next;
				UnsafeUtility.Free(p, Allocator.Persistent);
				--_allocCount;
				p = next;
			}
			Assert.IsTrue(_allocCount == 0);
		}
		public byte* Allocate(int size, int alignment) {
			Assert.IsTrue(size <= ChunkSize - sizeof(Chunk));
			var top = Util.AlignmentPow2(_lastChunkUsedSize, alignment);
			if (_firstChunk == null || size > ChunkSize - top) {
				++_allocCount;
				var newChunk = (Chunk*)UnsafeUtility.Malloc(ChunkSize, ChunkAlignment, Allocator.Persistent);
				newChunk->Next = null;
				if (_firstChunk == null) {
					_firstChunk = _lastChunk = newChunk;
				} else {
					_lastChunk->Next = newChunk;
					_lastChunk = newChunk;
				}
				_lastChunkUsedSize = sizeof(Chunk);
				top = Util.AlignmentPow2(_lastChunkUsedSize, alignment);
				Assert.IsTrue(size <= ChunkSize - top);
				return Allocate(size, alignment);
			}
			byte* ptr = ((byte*)_lastChunk + top);
			_lastChunkUsedSize = top + size;
			return ptr;
		}
		public int Capacity {
			get { return ChunkSize - sizeof(Chunk); }
		}
		public int CalcAlignedCapacity(int alignment) {
			var top = Util.AlignmentPow2(sizeof(Chunk), alignment);
			return ChunkSize - top;
		}
		internal Chunk* CurrentChunk {
			get { return _lastChunk; }
		}
		internal int Count {
			get { return _allocCount; }
		}
		internal Chunk* GetChunk(int idx) {
			Assert.IsTrue(0 <= idx && idx < _allocCount);
			var result = _firstChunk;
			while (idx-- > 0) {
				result = result->Next;
			}
			return result;
		}
	}



	public static class Util {
		public static int AlignmentPow2(int v, int alignmentSize) {
			Assert.IsTrue(v >= 0);
			Assert.IsTrue(alignmentSize >= 0);
			Assert.IsTrue(IsPow2(alignmentSize));
			return (v + alignmentSize - 1) & ~(alignmentSize - 1);
		}
		// http://asawicki.info/news_1688_operations_on_power_of_two_numbers.html
		public static bool IsPow2(int x) {
			return (x & (x - 1)) == 0;
		}
		public static uint NextPow2(uint v) {
			v--;
			v |= v >> 1;
			v |= v >> 2;
			v |= v >> 4;
			v |= v >> 8;
			v |= v >> 16;
			v++;
			return v;
		}
		public static ulong NextPow2(ulong v) {
			v--;
			v |= v >> 1;
			v |= v >> 2;
			v |= v >> 4;
			v |= v >> 8;
			v |= v >> 16;
			v |= v >> 32;
			v++;
			return v;
		}
		public static uint PrevPow2(uint v) {
			v |= v >> 1;
			v |= v >> 2;
			v |= v >> 4;
			v |= v >> 8;
			v |= v >> 16;
			v = v ^ (v >> 1);
			return v;
		}
		public static ulong PrevPow2(ulong v) {
			v |= v >> 1;
			v |= v >> 2;
			v |= v >> 4;
			v |= v >> 8;
			v |= v >> 16;
			v |= v >> 32;
			v = v ^ (v >> 1);
			return v;
		}
	}
}
