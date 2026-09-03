using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.System;

namespace DailyDuty.Classes;

/// <summary>
///     樣式存檔的批次守衛。
/// </summary>
/// <remarks>
///     登出或卸載外掛時,介面節點會先被釋放(OverlayController.DetachNodes → 各節點 Dispose),
///     但持有它們的欄位不會一起清空 —— 只有最上層的 TodoListNode / TimerNode 會被設成 null,
///     分類節點(DailyTaskNode 等)與每個模組的 TodoTaskNode 仍指向已釋放的節點。
///     此時再去序列化這些節點,KamiToolKit 的屬性 getter(例如 TextNode.TextColor)
///     會直接解參已經是 null 的 InternalNode,Newtonsoft 便擲出 NullReferenceException。
///     實機 log 每次關閉遊戲固定 23 筆:
///     "Error getting value from 'TextColor' on 'KamiToolKit.Nodes.TextNode'" ×3 +
///     "... on 'DailyDuty.CustomNodes.TodoTaskNode'" ×20,
///     全部落在 NodeBase.Save(String) 裡。
///     所以存檔前先確認節點的原生記憶體還在;已釋放就跳過,批次結束時記一筆 Information。
/// </remarks>
public sealed class StyleSaveBatch {
	private int skippedCount;

	/// <summary>
	///     節點還活著才存檔。路徑是延後求值的,已釋放的節點連路徑都不會去算
	///     (登出後 ContentId 是 0,算路徑本身就沒有意義)。
	/// </summary>
	public void Save(NodeBase? node, Func<string> pathProvider) {
		if (node is null) return;

		if (!IsAlive(node)) {
			skippedCount++;
			return;
		}

		node.Save(pathProvider());
	}

	/// <summary>
	///     真的跳過了才留紀錄。使用者跑 LogLevel 1,所以用 Information。
	/// </summary>
	public void LogSkipped(string context) {
		if (skippedCount is 0) return;

		Service.Log.Information($"[{context}] 已跳過 {skippedCount} 個已釋放的介面節點,這次沒有寫入它們的樣式檔");
		skippedCount = 0;
	}

	/// <summary>
	///     與 KamiToolKit 自己在 NodeBase.Dispose 用的存活判斷同一套:
	///     原生節點指標為 null(已釋放),或虛擬函式表為 null(節點已被遊戲銷毀)都算死掉。
	/// </summary>
	private static unsafe bool IsAlive(NodeBase node) {
		var resNode = (AtkResNode*)node;
		if (resNode is null) return false;
		if (resNode->VirtualTable is null) return false;

		return true;
	}
}
