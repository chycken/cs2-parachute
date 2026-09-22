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

[PluginMetadata(
    Id = "Parachute",
    Version = "v6-fully-optimized",
    Name = "Parachute",
    Author = "schwarper"
)]
public sealed class Parachute(ISwiftlyCore core) : BasePlugin(core)
{
    public sealed class PlayerData
    {
        public IPlayer? Player;
        public CDynamicProp? Entity;

        public bool Flying;
        public bool HasPermission;
        public bool SkipTick = true;
    }

    public IConVar<bool>? sv_parachute;
    private readonly PlayerData?[] _playerDatas = new PlayerData[64];
    public static Config Config { get; private set; } = null!;

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

        // Ensure FallSpeed is negative for downward velocity checks
        if (Config.Settings.FallSpeed > 0)
            Config.Settings.FallSpeed *= -1.0f;

        sv_parachute = Core.ConVar.Find<bool>("sv_parachute") ?? Core.ConVar.Create("sv_parachute", "Parachute on/off", true);

        if (hotReload)
        {
            foreach (var player in Core.PlayerManager.GetAllPlayers())
            {
                InitPlayer(player);
            }
        }
    }

    public override void Unload()
    {
        for (int i = 0; i < _playerDatas.Length; i++)
        {
            var data = _playerDatas[i];
            if (data == null) continue;

            RemoveParachute(data);
            if (data.Player?.PlayerPawn is { } pawn)
            {
                pawn.ActualGravityScale = 1.0f;
            }
            _playerDatas[i] = null;
        }
    }

    private void InitPlayer(IPlayer player)
    {
        int playerId = player.PlayerID;
        if (playerId < 0 || playerId >= _playerDatas.Length) return;

        _playerDatas[playerId] = new PlayerData
        {
            Player = player,
            HasPermission = string.IsNullOrEmpty(Config.Settings.AdminFlag) || Core.Permission.PlayerHasPermission(player.SteamID, Config.Settings.AdminFlag)
        };
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
        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;
        int playerId = player.PlayerID;

        if (playerId >= 0 && playerId < _playerDatas.Length)
        {
            if (_playerDatas[playerId] is { } data) RemoveParachute(data);
            _playerDatas[playerId] = null;
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;
        int playerId = player.PlayerID;
        if (playerId < 0 || playerId >= _playerDatas.Length) return HookResult.Continue;

        var data = _playerDatas[playerId] ?? new PlayerData();
        RemoveParachute(data);

        data.Flying = false;
        data.SkipTick = true;
        data.Player = player;
        data.HasPermission = string.IsNullOrEmpty(Config.Settings.AdminFlag) || Core.Permission.PlayerHasPermission(player.SteamID, Config.Settings.AdminFlag);
        _playerDatas[playerId] = data;

        if (player.PlayerPawn is { } pawn) pawn.ActualGravityScale = 1.0f;
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is not { } player) return HookResult.Continue;
        int playerId = player.PlayerID;
        if (playerId < 0 || playerId >= _playerDatas.Length) return HookResult.Continue;

        if (_playerDatas[playerId] is { } data)
        {
            RemoveParachute(data);
            data.Flying = false;
            data.SkipTick = true;
            if (player.PlayerPawn is { } pawn) pawn.ActualGravityScale = 1.0f;
        }
        return HookResult.Continue;
    }

    [EventListener<EventDelegates.OnPrecacheResource>]
    public void OnServerPrecacheResources(IOnPrecacheResourceEvent @event)
    {
        if (!string.IsNullOrEmpty(Config.Settings.Model))
        {
            @event.AddItem(Config.Settings.Model);
        }
    }

    [EventListener<EventDelegates.OnTick>]
    public void OnTick()
    {
        if (sv_parachute?.Value != true) return;

        bool hasModel = !string.IsNullOrEmpty(Config.Settings.Model);

        for (int i = 0; i < _playerDatas.Length; i++)
        {
            var data = _playerDatas[i];
            if (data == null || !data.HasPermission) continue;

            var player = data.Player;
            if (player == null || !player.IsValid) continue;

            // GYORS KILÉPÉS: Ha nem nyomja az E gombot, azonnal lépjünk tovább
            if ((player.PressedButtons & GameButtonFlags.E) == 0)
            {
                if (data.Flying && player.PlayerPawn is { } flyingPawn)
                {
                    StopParachute(data, flyingPawn);
                }
                continue;
            }

            var playerPawn = player.PlayerPawn;
            if (playerPawn == null || playerPawn.LifeState != (int)LifeState_t.LIFE_ALIVE)
            {
                if (data.Flying && playerPawn != null) StopParachute(data, playerPawn);
                continue;
            }

            // Földön van -> Nincs ejtőernyő
            if (playerPawn.GroundEntity.IsValid)
            {
                if (data.Flying) StopParachute(data, playerPawn);
                continue;
            }

            // Tusz szállítása esetén letiltva
            if (Config.Settings.DisableWhenCarryingHostage && playerPawn.HostageServices?.CarriedHostageProp.Value != null)
            {
                if (data.Flying) StopParachute(data, playerPawn);
                continue;
            }

            var velocity = playerPawn.AbsVelocity;

            // Nem lefelé esik
            if (velocity.Z >= 0.0f)
            {
                if (data.Flying) StopParachute(data, playerPawn);
                continue;
            }

            // Modell létrehozása és mozgatása
            if (hasModel)
            {
                if (data.Entity == null || !data.Entity.IsValid)
                {
                    data.Entity = CreateParachute(playerPawn);
                    data.SkipTick = true;
                }

                if (data.Entity != null && data.Entity.IsValid)
                {
                    data.SkipTick = !data.SkipTick;
                    if (!data.SkipTick)
                    {
                        data.Entity.Teleport(playerPawn.AbsOrigin, playerPawn.AbsRotation, velocity);
                    }
                }
            }

            if (!data.Flying)
            {
                playerPawn.ActualGravityScale = 0.1f;
                data.Flying = true;
            }

            // JAVÍTVA: Az új esési sebesség ténylegesen beállításra kerül a játékosnak
            float newSpeedZ = (velocity.Z >= Config.Settings.FallSpeed && Config.Settings.Linear) || Config.Settings.Decrease == 0.0f
                ? Config.Settings.FallSpeed
                : velocity.Z + Config.Settings.Decrease;

            playerPawn.AbsVelocity = new Vector(velocity.X, velocity.Y, newSpeedZ);
        }
    }

    private void StopParachute(PlayerData data, CCSPlayerPawn playerPawn)
    {
        RemoveParachute(data);
        data.Flying = false;
        data.SkipTick = true;
        playerPawn.ActualGravityScale = 1.0f;
    }

    private CDynamicProp? CreateParachute(CCSPlayerPawn playerPawn)
    {
        var entity = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic_override");
        if (entity?.IsValid is not true) return null;

        entity.Teleport(playerPawn.AbsOrigin, QAngle.Zero, Vector.Zero);
        entity.DispatchSpawn();
        entity.SetModel(Config.Settings.Model);
        return entity;
    }

    private static void RemoveParachute(PlayerData? playerData)
    {
        if (playerData?.Entity?.IsValid is true)
        {
            playerData.Entity.Despawn();
        }
        if (playerData != null)
        {
            playerData.Entity = null;
        }
    }
}
