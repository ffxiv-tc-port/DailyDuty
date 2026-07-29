using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using DailyDuty.Localization;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace DailyDuty.Classes;

/// <summary>
///     Shows a hint in the Duty Finder (ContentsFinder) window listing collectables
///     (mounts, minions, orchestrion rolls, Triple Triad cards, etc.) that drop from the
///     currently selected duty but have not yet been obtained by this character.
/// </summary>
public unsafe class CollectableController : IDisposable {
    private const string EmbeddedResourceName = "DailyDuty.Resources.DutyCollectables.json";

    // Entry shape: [itemId, type, ...relatedItemIds]
    // type 4 (timeworn orchestrion roll) is unlocked by checking the resulting orchestrion
    // roll item(s) in relatedItemIds instead of the itemId itself. All other types check
    // the itemId directly.
    private readonly record struct CollectableEntry(uint ItemId, byte Type, uint[] RelatedItemIds);

    private readonly Dictionary<uint, List<CollectableEntry>>? dutyCollectables;

    private TextNode? infoTextNode;

    private bool hasLastSelection;
    private ContentsId.ContentsType lastContentType;
    private uint lastContentId;

    public CollectableController() {
        dutyCollectables = LoadEmbeddedData();

        System.ContentsFinderController.OnAttach += AttachNodes;
        System.ContentsFinderController.OnDetach += DetachNodes;
        System.ContentsFinderController.OnUpdate += OnContentsFinderUpdate;
    }

    public void Dispose() {
        System.ContentsFinderController.OnAttach -= AttachNodes;
        System.ContentsFinderController.OnDetach -= DetachNodes;
        System.ContentsFinderController.OnUpdate -= OnContentsFinderUpdate;

        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
    }

    private static Dictionary<uint, List<CollectableEntry>>? LoadEmbeddedData() {
        try {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);

            if (stream is null) {
                Service.Log.Warning($"[CollectableController] Embedded resource '{EmbeddedResourceName}' was not found, duty collectable hints will be disabled.");
                return null;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var raw = JsonConvert.DeserializeObject<Dictionary<string, List<List<uint>>>>(json);
            if (raw is null) {
                Service.Log.Warning("[CollectableController] Embedded duty collectable data was empty, hints will be disabled.");
                return null;
            }

            var result = new Dictionary<uint, List<CollectableEntry>>();

            foreach (var (key, entries) in raw) {
                if (!uint.TryParse(key, out var cfcId)) continue;

                var parsedEntries = new List<CollectableEntry>();

                foreach (var entry in entries) {
                    if (entry.Count < 2) continue;

                    var itemId = entry[0];
                    var type = (byte) entry[1];
                    var relatedItemIds = entry.Count > 2 ? entry.Skip(2).ToArray() : [];

                    parsedEntries.Add(new CollectableEntry(itemId, type, relatedItemIds));
                }

                result[cfcId] = parsedEntries;
            }

            return result;
        }
        catch (Exception e) {
            Service.Log.Warning(e, "[CollectableController] Failed to load embedded duty collectable data, hints will be disabled.");
            return null;
        }
    }

    private void AttachNodes(AddonContentsFinder* addon) {
        if (dutyCollectables is null) return;

        // Placed in the free space at the bottom of the window, beside the "Open DailyDuty"
        // button that DutyRoulette already attaches there (Position(50, 622) Size(130, 28)) -
        // this strip is empty space below the native duty list/detail panel and does not
        // overlap any native ContentsFinder elements.
        infoTextNode = new TextNode {
            NodeId = 1001,
            Position = new Vector2(190.0f, 610.0f),
            Size = new Vector2(600.0f, 56.0f),
            TextFlags = TextFlags.MultiLine | TextFlags.WordWrap | TextFlags.AutoAdjustNodeSize,
            AlignmentType = AlignmentType.TopLeft,
            FontSize = 11,
            Tooltip = Strings.CollectableHintTooltip,
            EnableEventFlags = true,
            IsVisible = false,
        };
        System.NativeController.AttachNode(infoTextNode, addon->RootNode);

        hasLastSelection = false;
    }

