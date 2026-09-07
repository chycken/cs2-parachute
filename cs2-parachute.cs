using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using Tomlyn.Extensions.Configuration;

namespace Parachute;

public sealed class Config
{
    public Settings Settings { get; set; } = new();
}

public sealed class Settings
{
    public float FallSpeed { get; set; } = 85;
    public bool Linear { get; set; } = true;
    public string Model { get; set; } = "";
    public float Decrease { get; set; } = 15;
    public string AdminFlag { get; set; } = string.Empty;
    public bool DisableWhenCarryingHostage { get; set; } = false;
}

[PluginMetadata(Id = "Parachute", Version = "v5", Name = "Parachute", Author = "schwarper")]
public sealed class Parachute(ISwiftlyCore core) : BasePlugin(core)
{
    public class PlayerData
    {
        public CDynamicProp? Entity;
        public bool Flying;
        public bool HasPermission;
        public bool SkipTick = true;
    }

    public IConVar<bool>? sv_parachute;
    private readonly PlayerData?[] _playerDatas = new PlayerData[64];

    public static Config Config { get; set; } = null!;

    public override void Load(bool hotReload)
    {
        const string ConfigFileName = "config.toml";
        const string ConfigSection = "Parachute";
        Core.Configuration
            .InitializeTomlWithModel<Config>(ConfigFileName, ConfigSection)
            .Configure(cfg => cfg.AddTomlFile(ConfigFileName, optional: false, reloadOnChange: true));

        ServiceCollection services = new();
        services.AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<Config>()
            .BindConfiguration(ConfigSection);
        var provider = services.BuildServiceProvider();
        Config = provider.GetRequiredService<IOptions<Config>>().Value;

        Config.Settings.FallSpeed *= -1.0f;

        if (hotReload)
        {
            var players = Core.PlayerManager.GetAllPlayers();
            foreach (var player in players)
            {
                InitPlayer(player);
            }
        }

        sv_parachute = Core.ConVar.Find<bool>("sv_parachute") ?? Core.ConVar.Create("sv_parachute", "Parachute on/off", true);
    }

    public override void Unload()
    {
    }

    [EventListener<EventDelegates.OnConVarValueChanged>]
    public void OnConVarValueChanged(IOnConVarValueChanged @event)
    {
        if (@event.ConVarName == "sv_parachute" && bool.TryParse(@event.NewValue, out bool value) && !value)
        {
            for (int i = 0; i < _playerDatas.Length; i++)
            {
                var data = _playerDatas[i];
                if (data == null || !data.Flying) continue;

                var player = Core.PlayerManager.GetPlayer(i);
                if (player != null)
                {
                    RemoveParachute(data);
                    data.Flying = false;
                    player.PlayerPawn?.ActualGravityScale = 1.0f;
                }
            }
        }
    }

    private void InitPlayer(IPlayer player)
    {
        if (player.PlayerID < 0 || player.PlayerID >= _playerDatas.Length) return;

        var data = new PlayerData
        {
            HasPermission = string.IsNullOrEmpty(Config.Settings.AdminFlag) || Core.Permission.PlayerHasPermission(player.SteamID, Config.Settings.AdminFlag)
        };

        _playerDatas[player.PlayerID] = data;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnect(EventPlayerConnectFull @event)
    {
        if (@event.UserIdPlayer is { } player) InitPlayer(player);
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnClientDisconnect(EventPlayerDisconnect @event)
    {
        if (@event.UserIdPlayer is { } player && player.PlayerID >= 0 && player.PlayerID < _playerDatas.Length)
        {
            RemoveParachute(_playerDatas[player.PlayerID]);
            _playerDatas[player.PlayerID] = null;
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { } player && player.PlayerID >= 0 && player.PlayerID < _playerDatas.Length)
        {
            var data = _playerDatas[player.PlayerID];
            if (data != null)
            {
                RemoveParachute(data);
                data.HasPermission = string.IsNullOrEmpty(Config.Settings.AdminFlag) || Core.Permission.PlayerHasPermission(player.SteamID, Config.Settings.AdminFlag);
            }
            else
            {
                InitPlayer(player);
            }
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is { } player && player.PlayerID >= 0 && player.PlayerID < _playerDatas.Length)
        {
            RemoveParachute(_playerDatas[player.PlayerID]);
        }
        return HookResult.Continue;
    }

    [EventListener<EventDelegates.OnPrecacheResource>]
    public void OnServerPrecacheResources(IOnPrecacheResourceEvent @event)
    {
        if (!string.IsNullOrEmpty(Config.Settings.Model))
            @event.AddItem(Config.Settings.Model);
    }

    [EventListener<EventDelegates.OnTick>]
    public void OnTick()
    {
        if (sv_parachute?.Value is not true)
            return;

        var allPlayers = Core.PlayerManager.GetAllPlayers();
        bool hasParachuteModel = !string.IsNullOrEmpty(Config.Settings.Model);

        foreach (var player in allPlayers)
        {
            if (!player.IsValid)
                continue;

            int pId = player.PlayerID;
            if (pId < 0 || pId >= _playerDatas.Length)
                continue;

            var playerData = _playerDatas[pId];

            if (playerData == null ||
                !playerData.HasPermission ||
                player.PlayerPawn is not { } playerPawn ||
                playerPawn.LifeState != (int)LifeState_t.LIFE_ALIVE)
                continue;

            bool pressingE = (player.PressedButtons & GameButtonFlags.E) != 0;

            if (pressingE && !playerPawn.GroundEntity.IsValid)
            {
                if (Config.Settings.DisableWhenCarryingHostage && playerPawn.HostageServices?.CarriedHostageProp.Value != null)
                    continue;

                var velocity = playerPawn.AbsVelocity;
                if (velocity.Z >= 0.0)
                {
                    if (playerData.Flying)
                    {
                        playerPawn.ActualGravityScale = 1.0f;
                    }
                    continue;
                }

                if (hasParachuteModel)
                {
                    playerData.Entity ??= CreateParachute(playerPawn);
                    playerData.SkipTick = !playerData.SkipTick;

                    if (!playerData.SkipTick)
                        playerData.Entity?.Teleport(playerPawn.AbsOrigin, playerPawn.AbsRotation, playerPawn.AbsVelocity);
                }

                velocity.Z = (velocity.Z >= Config.Settings.FallSpeed && Config.Settings.Linear) || Config.Settings.Decrease == 0.0f
                    ? Config.Settings.FallSpeed
                    : velocity.Z + Config.Settings.Decrease;

                if (!playerData.Flying)
                {
                    playerPawn.ActualGravityScale = 0.1f;
                    playerData.Flying = true;
                }
            }
            else if (playerData.Flying)
            {
                RemoveParachute(playerData);
                playerData.Entity = null;
                playerData.Flying = false;
                playerPawn.ActualGravityScale = 1.0f;
            }
        }
    }

    private CDynamicProp? CreateParachute(CCSPlayerPawn playerPawn)
    {
        if (string.IsNullOrEmpty(Config.Settings.Model)) return null;

        var entity = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic_override");
        if (entity?.IsValid is not true) return null;

        entity.SetModel(Config.Settings.Model);
        entity.Teleport(playerPawn.AbsOrigin, QAngle.Zero, Vector.Zero);
        entity.DispatchSpawn();

        return entity;
    }

    private static void RemoveParachute(PlayerData? playerData)
    {
        if (playerData?.Entity?.IsValid is true)
        {
            playerData.Entity.Despawn();
            playerData.Entity = null;
        }
    }
}
