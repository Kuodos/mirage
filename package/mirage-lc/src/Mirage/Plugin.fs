namespace Mirage

open BepInEx
open Dissonance
open System
open System.IO
open System.Reflection
open System.Threading
open UnityEngine
open IcedTasks
open Newtonsoft.Json
open Mirage.PluginInfo
open Mirage.Compatibility
open Mirage.Core.Task.Fork
open Mirage.Domain.Netcode
open Mirage.Domain.Setting
open Mirage.Domain.Config
open Mirage.Domain.Directory
open Mirage.Domain.Audio.Recording
open Mirage.Domain.Logger
open Mirage.Hook.AudioSpatializer
open Mirage.Hook.Prefab
open Mirage.Hook.Config
open Mirage.Hook.Microphone
open Mirage.Hook.Dissonance
open Mirage.Hook.MaskedPlayerEnemy
open Mirage.Hook.PlayerControllerB

[<BepInPlugin(pluginId, pluginName, pluginVersion)>]
[<BepInDependency(LethalSettings.GeneratedPluginInfo.Identifier, BepInDependency.DependencyFlags.SoftDependency)>]
[<BepInDependency(LobbyCompatibility.PluginInfo.PLUGIN_GUID, BepInDependency.DependencyFlags.SoftDependency)>]
[<BepInDependency(LethalConfig.PluginInfo.Guid, BepInDependency.DependencyFlags.SoftDependency)>]
type Plugin() =
    inherit BaseUnityPlugin()

    member _.Awake() =
        let assembly = Assembly.GetExecutingAssembly()
        fork CancellationToken.None <| fun () -> valueTask {
            initLobbyCompatibility pluginName pluginVersion
            initGeneralLethalConfig assembly localConfig.General

            let enemies = getEnemyConfigEntries()
            if List.isEmpty enemies then
                logWarning "Mirage.Enemies.cfg has not been generated yet. Please host a game to create the file."
            else
                initEnemiesLethalConfig assembly enemies

            initNetcodePatcher assembly

            ignore <| Directory.CreateDirectory mirageDirectory
            let! settings = initSettings <| Path.Join(mirageDirectory, "settings.json")
            logInfo $"Loaded settings: {JsonConvert.SerializeObject settings}"
            ignore <| deleteRecordings settings
            Application.add_quitting(fun () -> ignore << deleteRecordings <| getSettings())

            // Credits goes to DissonanceLagFix: https://thunderstore.io/c/lethal-company/p/linkoid/DissonanceLagFix/
            for category in Seq.cast<LogCategory> <| Enum.GetValues typeof<LogCategory> do
                Logs.SetLogLevel(category, LogLevel.Error)

            // Hooks.
            cacheDissonance()
            disableAudioSpatializer()
            registerPrefab()
            syncConfig()
            readMicrophone recordingDirectory
            hookMaskedEnemy()
            hookPlayerControllerB()
        }