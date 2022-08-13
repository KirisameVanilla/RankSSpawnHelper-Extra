﻿using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.IoC;
using Dalamud.Plugin;
using RankSSpawnHelper.Features;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Local
namespace RankSSpawnHelper;

internal class Service
{
    internal static Commands Commands { get; set; } = null!;
    internal static Configuration Configuration { get; set; } = null!;
    internal static ConfigWindow ConfigWindow { get; set; } = null!;
    internal static AutoDiscardItem AutoDiscardItem { get; set; } = null!;
    internal static LeaveDuty LeaveDuty { get; set; } = null!;
    internal static JournalStuff Journal { get; set; } = null!;

    [PluginService] internal static DalamudPluginInterface Interface { get; private set; } = null!;

    [PluginService] internal static ChatGui ChatGui { get; private set; } = null!;

    [PluginService] internal static ClientState ClientState { get; private set; } = null!;

    [PluginService] internal static CommandManager CommandManager { get; private set; } = null!;

    [PluginService] internal static SigScanner SigScanner { get; private set; } = null!;

    [PluginService] internal static DataManager DataManager { get; private set; } = null!;

    [PluginService] internal static GameGui GameGui { get; set; } = null!;

    [PluginService] internal static Condition Condition { get; set; } = null!;

    [PluginService] internal static PartyList PartyList { get; set; } = null!;

    [PluginService] internal static ObjectTable ObjectTable { get; set; } = null!;
}