    private void DetachNodes(AddonContentsFinder* addon) {
        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
    }

    private void OnContentsFinderUpdate(AddonContentsFinder* addon) {
        if (infoTextNode is null || dutyCollectables is null) return;

        if (!System.CollectableConfig.Enabled) {
            infoTextNode.IsVisible = false;
            return;
        }

        var agent = AgentContentsFinder.Instance();
        if (agent is null) return;

        var selectedDuty = agent->SelectedDuty;

        // Only recompute when the highlighted/described duty actually changes.
        if (hasLastSelection && selectedDuty.ContentType == lastContentType && selectedDuty.Id == lastContentId) return;

        hasLastSelection = true;
        lastContentType = selectedDuty.ContentType;
        lastContentId = selectedDuty.Id;

        if (selectedDuty.ContentType != ContentsId.ContentsType.Regular || !dutyCollectables.TryGetValue(selectedDuty.Id, out var entries)) {
            infoTextNode.IsVisible = false;
            return;
        }

        RefreshDisplay(entries);
    }

    private void RefreshDisplay(List<CollectableEntry> entries) {
        if (infoTextNode is null) return;

        var itemSheet = Service.DataManager.GetExcelSheet<Item>();
        var missingByType = new SortedDictionary<byte, List<string>>();

        foreach (var entry in entries) {
            if (!ShouldShowType(entry.Type)) continue;
            if (IsAcquired(entry)) continue;

            var itemRow = itemSheet.GetRowOrDefault(entry.ItemId);
            if (itemRow is null) continue;

            var name = itemRow.Value.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            if (!missingByType.TryGetValue(entry.Type, out var names)) {
                names = [];
                missingByType[entry.Type] = names;
            }

            if (!names.Contains(name)) {
                names.Add(name);
            }
        }

        if (missingByType.Count == 0) {
            infoTextNode.IsVisible = false;
            return;
        }

        var builder = new StringBuilder();
        builder.Append(Strings.CollectableHintHeader);

        foreach (var (type, names) in missingByType) {
            builder.Append('\n');
            builder.Append(GetTypeName(type));
            builder.Append(':');
            builder.Append(string.Join('、', names));
        }

        infoTextNode.Text = builder.ToString();
        infoTextNode.IsVisible = true;
    }

    private static bool IsAcquired(CollectableEntry entry) {
        if (entry.Type == 4) {
            return entry.RelatedItemIds.Length != 0 && entry.RelatedItemIds.All(IsItemUnlocked);
        }

        return IsItemUnlocked(entry.ItemId);
    }

    private static bool IsItemUnlocked(uint itemId) {
        var itemRow = ExdModule.GetItemRowById(itemId);
        if (itemRow is null) return true; // No data to show anything is missing, don't claim it's missing.

        return UIState.Instance()->IsItemActionUnlocked(itemRow) == 1;
    }

    private static bool ShouldShowType(byte type) => type switch {
        1 => System.CollectableConfig.ShowMounts,
        2 => System.CollectableConfig.ShowMinions,
        3 => System.CollectableConfig.ShowOrchestrionRolls,
        4 => System.CollectableConfig.ShowTimewornOrchestrionRolls,
        5 => System.CollectableConfig.ShowTripleTriadCards,
        6 => System.CollectableConfig.ShowChocoboBarding,
        7 => System.CollectableConfig.ShowOther,
        _ => false,
    };

    private static string GetTypeName(byte type) => type switch {
        1 => Strings.CollectableTypeMount,
        2 => Strings.CollectableTypeMinion,
        3 => Strings.CollectableTypeOrchestrionRoll,
        4 => Strings.CollectableTypeTimewornOrchestrionRoll,
        5 => Strings.CollectableTypeTripleTriadCard,
        6 => Strings.CollectableTypeChocoboBarding,
        7 => Strings.CollectableTypeOther,
        _ => string.Empty,
    };
}
