using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// Soft-bound IPC subscribers for the hunt assistant.
///
/// Every entry point checks HasFunction/HasAction before invoking and swallows remote
/// exceptions, so a missing (or older) vnavmesh/Lifestream degrades the feature instead of
/// throwing into DailyDuty's framework tick. Never assume a call succeeded - check the
/// returned bool.
/// </summary>
internal static class IpcGuard {
	internal static T Safe<T>(Func<T> func, T fallback, string label) {
		try {
			return func();
		}
		catch (Exception ex) {
			Service.Log.Warning(ex, $"[HuntAssist] IPC call failed: {label}");
			return fallback;
		}
	}

	internal static bool SafeVoid(Action action, string label) {
		try {
			action();
			return true;
		}
		catch (Exception ex) {
			Service.Log.Warning(ex, $"[HuntAssist] IPC call failed: {label}");
			return false;
		}
	}
}

/// <summary>
/// Subscriber for vnavmesh's IPC surface (prefix "vnavmesh."), as registered by its
/// IPCProvider. Only the handful of endpoints the hunt assistant needs are bound.
/// </summary>
public sealed class NavmeshIpc {
	private const string Prefix = "vnavmesh";

	private readonly ICallGateSubscriber<bool> navIsReady;
	private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> queryPointOnFloor;
	private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> queryNearestPoint;
	private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
	private readonly ICallGateSubscriber<bool> pathfindInProgress;
	private readonly ICallGateSubscriber<object> pathStop;
	private readonly ICallGateSubscriber<bool> pathIsRunning;

	public NavmeshIpc() {
		navIsReady = Service.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.Nav.IsReady");
		queryPointOnFloor = Service.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>($"{Prefix}.Query.Mesh.PointOnFloor");
		queryNearestPoint = Service.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>($"{Prefix}.Query.Mesh.NearestPoint");
		pathfindAndMoveTo = Service.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>($"{Prefix}.SimpleMove.PathfindAndMoveTo");
		pathfindInProgress = Service.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.SimpleMove.PathfindInProgress");
		pathStop = Service.PluginInterface.GetIpcSubscriber<object>($"{Prefix}.Path.Stop");
		pathIsRunning = Service.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.Path.IsRunning");
	}

	/// <summary>True when vnavmesh is loaded and has registered its IPC.</summary>
	public bool IsInstalled => IpcGuard.Safe(() => navIsReady.HasFunction, false, "Nav.IsReady(probe)");

	/// <summary>True when a navmesh for the current zone has finished building.</summary>
	public bool IsReady => IsInstalled && IpcGuard.Safe(() => navIsReady.InvokeFunc(), false, "Nav.IsReady");

	/// <summary>Snaps an approximate position onto the navmesh floor. Null when off-mesh.</summary>
	public Vector3? PointOnFloor(Vector3 position, float halfExtentXZ = 5.0f)
		=> !IsInstalled ? null : IpcGuard.Safe(() => queryPointOnFloor.InvokeFunc(position, false, halfExtentXZ), null, "Query.Mesh.PointOnFloor");

	/// <summary>Finds the closest point that is actually on the navmesh. Null when too far off-mesh.</summary>
	public Vector3? NearestPoint(Vector3 position, float halfExtentXZ = 5.0f, float halfExtentY = 5.0f)
		=> !IsInstalled ? null : IpcGuard.Safe(() => queryNearestPoint.InvokeFunc(position, halfExtentXZ, halfExtentY), null, "Query.Mesh.NearestPoint");

	/// <summary>Queues an asynchronous pathfind-and-walk. Returns false when it could not be started.</summary>
	public bool MoveTo(Vector3 destination)
		=> IsInstalled && IpcGuard.Safe(() => pathfindAndMoveTo.InvokeFunc(destination, false), false, "SimpleMove.PathfindAndMoveTo");

	/// <summary>True while a pathfind request is still being computed (before walking starts).</summary>
	public bool PathfindInProgress
		=> IsInstalled && IpcGuard.Safe(() => pathfindInProgress.InvokeFunc(), false, "SimpleMove.PathfindInProgress");

	/// <summary>True while the character is actively following a path.</summary>
	public bool IsPathRunning
		=> IsInstalled && IpcGuard.Safe(() => pathIsRunning.InvokeFunc(), false, "Path.IsRunning");

	/// <summary>Cancels any in-progress movement. Safe to call when nothing is running.</summary>
	public void Stop() {
		if (!IpcGuard.Safe(() => pathStop.HasAction, false, "Path.Stop(probe)")) return;
		IpcGuard.SafeVoid(() => pathStop.InvokeAction(), "Path.Stop");
	}
}

/// <summary>
/// Subscriber for Lifestream's IPC surface. Lifestream registers via ECommons' EzIPC, which
/// prefixes every tag with the plugin's InternalName ("Lifestream") and uses the method name
/// verbatim.
/// </summary>
public sealed class LifestreamIpc {
	private const string Prefix = "Lifestream";

	private readonly ICallGateSubscriber<bool> isBusy;
	private readonly ICallGateSubscriber<object> abort;
	private readonly ICallGateSubscriber<uint, byte, bool> teleport;
	private readonly ICallGateSubscriber<uint, bool> aethernetTeleportById;
	private readonly ICallGateSubscriber<uint> getActiveAetheryte;

	public LifestreamIpc() {
		isBusy = Service.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.IsBusy");
		abort = Service.PluginInterface.GetIpcSubscriber<object>($"{Prefix}.Abort");
		teleport = Service.PluginInterface.GetIpcSubscriber<uint, byte, bool>($"{Prefix}.Teleport");
		aethernetTeleportById = Service.PluginInterface.GetIpcSubscriber<uint, bool>($"{Prefix}.AethernetTeleportById");
		getActiveAetheryte = Service.PluginInterface.GetIpcSubscriber<uint>($"{Prefix}.GetActiveAetheryte");
	}

	/// <summary>True when Lifestream is loaded and has registered its IPC.</summary>
	public bool IsInstalled => IpcGuard.Safe(() => isBusy.HasFunction, false, "IsBusy(probe)");

	/// <summary>True while Lifestream is running a task of its own.</summary>
	public bool IsBusy => IsInstalled && IpcGuard.Safe(() => isBusy.InvokeFunc(), false, "IsBusy");

	/// <summary>Aborts Lifestream's current task queue and any movement it owns.</summary>
	public void Abort() {
		if (!IpcGuard.Safe(() => abort.HasAction, false, "Abort(probe)")) return;
		IpcGuard.SafeVoid(() => abort.InvokeAction(), "Abort");
	}

	/// <summary>World teleport to an Aetheryte sheet row.</summary>
	public bool Teleport(uint aetheryteId)
		=> IsInstalled && IpcGuard.Safe(() => teleport.InvokeFunc(aetheryteId, 0), false, "Teleport");

	/// <summary>
	/// Aethernet hop to an Aetheryte sheet row. Requires the character to already be standing
	/// in range of an aetheryte or aethernet shard - check <see cref="ActiveAetheryte"/> first.
	/// </summary>
	public bool AethernetTeleport(uint aetheryteId)
		=> IsInstalled && IpcGuard.Safe(() => aethernetTeleportById.InvokeFunc(aetheryteId), false, "AethernetTeleportById");

	/// <summary>The aetheryte/shard the character is currently in range of, or 0.</summary>
	public uint ActiveAetheryte
		=> !IsInstalled ? 0u : IpcGuard.Safe(() => getActiveAetheryte.InvokeFunc(), 0u, "GetActiveAetheryte");
}
