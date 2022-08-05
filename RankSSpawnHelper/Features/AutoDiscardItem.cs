﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClickLib.Clicks;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.Attributes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.GeneratedSheets;
using Newtonsoft.Json.Linq;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace RankSSpawnHelper.Features;

public class ItemInfo_t
{
    public uint id;
    public string name;

    public ItemInfo_t(uint id, string name)
    {
        this.id = id;
        this.name = name;
    }
}

public class AutoDiscardItem : IDisposable
{
    // Credit goes to SimpleTweaks https://github.com/Caraxi/SimpleTweaksPlugin/blob/0ddc29b5ceb4942a0234de7f3e0f34eb981f8e3b/Tweaks/QuickSellItems.cs

    private static Hook<AddonSetupDelegate> _addonSetupHook;
    private static Hook<OpenInventoryContext> _openInventoryContextHook;
    private readonly CancellationTokenSource _eventLoopTokenSource = new();
    private readonly DalamudPluginInterface _plugin;
    private bool _pmbContextMenuExist;

    public List<ItemInfo_t> ItemInfos = new();

    public AutoDiscardItem(DalamudPluginInterface plugin)
    {
        _plugin = plugin;
        Task.Factory.StartNew(CheckMarketBoardPlugin, TaskCreationOptions.LongRunning);

        unsafe
        {
            _addonSetupHook = new Hook<AddonSetupDelegate>(Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 8B 83 ?? ?? ?? ?? C1 E8 14"), hk_AddonSetup);
            _addonSetupHook.Enable();

            _openInventoryContextHook = new Hook<OpenInventoryContext>(Service.SigScanner.ScanText("83 B9 ?? ?? ?? ?? ?? 7E 11"), hk_OpenInventoryContext);
            _openInventoryContextHook.Enable();
        }

        Task.Run(async () =>
        {
            while (!Service.DataManager.IsDataReady)
                await Task.Delay(100);

            try
            {
                var info = new List<ItemInfo_t>();

                info.AddRange(Service.DataManager.GetExcelSheet<Item>().Where(
                    i => !string.IsNullOrEmpty(i.Name) &&
                         (
                             (i.FilterGroup == 4 && i.LevelItem.Value.RowId == 1 && i.IsDyeable) || // 普通装备且装等为1的物品 比如草布马裤，超级米饭的斗笠
                             (i.FilterGroup == 12 && i.RowId != 36256 && i.RowId != 27850) || // 材料比如矮人棉，庵摩罗果等 但因为秧鸡胸脯肉和厄尔庇斯鸟蛋是特定地图扔的，所以不会加进列表里
                             i.FilterGroup == 17 // 鱼饵，比如沙蚕
                         )
                ).Select(i => new ItemInfo_t(i.RowId, i.Name)));

                PluginLog.Debug($"Finished loading {info.Count} items");
                ItemInfos = info;
            }
            catch (Exception e)
            {
                PluginLog.Error($"An error occurred when loading items. Reason:{e}");
            }
        });
    }

    public void Dispose()
    {
        _addonSetupHook.Dispose();
        _openInventoryContextHook.Dispose();

        _eventLoopTokenSource.Cancel();
        _eventLoopTokenSource.Dispose();
    }

    private async void CheckMarketBoardPlugin()
    {
        var token = _eventLoopTokenSource.Token;

        var hasEverPrinted = false;

        while (!token.IsCancellationRequested)
            try
            {
                await Task.Delay(100, token);

                if (!_plugin.PluginInternalNames.Contains("MarketBoardPlugin"))
                {
                    _pmbContextMenuExist = false;
                    continue;
                }

                var marketBoardConfigPath = Path.Combine(_plugin.ConfigDirectory.Parent.FullName, "MarketBoardPlugin.json");

                if (!File.Exists(marketBoardConfigPath))
                {
                    if (!hasEverPrinted && Service.ClientState.LocalPlayer)
                    {
                        hasEverPrinted = true;
                        Service.ChatGui.PrintError("[自动扔物品] 找到了MarketBoard插件但找不到对应的配置文件，该功能可能无法正常使用");
                    }

                    continue;
                    // throw new FileNotFoundException("MarketBoardPlugin loaded but cannot find the config file.")
                }

                var fileContent = await File.ReadAllTextAsync(marketBoardConfigPath, token);
                var json = JObject.Parse(fileContent);

                try
                {
                    _pmbContextMenuExist = json["ContextMenuIntegration"].Value<bool>();
                }
                catch (Exception)
                {
                    _pmbContextMenuExist = false;
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
    }

    private static void ProcessYesNo()
    {
        Task.Run(async () =>
        {
            await Task.Delay(Service.Configuration._clickDelay);

            var yesNoAddon = Service.GameGui.GetAddonByName("SelectYesno", 1);
            if (yesNoAddon == IntPtr.Zero) return;

            ClickSelectYesNo.Using(yesNoAddon).Yes();
        });
    }

    private static void ProcessConfirm(IntPtr addon)
    {
        Task.Run(async () =>
        {
            await Task.Delay(Service.Configuration._clickDelay);

            ClickInputNumeric.Using(addon).ClickConfirm();
        });
    }

    private static unsafe void* hk_AddonSetup(AtkUnitBase* addon)
    {
        var original = _addonSetupHook.Original(addon);

        if (!Service.Configuration._autoDiscardItem)
            return original;

        // Elips 雷克兰德 湖区
        if (Service.ClientState.TerritoryType != 961 && Service.ClientState.TerritoryType != 813 && Service.ClientState.TerritoryType != 621)
            return original;

        var name = Marshal.PtrToStringUTF8((IntPtr)addon->Name);

        switch (name)
        {
            case "InputNumeric" when addon->ContextMenuParentID == 0:
                PluginLog.Error("ContextMenuParentID is 0");
                return original;
            case "InputNumeric":
            {
                // 检查附属的是哪个addon
                var parentAddon = AtkStage.GetSingleton()->RaptureAtkUnitManager->GetAddonById(addon->ContextMenuParentID);
                var parentAddonName = Marshal.PtrToStringUTF8(new IntPtr(parentAddon->Name));

                // 傻逼SE我操你妈
                if (parentAddonName != "Inventory" && parentAddonName != "InventoryExpansion" && parentAddonName != "InventoryLarge")
                    return original;

                try
                {
                    var inventoryContext = AgentInventoryContext.Instance();

                    if (!IsAllowedToDiscard(inventoryContext->InventoryItemId))
                        return original;

                    ClickInputNumeric.Using(new IntPtr(addon)).SetValue(inventoryContext->InventoryItemId == 36256 ? 5 : 1, inventoryContext->InventoryItemId == 36256);

                    ProcessConfirm(new IntPtr(addon));

                    return original;
                }
                catch (Exception e)
                {
                    PluginLog.Error(e.ToString());
                    return original;
                }
            }
        }

        return original;
    }

    private static bool IsAllowedToDiscard(uint id)
    {
        var item = Service.DataManager.Excel.GetSheet<Item>()?.GetRow(id);
        if (item == null)
            return false;

        /*
        PluginLog.Debug(
            $"\nID:{id}\nName:{item.Name}\nRarity:{item.Rarity}\nUnique:{item.IsUnique}\nCollectable:{item.IsCollectable}\nUntradable:{item.IsUntradable}\nDyeable:{item.IsDyeable}\nCrestWorthy:{item.IsCrestWorthy}\nFilterGroup:{item.FilterGroup}\nLI:{item.LevelItem.Value.RowId}");
        */

        return Service.Configuration._itemsToDiscard.Contains(id) || (Service.ClientState.TerritoryType == 961 && id == 36256) || (Service.ClientState.TerritoryType == 813 && id == 27850);
    }

    private unsafe void* hk_OpenInventoryContext(AgentInventoryContext2* agent, InventoryType inventoryType, ushort slot, int a4, ushort a5, byte a6)
    {
        var original = _openInventoryContextHook.Original(agent, inventoryType, slot, a4, a5, a6);

        if (!Service.Configuration._autoDiscardItem)
            return original;

        // Elips 雷克兰德 湖区
        if (Service.ClientState.TerritoryType != 961 && Service.ClientState.TerritoryType != 813 && Service.ClientState.TerritoryType != 621)
            return original;

        if (Service.GameGui.GetAddonByName("InventoryBuddy", 1) != IntPtr.Zero || Service.GameGui.GetAddonByName("InventoryEvent", 1) != IntPtr.Zero ||
            Service.GameGui.GetAddonByName("ArmouryBoard", 1) != IntPtr.Zero)
            return original;

        var inventory = InventoryManager.Instance()->GetInventoryContainer(inventoryType);
        if (inventory == null)
            return original;

        var itemSlot = inventory->GetInventorySlot(slot);
        if (itemSlot == null)
            return original;

        var itemId = itemSlot->ItemID;
        var itemQuantity = itemSlot->Quantity;

        if (!IsAllowedToDiscard(itemId))
            return original;

        var addonId = agent->AgentInterface.GetAddonID();
        if (addonId == 0)
            return original;

        var addon = AtkStage.GetSingleton()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null)
            return original;

        var foundSearchForItem = false;

        for (var i = 0; i < agent->ContextItemCount; i++)
        {
            var contextItemParam = agent->EventParamsSpan[agent->ContexItemStartIndex + i];
            if (contextItemParam.Type != ValueType.String)
                continue;

            var contextItemName = Marshal.PtrToStringUTF8(new IntPtr(contextItemParam.String));
            if (contextItemName == null)
                continue;

            // PluginLog.Debug($"index:{i} | contextItemName: {contextItemName}");

            switch (contextItemName)
            {
                case "查看持有情况":
                    foundSearchForItem = true;
                    continue;
                case "拆分":
                    switch (Service.ClientState.TerritoryType)
                    {
                        case 961 when itemId != 36256:
                        case 813 when itemId != 27850:
                            return original;
                        case 961 when itemQuantity <= 5:
                        case 813 when itemQuantity == 1:
                        case 621 when itemQuantity == 1:
                            continue;
                        default:
                            ClickInventoryItemContext.Using(new IntPtr(addon)).FireCallback(foundSearchForItem && _pmbContextMenuExist ? i + 1 : i);
                            return original;
                    }

                case "舍弃":
                    switch (Service.ClientState.TerritoryType)
                    {
                        // 湖区只能扔一件
                        case 961 when itemId != 36256 && itemQuantity != 5:
                        case 813 when itemId != 27850 && itemQuantity != 1:
                        case 621 when itemQuantity != 1:
                            continue;
                    }

                    ClickInventoryItemContext.Using(new IntPtr(addon)).FireCallback(_pmbContextMenuExist && foundSearchForItem ? i + 1 : i);

                    // no async in unsafe function :skull:
                    ProcessYesNo();
                    return original;
            }
        }

        return original;
    }

    // TODO: 等獭爹他们更新ClientStruct的时候删掉这个。。因为缺了个EventParamsSpan
    [Agent(AgentId.InventoryContext)]
    [StructLayout(LayoutKind.Explicit, Size = 0x678)]
    private unsafe struct AgentInventoryContext2
    {
        public static AgentInventoryContext2* Instance()
        {
            return (AgentInventoryContext2*)Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(
                AgentId.InventoryContext);
        }

        [FieldOffset(0x0)] public AgentInterface AgentInterface;
        [FieldOffset(0x28)] public readonly uint BlockingAddonId;

        [FieldOffset(0x2C)] public readonly int ContexItemStartIndex;

        [FieldOffset(0x30)] public readonly int ContextItemCount;

        //TODO check if this is actually correct
        [FieldOffset(0x38)] public fixed byte EventParams[0x10 * 82];
        [FieldOffset(0x558)] public fixed byte EventIdArray[80];
        [FieldOffset(0x5A8)] public readonly uint ContextItemDisabledMask;
        [FieldOffset(0x5AC)] public readonly uint ContextItemSubmenuMask;

        public Span<AtkValue> EventParamsSpan
        {
            get
            {
                fixed (byte* ptr = EventParams)
                {
                    return new Span<AtkValue>(ptr, 82);
                }
            }
        }

        public Span<byte> EventIdSpan
        {
            get
            {
                fixed (byte* ptr = EventIdArray)
                {
                    return new Span<byte>(ptr, 80);
                }
            }
        }

        [FieldOffset(0x5B0)] public readonly int PositionX;
        [FieldOffset(0x5B4)] public readonly int PositionY;

        [FieldOffset(0x5C8)] public readonly uint OwnerAddonId;

        [FieldOffset(0x5D0)] public readonly InventoryType TargetInventoryId;
        [FieldOffset(0x5D4)] public readonly int TargetInventorySlotId;

        [FieldOffset(0x5DC)] public readonly uint DummyInventoryId;

        [FieldOffset(0x5E8)] public readonly InventoryItem* TargetInventorySlot;
        [FieldOffset(0x5F0)] public readonly InventoryItem TargetDummyItem;
        [FieldOffset(0x5D0)] public readonly InventoryType BlockedInventoryId;
        [FieldOffset(0x5D4)] public readonly int BlockedInventorySlotId;

        [FieldOffset(0x638)] public readonly InventoryItem DiscardDummyItem;

        public bool IsContextItemDisabled(int index)
        {
            return index >= 0 && (ContextItemDisabledMask & (1 << index)) != 0;
        }
    }

    private unsafe delegate void* AddonSetupDelegate(AtkUnitBase* addon);

    private unsafe delegate void* OpenInventoryContext(AgentInventoryContext2* agent, InventoryType inventory, ushort slot, int a4, ushort a5, byte a6);
}