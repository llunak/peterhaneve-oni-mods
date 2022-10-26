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

using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace PeterHan.FastTrack.GamePatches {
	/// <summary>
	/// A component added to SuitMarker instances to update things slower.
	/// </summary>
	[SkipSaveFileSerialization]
	public sealed class SuitMarkerUpdater : KMonoBehaviour, ISim1000ms, ISim200ms {
		/// <summary>
		/// Drops the currently worn suit on the floor and emits a notification that a suit
		/// was dropped due to lack of space.
		/// </summary>
		/// <param name="equipment">The assignables containing the suit to drop.</param>
		private static void DropSuit(Assignables equipment) {
			var assignable = equipment.GetAssignable(Db.Get().AssignableSlots.Suit);
			var notification = new Notification(STRINGS.MISC.NOTIFICATIONS.SUIT_DROPPED.NAME,
				NotificationType.BadMinor, (_, data) => STRINGS. MISC.NOTIFICATIONS.
				SUIT_DROPPED.TOOLTIP);
			assignable.Unassign();
			if (assignable.TryGetComponent(out Notifier notifier))
				notifier.Add(notification);
		}

		/// <summary>
		/// Checks the status of a suit locker to see if the suit can be used.
		/// </summary>
		/// <param name="locker">The suit dock to check.</param>
		/// <param name="fullyCharged">Will contain with the suit if fully charged.</param>
		/// <param name="partiallyCharged">Will contain the suit if partially charged.</param>
		/// <param name="suit">Will contain any suit inside.</param>
		/// <returns>true if the locker is vacant, or false if it is occupied.</returns>
		internal static bool GetSuitStatus(SuitLocker locker, out KPrefabID fullyCharged,
				out KPrefabID partiallyCharged, out KPrefabID suit) {
			var smi = locker.smi;
			bool vacant = false;
			float minCharge = TUNING.EQUIPMENT.SUITS.MINIMUM_USABLE_SUIT_CHARGE;
			suit = locker.GetStoredOutfit();
			// CanDropOffSuit calls GetStoredOutfit again, avoid!
			if (suit == null) {
				if (smi.sm.isConfigured.Get(smi) && !smi.sm.isWaitingForSuit.Get(smi))
					vacant = true;
				fullyCharged = null;
				partiallyCharged = null;
			} else if (suit.TryGetComponent(out SuitTank tank) && tank.PercentFull() >=
					minCharge) {
				// Check for jet suit tank of petroleum
				if (suit.TryGetComponent(out JetSuitTank petroTank)) {
					fullyCharged = tank.IsFull() && petroTank.IsFull() ? suit : null;
					partiallyCharged = petroTank.PercentFull() >= minCharge ? suit : null;
				} else {
					fullyCharged = tank.IsFull() ? suit : null;
					partiallyCharged = suit;
				}
			} else {
				fullyCharged = null;
				partiallyCharged = null;
			}
			return vacant;
		}

		/// <summary>
		/// Processes a Duplicant walking by the checkpoint.
		/// </summary>
		/// <param name="checkpoint">The checkpoint to walk by.</param>
		/// <param name="reactor">The Duplicant that is reacting.</param>
		/// <returns>true if the reaction was processed, or false otherwise.</returns>
		internal static bool React(SuitMarker checkpoint, GameObject reactor) {
			bool react = false;
			if (reactor.TryGetComponent(out MinionIdentity id) && checkpoint.TryGetComponent(
					out SuitMarkerUpdater updater)) {
				var equipment = id.GetEquipment();
				bool hasSuit = equipment.IsSlotOccupied(Db.Get().AssignableSlots.Suit);
				if (reactor.TryGetComponent(out KBatchedAnimController kbac))
					kbac.RemoveAnimOverrides(checkpoint.interactAnim);
				// If not wearing a suit, or the navigator can pass this checkpoint
				bool changed = (!hasSuit || (reactor.TryGetComponent(out Navigator nav) &&
					(nav.flags & checkpoint.PathFlag) > PathFinder.PotentialPath.Flags.
					None)) && updater.TryEquipSuit(equipment, hasSuit);
				// Dump on floor, if they pass by with a suit and taking it off is impossible
				if (!changed && hasSuit)
					DropSuit(equipment);
				react = true;
			}
			return react;
		}

		/// <summary>
		/// The current location of the dock.
		/// </summary>
		private int cell;

		/// <summary>
		/// Whether there was a suit available last frame.
		/// </summary>
		private bool hadAvailableSuit;

		/// <summary>
		/// The cached list of suit docks next to the checkpoint.
		/// </summary>
		private readonly List<SuitLocker> docks;

#pragma warning disable CS0649
#pragma warning disable IDE0044
		// These fields are automatically populated by KMonoBehaviour
		[MyCmpReq]
		private KAnimControllerBase anim;

		[MyCmpReq]
		private SuitMarker suitCheckpoint;
#pragma warning restore IDE0044
#pragma warning restore CS0649

		internal SuitMarkerUpdater() {
			docks = new List<SuitLocker>();
		}

		public override void OnSpawn() {
			base.OnSpawn();
			hadAvailableSuit = false;
			cell = Grid.PosToCell(transform.position);
			if (suitCheckpoint != null)
				suitCheckpoint.GetAttachedLockers(docks);
			UpdateSuitStatus();
		}

		/// <summary>
		/// Updates the status of nearby suits.
		/// </summary>
		public void Sim200ms(float _) {
			UpdateSuitStatus();
		}

		/// <summary>
		/// Only update the nearby lockers every second, as they rarely change.
		/// </summary>
		public void Sim1000ms(float _) {
			if (suitCheckpoint != null) {
				docks.Clear();
				suitCheckpoint.GetAttachedLockers(docks);
			}
		}
		
		/// <summary>
		/// Attempts to equip or uneqip a suit to a passing Duplicant.
		/// </summary>
		/// <param name="equipment">The Duplicant to be equipped or uneqipped.</param>
		/// <param name="hasSuit">Whether they already have a suit.</param>
		/// <returns>true if a suit was added or removed, or false otherwise.</returns>
		private bool TryEquipSuit(Equipment equipment, bool hasSuit) {
			bool changed = false;
			int n = docks.Count;
			for (int i = 0; i < n; i++) {
				var dock = docks[i];
				if (dock != null) {
					if (GetSuitStatus(dock, out var fullyCharged, out _, out _) && hasSuit) {
						dock.UnequipFrom(equipment);
						changed = true;
						break;
					}
					if (!hasSuit && fullyCharged != null) {
						dock.EquipTo(equipment);
						changed = true;
						break;
					}
				}
			}
			if (!hasSuit && !changed) {
				SuitLocker bestAvailable = null;
				float maxScore = 0f;
				for (int i = 0; i < n; i++) {
					var dock = docks[i];
					if (dock != null && dock.GetSuitScore() > maxScore) {
						bestAvailable = dock;
						maxScore = dock.GetSuitScore();
					}
				}
				if (bestAvailable != null) {
					bestAvailable.EquipTo(equipment);
					changed = true;
				}
			}
			if (changed)
				UpdateSuitStatus();
			return changed;
		}

		/// <summary>
		/// Updates the status of the suits in nearby suit docks for pathfinding and
		/// animation purposes.
		/// </summary>
		internal void UpdateSuitStatus() {
			if (suitCheckpoint != null) {
				KPrefabID availableSuit = null;
				int charged = 0, vacancies = 0;
				foreach (var dock in docks)
					if (dock != null) {
						if (GetSuitStatus(dock, out _, out var partiallyCharged,
								out var outfit))
							vacancies++;
						else if (partiallyCharged != null)
							charged++;
						if (availableSuit == null)
							availableSuit = outfit;
					}
				bool hasSuit = availableSuit != null;
				if (hasSuit != hadAvailableSuit) {
					anim.Play(hasSuit ? "off" : "no_suit");
					hadAvailableSuit = hasSuit;
				}
				Grid.UpdateSuitMarker(cell, charged, vacancies, suitCheckpoint.gridFlags,
					suitCheckpoint.PathFlag);
			}
		}
	}

	/// <summary>
	/// Applied to SuitMarker to add an improved updater to each instance.
	/// </summary>
	[HarmonyPatch(typeof(SuitMarker), nameof(SuitMarker.OnSpawn))]
	public static class SuitMarker_OnSpawn_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.MiscOpts;

		/// <summary>
		/// Applied after OnSpawn runs.
		/// </summary>
		internal static void Postfix(SuitMarker __instance) {
			var go = __instance.gameObject;
			if (go != null)
				go.AddOrGet<SuitMarkerUpdater>();
		}
	}

	/// <summary>
	/// Applied to SuitMarker.SuitMarkerReactable to make the Run method more efficient and
	/// use the SuitMarkerUpdater..
	/// </summary>
	[HarmonyPatch(typeof(SuitMarker.SuitMarkerReactable), nameof(SuitMarker.
		SuitMarkerReactable.Run))]
	public static class SuitMarker_SuitMarkerReactable_Run_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.MiscOpts;

		/// <summary>
		/// Applied before Run runs.
		/// </summary>
		internal static bool Prefix(GameObject ___reactor, SuitMarker ___suitMarker) {
			return ___reactor != null && ___suitMarker != null && SuitMarkerUpdater.React(
				___suitMarker, ___reactor);
		}
	}

	/// <summary>
	/// Applied to SuitMarker to turn off the expensive Update method. The SuitMarkerUpdater
	/// component can update the SuitMarker at more appropriate rates.
	/// </summary>
	[HarmonyPatch(typeof(SuitMarker), nameof(SuitMarker.Update))]
	public static class SuitMarker_Update_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.MiscOpts;

		/// <summary>
		/// Applied before Update runs.
		/// </summary>
		internal static bool Prefix() {
			return false;
		}
	}
}
