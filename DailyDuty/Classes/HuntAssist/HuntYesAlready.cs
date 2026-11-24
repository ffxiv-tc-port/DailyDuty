using System;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace DailyDuty.Classes.HuntAssist;

internal enum BotherPauseResult {
	/// <summary>確認不了（YesAlready 載入了但 IPC 出錯）。零值刻意給這個。</summary>
	Failed = 0,
	Paused,

	/// <summary>那一項本來就沒開，不會自動按。</summary>
	NotActive,
	NotLoaded,
}

/// <summary>
/// 自動接取要按告示板的 SelectString，YesAlready 也會看這扇窗：按之前請它把 SelectString 暫停一段時間，
/// 而且答案要夠新才按（同幀搶按＝當機）。確認不了就不按。框架執行緒限定。
/// </summary>
internal sealed class HuntYesAlreadyPause {
	public const string SelectStringFeature = "SelectString";
	public const int PauseMilliseconds = 10_000;

	/// <summary>結果超過這麼久就不算數，比暫停時間短，留下到期前的餘裕。</summary>
	public const long FreshMilliseconds = 8_000;

	private readonly ICallGateSubscriber<string, int, bool> pauseBother = Service.PluginInterface.GetIpcSubscriber<string, int, bool>("YesAlready.PauseBother");
	private long renewedAt = -1;

	public BotherPauseResult Last { get; private set; }

	public bool IsExclusive(long now) => Last is not BotherPauseResult.Failed && renewedAt >= 0 && now - renewedAt < FreshMilliseconds;

	public BotherPauseResult Renew(long now) {
		Last = Ask();
		renewedAt = now;
		return Last;
	}

	public void Reset() {
		Last = BotherPauseResult.Failed;
		renewedAt = -1;
	}

	private BotherPauseResult Ask() {
		try {
			return pauseBother.InvokeFunc(SelectStringFeature, PauseMilliseconds) ? BotherPauseResult.Paused : BotherPauseResult.NotActive;
		}
		catch (IpcNotReadyError) {
			return IsYesAlreadyLoaded() is false ? BotherPauseResult.NotLoaded : BotherPauseResult.Failed;
		}
		catch (Exception ex) {
			Service.Log.Information($"[HuntAssist] 請 YesAlready 暫停 SelectString 失敗（{ex.GetType().Name}）：{ex.Message}");
			return BotherPauseResult.Failed;
		}
	}

	/// <returns>null＝問不到。</returns>
	private static bool? IsYesAlreadyLoaded() {
		try {
			return Service.PluginInterface.InstalledPlugins.Any(plugin => plugin.InternalName == "YesAlready" && plugin.IsLoaded);
		}
		catch (Exception) {
			return null;
		}
	}
}
