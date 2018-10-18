using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Assert = UnityEngine.Assertions.Assert;

namespace SimpleECS {
	public class BitVectorTest {
		[Test]
		public void BitVectorTestSimplePasses() {
			// Use the Assert class to test conditions
			using (var bv = new BitVector()) {
				bv.Set(0);
				Assert.IsTrue(bv.Check(0));
			}
		}

		[Test]
		public void CapacityTest() {
			for (int i = 1; i < 1024; ++i) {
				using (var bv = new BitVector(i)) {
					Assert.AreEqual(i, bv.Capacity);
					bv.Set(i-1);
					Assert.IsTrue(bv.Check(i-1));
				}
			}
		}
		[Test]
		public void ExpandCapacityTest() {
			using (var bv = new BitVector(1)) {
				for (int i = 2; i <= 1024; ++i) {
					int prev = bv.Capacity;
					for (int j = 0; j < bv.Capacity; ++j) {
						bv.Set(j);
					}
					bv.Capacity = i;
					for (int j = 0; j < prev; ++j) {
						Assert.IsTrue(bv.Check(j));
					}
					for (int j = prev; j < i; ++j) {
						Assert.IsFalse(bv.Check(j));
					}
				}
			}
		}




















	}
}
