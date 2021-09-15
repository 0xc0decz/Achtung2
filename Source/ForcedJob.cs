﻿using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AchtungMod
{
	public class ForcedJobs : IExposable
	{
		public List<ForcedJob> jobs = new List<ForcedJob>();

		public ForcedJobs()
		{
			jobs = new List<ForcedJob>();
		}

		public bool Any() => jobs != null && jobs.OfType<ForcedJob>().Count() > 0;

		public void ExposeData()
		{
			jobs ??= new List<ForcedJob>();

			if (Scribe.mode == LoadSaveMode.Saving)
				_ = jobs.RemoveAll(job => job == null || job.IsEmpty());

			Scribe_Collections.Look(ref jobs, "jobs", LookMode.Deep);

			jobs ??= new List<ForcedJob>();

			if (Scribe.mode == LoadSaveMode.PostLoadInit)
				_ = jobs.RemoveAll(job => job == null || job.IsEmpty());
		}
	}

	public class ForcedJob : IExposable
	{
		private HashSet<ForcedTarget> targets = new HashSet<ForcedTarget>();

		public Pawn pawn = null;
		public List<WorkGiverDef> workgiverDefs = new List<WorkGiverDef>();
		public bool isThingJob = false;
		public IntVec3 lastLocation = IntVec3.Invalid;
		public Thing lastThing = null;
		public bool initialized = false;
		public int cellRadius = 0;
		public bool buildSmart = Achtung.Settings.buildingSmartDefault;
		public bool cancelled = false;
		public Coroutine expanderThing, contractorThing;
		public Coroutine expanderCell, contractorCell;
		static readonly Dictionary<BuildableDef, int> TypeScores = new Dictionary<BuildableDef, int>
		{
			{ ThingDefOf.PowerConduit, 1000 },
			{ ThingDefOf.Wall, 900 },
			{ ThingDefOf.TrapSpike, 300 },
			{ ThingDefOf.Sandbags, 200 },
			{ ThingDefOf.Turret_MiniTurret, 150 },
			{ ThingDefOf.Door, 50 },
			{ ThingDefOf.Bed, 10 },
			{ ThingDefOf.Bedroll, 9 },
			{ ThingDefOf.Campfire, 6 },
			{ ThingDefOf.TorchLamp, 6 },
			{ ThingDefOf.Table2x2c, 6 },
			{ ThingDefOf.DiningChair, 6 },
			{ ThingDefOf.Battery, 5 },
			{ ThingDefOf.WoodFiredGenerator, 5 },
			{ ThingDefOf.SolarGenerator, 5 },
			{ ThingDefOf.WindTurbine, 5 },
			{ ThingDefOf.GeothermalGenerator, 5 },
			{ ThingDefOf.WatermillGenerator, 5 },
			{ ThingDefOf.Cooler, 2 },
			{ ThingDefOf.Heater, 2 },
			{ ThingDefOf.FirefoamPopper, 2 },
			{ ThingDefOf.PassiveCooler, 2 },
			{ ThingDefOf.Turret_Mortar, 2 },
			{ ThingDefOf.StandingLamp, 1 },
			{ ThingDefOf.PlantPot, 1 },
			{ ThingDefOf.Grave, 1 },
		};

		public ForcedJob()
		{
			workgiverDefs = new List<WorkGiverDef>();
			targets = new HashSet<ForcedTarget>();
			buildSmart = Achtung.Settings.buildingSmartDefault;
			lastLocation = IntVec3.Invalid;
		}

		public ForcedJob(Pawn pawn, LocalTargetInfo item, List<WorkGiverDef> workgiverDefs)
		{
			this.pawn = pawn;
			this.workgiverDefs = workgiverDefs;
			targets = new HashSet<ForcedTarget>() { new ForcedTarget(item, MaterialScore(item)) };
			buildSmart = Achtung.Settings.buildingSmartDefault;

			lastThing = item.Thing;
			lastLocation = item.Cell;

			isThingJob = item.HasThing;
			CreateCoroutines();
		}

		public void Prepare()
		{
			CreateCoroutines();
		}

		void CreateCoroutines()
		{
			expanderThing ??= Find.CameraDriver.StartCoroutine(ExpandThingTargets());
			contractorThing ??= Find.CameraDriver.StartCoroutine(ContractThingTargets());
			expanderCell ??= Find.CameraDriver.StartCoroutine(ExpandCellTargets());
			contractorCell ??= Find.CameraDriver.StartCoroutine(ContractCellTargets());
		}

		public void Cleanup()
		{
			cancelled = true;

			if (expanderThing != null) Find.CameraDriver.StopCoroutine(expanderThing);
			expanderThing = null;

			if (contractorThing != null) Find.CameraDriver.StopCoroutine(contractorThing);
			contractorThing = null;

			if (expanderCell != null) Find.CameraDriver.StopCoroutine(expanderCell);
			expanderCell = null;

			if (contractorCell != null) Find.CameraDriver.StopCoroutine(contractorCell);
			contractorCell = null;
		}

		public IEnumerable<IntVec3> AllCells(bool onlyValid = false)
		{
			var validTargets = onlyValid ? targets.Where(target => target.IsValidTarget()) : targets;
			if (isThingJob)
				return validTargets
					.SelectMany(target => target.item.ThingDestroyed ? null : target.item.Thing.AllCells())
					.Distinct();
			else
				return validTargets.Select(target => target.item.Cell);
		}

		public IEnumerable<WorkGiver_Scanner> WorkGivers => workgiverDefs
			.Where(wgd => wgd.giverClass != null)
			.Select(wgd => (WorkGiver_Scanner)wgd.Worker);

		public static int MaterialScore(LocalTargetInfo item)
		{
			var scoreThing = 0;
			var scoreBlueprint = 0;
			var scoreFrame = 0;

			var thing = item.Thing;
			if (thing != null)
			{
				if (TypeScores.TryGetValue(thing.def, out var n))
					scoreThing = n;

				if (thing is Blueprint_Build blueprint)
				{
					if (TypeScores.TryGetValue(blueprint.def.entityDefToBuild, out n))
						scoreBlueprint = n;
				}

				if (thing is Frame frame)
				{
					if (TypeScores.TryGetValue(frame.def.entityDefToBuild, out n))
						scoreFrame = n;
				}
			}

			return new[] { scoreThing, scoreBlueprint, scoreFrame }.Max();
		}

		public IEnumerable<Thing> GetUnsortedTargets()
		{
			var mapWidth = pawn.Map.Size.x;
			return targets
				.Where(target => target.IsValidTarget() && Tools.IsFreeTarget(pawn, target))
				.OrderByDescending(target => target.materialScore)
				.Select(target => target.item.Thing);
		}

		public IEnumerable<LocalTargetInfo> GetSortedTargets(HashSet<int> planned)
		{
			const int maxSquaredDistance = 200 * 200;

			var map = pawn.Map;
			var pos = pawn.Position;
			var pathGrid = map.pathing.For(pawn).pathGrid;
			var mapWidth = map.Size.x;
			return targets
				.Where(target =>
				{
					var vec = target.item.Cell;
					var idx = CellIndicesUtility.CellToIndex(vec.x, vec.z, mapWidth);
					return planned.Contains(idx) == false && target.IsValidTarget() && Tools.IsFreeTarget(pawn, target);
				})
				.OrderByDescending(target =>
				{
					var reverseDistance = maxSquaredDistance - pos.DistanceToSquared(target.item.Cell);
					if (reverseDistance < 0) reverseDistance = 0;
					if (buildSmart && isThingJob)
					{
						var willBlock = target.item.WillBlock();
						var neighbourScore = willBlock ? Tools.NeighbourScore(target.item.Cell, pathGrid, map.reservationManager.ReservationsReadOnly, mapWidth, planned) : 100;
						return target.materialScore * 10000 + neighbourScore * 100000000 + reverseDistance;
					}
					return target.materialScore + reverseDistance * 10000;
				})
				.Select(target => target.item);
		}

		public bool IsForbiddenCell(IntVec3 cell)
		{
			return targets.Any(target => target.item.Cell == cell && target.IsBuilding());
		}

		public bool GetNextJob(out Job job)
		{
			job = null;

			var workGiversByPrio = WorkGivers.OrderBy(worker =>
			{
				if (worker is WorkGiver_ConstructDeliverResourcesToBlueprints) return 1;
				if (worker is WorkGiver_ConstructDeliverResourcesToFrames) return 2;
				if (worker is WorkGiver_ConstructDeliverResources) return 3;
				if (worker is WorkGiver_Haul) return 4;
				if (worker is WorkGiver_PlantsCut) return 5;
				if (worker is WorkGiver_ConstructFinishFrames) return 6;
				if (worker is WorkGiver_ConstructAffectFloor) return 7;
				return 999;
			});

			var exist = false;
			foreach (var workgiver in workGiversByPrio)
			{
				foreach (var item in GetSortedTargets(new HashSet<int>()))
				{
					exist = true;

					if (isThingJob)
					{
						job = item.Thing.GetThingJob(pawn, workgiver);
						if (job == null)
							job = item.Cell.GetCellJob(pawn, workgiver);
					}
					else
					{
						job = item.Cell.GetCellJob(pawn, workgiver);
						if (job == null)
							job = item.Thing.GetThingJob(pawn, workgiver);
					}

					if (job != null)
					{
						if (isThingJob)
						{
							lastThing = item.Thing;
							lastLocation = item.Thing.Position;
						}
						else
							lastLocation = item.Cell;
						return true;
					}
				}
			}

			if (exist)
				Find.LetterStack.ReceiveLetter("No forced work", pawn.Name.ToStringShort + " could not find more forced work. The remaining work is most likely reserved or not accessible.", LetterDefOf.NeutralEvent, pawn);

			return false;
		}

		public static bool ContinueJob(Pawn_JobTracker tracker, Job lastJob, Pawn pawn, JobCondition condition)
		{
			_ = tracker;
			_ = lastJob;

			if (pawn == null || pawn.IsColonist == false) return false;
			var forcedWork = ForcedWork.Instance;

			var forcedJob = forcedWork.GetForcedJob(pawn);
			if (forcedJob == null)
				return false;
			if (forcedJob.initialized == false)
			{
				forcedJob.initialized = true;
				return false;
			}

			if (condition == JobCondition.InterruptForced)
			{
				Messages.Message("Forced work of " + pawn.Name.ToStringShort + " was interrupted.", MessageTypeDefOf.RejectInput);
				forcedWork.Remove(pawn);
				return false;
			}

			forcedJob.ExpandTargets();

			while (true)
			{
				if (forcedJob.GetNextJob(out var job))
				{
					job.expiryInterval = 0;
					job.ignoreJoyTimeAssignment = true;
					job.playerForced = true;
					ForcedWork.QueueJob(pawn, job);
					return true;
				}

				forcedWork.RemoveForcedJob(pawn);
				forcedJob = forcedWork.GetForcedJob(pawn);
				if (forcedJob == null)
					break;
				forcedJob.initialized = true;
			}

			forcedWork.Remove(pawn);
			return false;
		}

		public void ChangeCellRadius(int delta)
		{
			cellRadius += delta;
		}

		public void ToggleSmartBuilding()
		{
			buildSmart = !buildSmart;
		}

		public bool HasJob(Thing thing)
		{
			return WorkGivers.Any(workgiver => thing.GetThingJob(pawn, workgiver, true) != null);
		}

		public bool HasJob(ref IntVec3 cell)
		{
			var cell2 = cell;
			return WorkGivers.Any(workgiver => cell2.GetCellJob(pawn, workgiver) != null);
		}

		private IEnumerable<IntVec3> Nearby(ref IntVec3 cell, bool useCenter = false)
		{
			if (cellRadius > 0 && cellRadius <= GenRadial.MaxRadialPatternRadius)
				return GenRadial.RadialCellsAround(cell, cellRadius, useCenter);
			var cell2 = cell;
			if (useCenter) return GenAdj.AdjacentCellsAndInside.Select(vec => cell2 + vec);
			return GenAdj.AdjacentCells.Select(vec => cell2 + vec);
		}

		public void ExpandTargets()
		{
			if (cancelled || lastLocation.IsValid == false) return;
			var thingGrid = pawn.Map.thingGrid;

			if (isThingJob && lastThing != null)
			{
				//if ((lastThing.Spawned == false || HasJob(lastThing) == false) && HasJob(ref lastLocation) == false)
				//	_ = targets.RemoveWhere(target => target.item.Thing == lastThing);

				var cells = lastThing.Spawned ? lastThing.AllCells() : new List<IntVec3> { lastLocation };
				var nearbys = cells
					.SelectMany(cell => Nearby(ref cell, true))
					.Distinct()
					.SelectMany(cell => thingGrid.ThingsAt(cell))
					.Distinct()
					.SelectMany(thing => thing.AllCells())
					.Distinct()
					.SelectMany(cell => Nearby(ref cell, true))
					.Distinct()
					.SelectMany(cell => thingGrid.ThingsAt(cell))
					.Distinct()
					//.SelectMany(thing => thing.AllCells())
					//.Distinct()
					//.SelectMany(cell => Nearby(ref cell, true))
					//.Distinct()
					//.SelectMany(cell => thingGrid.ThingsAt(cell))
					//.Distinct()
					.ToList();
				var visitedNeighbours = new HashSet<Thing>();
				for (var j = 0; j < nearbys.Count; j++)
				{
					var nearbyThing = nearbys[j];
					if (visitedNeighbours.Contains(nearbyThing) == false)
					{
						var ok = HasJob(nearbyThing);
						if (ok)
						{
							var item = new LocalTargetInfo(nearbyThing);
							_ = targets.Add(new ForcedTarget(item, MaterialScore(item)));
						}
						_ = visitedNeighbours.Add(nearbyThing);
					}
				}
			}
			if (isThingJob == false || (lastThing == null && lastLocation.IsValid))
			{
				//if (HasJob(ref lastLocation) == false && thingGrid.ThingsAt(lastLocation).All(thing => HasJob(thing) == false))
				//	_ = targets.RemoveWhere(target => target.item.Cell == lastLocation);

				var nearbys = Nearby(ref lastLocation)
					.SelectMany(cell => Nearby(ref cell, true)).Distinct()
					//.SelectMany(cell => Nearby(ref cell, true)).Distinct()
					.ToList();
				var visitedNeighbours = new HashSet<IntVec3>();
				for (var j = 0; j < nearbys.Count; j++)
				{
					var nearbyCell = nearbys[j];
					if (visitedNeighbours.Contains(nearbyCell) == false)
					{
						var ok = HasJob(ref nearbyCell);
						if (ok)
						{
							var item = new LocalTargetInfo(nearbyCell);
							_ = targets.Add(new ForcedTarget(item, MaterialScore(item)));
						}
						_ = visitedNeighbours.Add(nearbyCell);
					}
				}
			}
		}

		public IEnumerator ExpandThingTargets()
		{
			while (cancelled == false && targets.Count > 0)
			{
				yield return null;
				if (Achtung.Settings.maxForcedItems < AchtungSettings.UnlimitedForcedItems && targets.Count > Achtung.Settings.maxForcedItems)
					continue;

				var visitedNeighbours = new HashSet<Thing>();
				var neighbours = targets.Select(target => target.item.Thing).ToList();
				while (cancelled == false && neighbours.Count > 0 && (Achtung.Settings.maxForcedItems >= AchtungSettings.UnlimitedForcedItems || neighbours.Count < Achtung.Settings.maxForcedItems))
				{
					var newNeighbours = new List<Thing>();
					var thingGrid = pawn.Map.thingGrid;
					for (var i = 0; cancelled == false && i < neighbours.Count; i++)
					{
						var neighbour = neighbours[i];
						var nearbys = neighbour.AllCells()
							.SelectMany(cell => Nearby(ref cell, true))
							.Distinct()
							.SelectMany(cell => thingGrid.ThingsAt(cell))
							.Distinct()
							.ToList();
						for (var j = 0; cancelled == false && j < nearbys.Count; j++)
						{
							var thing = nearbys[j];
							if (visitedNeighbours.Contains(thing) == false)
							{
								if (HasJob(thing))
								{
									var item = new LocalTargetInfo(thing);
									_ = targets.Add(new ForcedTarget(item, MaterialScore(item)));
									newNeighbours.Add(thing);
								}
								_ = visitedNeighbours.Add(thing);
							}
						}
						yield return null;
					}
					neighbours = newNeighbours.ToList();
				}
			}
		}

		public IEnumerator ExpandCellTargets()
		{
			while (cancelled == false && targets.Count > 0)
			{
				yield return null;
				if (Achtung.Settings.maxForcedItems < AchtungSettings.UnlimitedForcedItems && targets.Count > Achtung.Settings.maxForcedItems)
					continue;

				var visitedNeighbours = new HashSet<IntVec3>();
				var neighbours = targets.Select(target => target.item.Cell).ToList();
				while (cancelled == false && neighbours.Count > 0 && (Achtung.Settings.maxForcedItems >= AchtungSettings.UnlimitedForcedItems || neighbours.Count < Achtung.Settings.maxForcedItems))
				{
					var newNeighbours = new List<IntVec3>();
					for (var i = 0; cancelled == false && i < neighbours.Count; i++)
					{
						var neighbour = neighbours[i];
						var nearbys = Nearby(ref neighbour).ToList();
						for (var j = 0; cancelled == false && j < nearbys.Count; j++)
						{
							var cell = nearbys[j];
							if (visitedNeighbours.Contains(cell) == false)
							{
								if (HasJob(ref cell))
								{
									var item = new LocalTargetInfo(cell);
									_ = targets.Add(new ForcedTarget(item, MaterialScore(item)));
									newNeighbours.Add(cell);
								}
								_ = visitedNeighbours.Add(cell);
							}
						}
						yield return null;
					}
					neighbours = newNeighbours.ToList();
				}
			}
		}

		public IEnumerator ContractThingTargets()
		{
			while (cancelled == false && targets.Count > 0)
			{
				var enumerator = targets.Select(target => target.item.Thing).GetEnumerator();
				yield return null;
				var counter = 0;
				while (cancelled == false && enumerator.MoveNext())
				{
					var thing = enumerator.Current;
					if (thing.Spawned == false || HasJob(thing) == false)
						_ = targets.RemoveWhere(target => target.item.Thing == thing);
					if (++counter % 8 == 0)
						yield return null;
					if (cancelled)
						yield break;
				}
			}
		}

		public IEnumerator ContractCellTargets()
		{
			while (cancelled == false && targets.Count > 0)
			{
				var enumerator = targets.Select(target => target.item.Cell).GetEnumerator();
				yield return null;
				var counter = 0;
				while (cancelled == false && enumerator.MoveNext())
				{
					var cell = enumerator.Current;
					if (HasJob(ref cell) == false)
						_ = targets.RemoveWhere(target => target.item.Cell == cell);
					if (++counter % 8 == 0)
						yield return null;
					if (cancelled)
						yield break;
				}
			}
		}

		public bool IsEmpty()
		{
			return targets.Count == 0;
		}

		public void ExposeData()
		{
			if (Scribe.mode == LoadSaveMode.Saving)
				_ = targets.RemoveWhere(target => target == null || target.item == null || target.item.IsValid == false || target.item.ThingDestroyed);

			Scribe_References.Look(ref pawn, "pawn");
			Scribe_Collections.Look(ref workgiverDefs, "workgivers", LookMode.Def);
			Scribe_Collections.Look(ref targets, "targets", LookMode.Deep);
			Scribe_Values.Look(ref isThingJob, "thingJob", false, true);
			Scribe_Values.Look(ref initialized, "inited", false, true);
			Scribe_Values.Look(ref cellRadius, "radius", 0, true);
			Scribe_Values.Look(ref cancelled, "cancelled", false, true);
			Scribe_Values.Look(ref buildSmart, "buildSmart", true, true);
			Scribe_References.Look(ref lastThing, "lastThing");
			Scribe_Values.Look(ref lastLocation, "lastLocation", IntVec3.Invalid, true);

			if (Scribe.mode == LoadSaveMode.PostLoadInit)
				_ = targets.RemoveWhere(target => target == null || target.item == null || target.item.IsValid == false || target.item.ThingDestroyed);
		}
	}

	public class ForcedTarget : IExposable, IEquatable<ForcedTarget>
	{
		public LocalTargetInfo item = LocalTargetInfo.Invalid;
		public int materialScore = 0;

		public ForcedTarget()
		{
			item = LocalTargetInfo.Invalid;
			materialScore = 0;
		}

		public ForcedTarget(LocalTargetInfo item, int materialScore)
		{
			this.item = item;
			this.materialScore = materialScore;
		}

		public void ExposeData()
		{
			Scribe_TargetInfo.Look(ref item, false, "item", LocalTargetInfo.Invalid);
			Scribe_Values.Look(ref materialScore, "materialScore", 0, true);
		}

		public bool Equals(ForcedTarget other)
		{
			return item == other.item;
		}

		public override int GetHashCode()
		{
			return item.GetHashCode();
		}

		public bool IsValidTarget()
		{
			return item.HasThing == false || item.ThingDestroyed == false;
		}

		public bool IsBuilding()
		{
			if (item.HasThing == false) return false;
			var thing = item.Thing;
			if (thing is Frame frame)
				return frame.def.entityDefToBuild == ThingDefOf.Wall;
			if (thing is Blueprint_Build blueprint)
				return blueprint.def.entityDefToBuild == ThingDefOf.Wall;
			return false;
		}

		public override string ToString()
		{
			if (item.HasThing)
				return $"{item.Thing.def.defName}@{item.Cell.x}x{item.Cell.z}({materialScore})";
			return $"{item.Cell.x}x{item.Cell.z}({materialScore})";
		}
	}

	public static class ForcedExtensions
	{
		public static bool Ignorable(this WorkGiver_Scanner workgiver)
		{
			return (false
				|| (workgiver as WorkGiver_Haul) != null
				|| (workgiver as WorkGiver_Repair) != null
				|| (workgiver as WorkGiver_ConstructAffectFloor) != null
				|| (workgiver as WorkGiver_ConstructDeliverResources) != null
				|| (workgiver as WorkGiver_ConstructFinishFrames) != null
				|| (workgiver as WorkGiver_Flick) != null
				|| (workgiver as WorkGiver_Miner) != null
				|| (workgiver as WorkGiver_Refuel) != null
				|| (workgiver as WorkGiver_RemoveRoof) != null
				|| (workgiver as WorkGiver_Strip) != null
				|| (workgiver as WorkGiver_TakeToBed) != null
				|| (workgiver as WorkGiver_RemoveBuilding) != null
			);
		}

		// fix for forbidden state in cached handlers
		//
		public static bool ShouldBeHaulable(Thing t)
		{
			// vanilla code but added 'Achtung.Settings.ignoreForbidden == false &&'
			if (Achtung.Settings.ignoreForbidden == false && t.IsForbidden(Faction.OfPlayer))
				return false;

			if (!t.def.alwaysHaulable)
			{
				if (!t.def.EverHaulable)
					return false;
				// vanilla code but added 'Achtung.Settings.ignoreForbidden == false &&'
				if (Achtung.Settings.ignoreForbidden == false && t.Map.designationManager.DesignationOn(t, DesignationDefOf.Haul) == null && !t.IsInAnyStorage())
					return false;
			}
			return !t.IsInValidBestStorage();
		}
		//
		public static bool ShouldBeMergeable(Thing t)
		{
			// vanilla code but added 'Achtung.Settings.ignoreForbidden ||'
			return (Achtung.Settings.ignoreForbidden || !t.IsForbidden(Faction.OfPlayer)) && t.GetSlotGroup() != null && t.stackCount != t.def.stackLimit;
		}

		public static Job GetThingJob(this Thing thing, Pawn pawn, WorkGiver_Scanner workgiver, bool ignoreReserve = false)
		{
			if (thing == null || thing.Spawned == false) return null;
			var potentialWork = workgiver.PotentialWorkThingRequest.Accepts(thing) || workgiver.PotentialWorkThingsGlobal(pawn) != null && workgiver.PotentialWorkThingsGlobal(pawn).Contains(thing);
			if ((workgiver as WorkGiver_Haul) != null && potentialWork == false)
				potentialWork = ShouldBeHaulable(thing);
			else if ((workgiver as WorkGiver_Merge) != null && potentialWork == false)
				potentialWork = ShouldBeMergeable(thing);

			if (potentialWork)
				if (workgiver.MissingRequiredCapacity(pawn) == null)
					if (workgiver.HasJobOnThing(pawn, thing, true))
					{
						var job = workgiver.JobOnThing(pawn, thing, true);
						if (job != null)
						{
							var ignorable = workgiver.Ignorable();
							if (Achtung.Settings.ignoreForbidden && ignorable || thing.IsForbidden(pawn) == false)
								if (Achtung.Settings.ignoreRestrictions && ignorable || thing.Position.InAllowedArea(pawn))
								{
									if (
										(ignoreReserve == false && pawn.CanReserveAndReach(thing, workgiver.PathEndMode, Danger.Deadly))
										||
										(ignoreReserve && pawn.CanReach(thing, workgiver.PathEndMode, Danger.Deadly))
									) return job;
								}
						}
					}
			return null;
		}

		public static Job GetCellJob(this IntVec3 cell, Pawn pawn, WorkGiver_Scanner workgiver, bool ignoreReserve = false)
		{
			if (cell.IsValid == false) return null;
			if (workgiver.PotentialWorkCellsGlobal(pawn).Contains(cell))
				if (workgiver.MissingRequiredCapacity(pawn) == null)
					if (workgiver.HasJobOnCell(pawn, cell, ignoreReserve))
					{
						var job = workgiver.JobOnCell(pawn, cell);
						if (pawn.CanReach(cell, workgiver.PathEndMode, Danger.Deadly))
							return job;
					}
			return null;
		}
	}
}
