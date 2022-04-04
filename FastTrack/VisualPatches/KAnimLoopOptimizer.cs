﻿/*
 * Copyright 2022 Peter Han
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 * BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System.Collections.Generic;
using UnityEngine;

namespace PeterHan.FastTrack.VisualPatches {
	/// <summary>
	/// Stores a list of all "idle" animations (that have no uv changes on any of their frames)
	/// and uses this to disable frame advance or looping on any of these anims once played.
	/// </summary>
	public sealed class KAnimLoopOptimizer {
		/// <summary>
		/// The singleton instance of this class.
		/// </summary>
		public static KAnimLoopOptimizer Instance { get; private set; }

		/// <summary>
		/// Compares two animation frames.
		/// </summary>
		/// <param name="groupData">The group data to retrieve the frame elements.</param>
		/// <param name="a">The first frame.</param>
		/// <param name="b">The second frame.</param>
		/// <returns>true if they are equal, or false otherwise.</returns>
		private static bool CompareFrames(KBatchGroupData groupData, ref KAnim.Anim.Frame a,
				ref KAnim.Anim.Frame b) {
			// Each element specifies a frame from a particular symbol, position, tint, and
			// flags
			int ne = a.numElements;
			bool equal = ne == b.numElements;
			if (equal) {
				int startA = a.firstElementIdx, startB = b.firstElementIdx;
				// If they point to the same elements, they are automatically equal
				if (startA != startB)
					for (int i = 0; i < ne && equal; i++) {
						var elementA = groupData.GetFrameElement(i + startA);
						var elementB = groupData.GetFrameElement(i + startB);
						Color colorA = elementA.multColour, colorB = elementB.multColour;
						equal = elementA.symbol == elementB.symbol && colorA == colorB &&
							elementA.symbolIdx == elementB.symbolIdx && elementA.flags ==
							elementB.flags && elementA.transform == elementB.transform &&
							elementA.frame == elementB.frame;
					}
			}
			return equal;
		}

		/// <summary>
		/// Creates the instance and indexes the animations.
		/// </summary>
		internal static void CreateInstance() {
			var inst = new KAnimLoopOptimizer();
			inst.IndexAnims();
			Instance = inst;
		}

#if false
		internal static void ReRegister(KBatchedAnimController controller) {
			var batch = controller.batch;
			HashedString batchGroupID;
			if (batch == null)
				// No need for a deregister
				controller.Register();
			else if (!controller.IsActive()) {
				// No need for a reregister
				batch.Deregister(controller);
				controller.batch = null;
			} else if ((batchGroupID = controller.batchGroupID).IsValid && batchGroupID !=
					KAnimBatchManager.NO_BATCH) {
				var inst = KAnimBatchManager.Instance();
				var key = BatchKey.Create(controller);
				var bs = inst.batchSets;
				// Store the last chunk coords
				Vector2I coords = KAnimBatchManager.ControllerToChunkXY(controller);
				if (coords != controller.lastChunkXY) {
					batch.Deregister(controller);
					controller.lastChunkXY = coords;
					if (!bs.TryGetValue(key, out BatchSet batchSet)) {
						// No batch set for this key (key includes the xy)
						batchSet = new BatchSet(inst.GetBatchGroup(new BatchGroupKey(key.groupID)),
							key, coords);
						bs.Add(key, batchSet);
						// If UI, add to UI batch sets, otherwise to regular
						if (key.materialType == KAnimBatchGroup.MaterialType.UI)
							inst.uiBatchSets.Add(new KAnimBatchManager.BatchSetInfo {
								batchSet = batchSet,
								isActive = false,
								spatialIdx = coords
							});
						else
							inst.culledBatchSetInfos.Add(new KAnimBatchManager.BatchSetInfo {
								batchSet = batchSet,
								isActive = false,
								spatialIdx = coords
							});
					}
					batchSet.Add(controller);
				}
				controller.forceRebuild = true;
				batch.SetDirty(controller);
			}
		}
#endif

		/// <summary>
		/// The animations that are nothing but static idle animations.
		/// </summary>
		private readonly HashSet<AnimWrapper> idleAnims;

		private KAnimLoopOptimizer() {
			idleAnims = new HashSet<AnimWrapper>();
		}

		/// <summary>
		/// Indexes a particular anim file to see if it makes any progress.
		/// </summary>
		/// <param name="manager">The current batched animation manager.</param>
		/// <param name="data">The file data to check.</param>
		private void IndexAnim(KAnimBatchManager manager, KAnimFileData data) {
			int n = data.animCount;
			var build = data.build;
			var bgd = manager.GetBatchGroupData(build.batchTag);
			for (int i = 0; i < n; i++) {
				// Anim specifies a number of frames from the batch group's frames
				var anim = data.GetAnim(i);
				int start = anim.firstFrameIdx, nf = anim.numFrames, end = start + nf;
				bool trivial = true;
				if (nf > 1) {
					var firstFrame = bgd.GetFrame(start++);
					trivial = firstFrame.idx >= 0;
					for (int j = start; j < end && trivial; j++) {
						// Frames of the animation are available from the batch group
						var nextFrame = bgd.GetFrame(j);
						trivial = nextFrame.idx >= 0 && CompareFrames(bgd, ref firstFrame,
							ref nextFrame);
					}
				}
				if (trivial)
					idleAnims.Add(new AnimWrapper(anim));
			}
		}

		/// <summary>
		/// Indexes all kanims and finds those that do not actually make any progress.
		/// </summary>
		private void IndexAnims() {
			var manager = KAnimBatchManager.Instance();
			idleAnims.Clear();
			foreach (var file in Assets.Anims) {
				var data = file?.GetData();
				if (data != null && data.build != null)
					IndexAnim(manager, data);
			}
		}

		/// <summary>
		/// Checks to see if an animation is an idle animation.
		/// </summary>
		/// <param name="anim">The anim file that is playing.</param>
		/// <returns>true if that animation is an idle animation, or false otherwise.</returns>
		public bool IsIdleAnim(KAnim.Anim anim) {
			bool idle = true;
			if (anim != null)
				idle = idleAnims.Contains(new AnimWrapper(anim));
			return idle;
		}

		/// <summary>
		/// Efficiently wraps an anim bank for hashing.
		/// </summary>
		private sealed class AnimWrapper {
			/// <summary>
			/// The wrapped anim (which has file and bank name).
			/// </summary>
			internal readonly KAnim.Anim anim;

			internal AnimWrapper(KAnim.Anim anim) {
				this.anim = anim;
			}

			public override bool Equals(object obj) {
				return obj is AnimWrapper other && anim == other.anim;
			}

			public override int GetHashCode() {
				return anim.animFile.name.GetHashCode() ^ anim.name.GetHashCode();
			}

			public override string ToString() {
				return anim.animFile.name + "." + anim.name;
			}
		}
	}
}