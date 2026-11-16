using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace MonsterIntentRandomizer;

[HarmonyPatch]
internal static class MultiplayerRules
{
    private const string ModNamePrefix = "MonsterIntentRandomizer-";
    private static readonly ConditionalWeakTable<INetGameService, Task<RuleConfiguration?>> Sessions = new();
    private sealed record HostSettings(bool Enabled, JsonElement Settings, JsonElement Profiles);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PeerVersionInfo), nameof(PeerVersionInfo.LocalDefault))]
    private static void IncludeBuildInHandshake(ref PeerVersionInfo __result)
    {
        var mods = __result.gameplayAffectingMods;
        int index = mods?.FindIndex(IsThisMod) ?? -1;
        if (index < 0)
            return;

        __result.gameplayAffectingMods = [.. mods];
        __result.gameplayAffectingMods[index] = $"{mods![index]}+{typeof(MultiplayerRules).Module.ModuleVersionId:N}";
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NetHostGameService), MethodType.Constructor, new[] { typeof(PeerVersionInfo) })]
    private static void StoreHostRules(NetHostGameService __instance)
    {
        var settings = ModSettings.Current;
        var packet = new HostRulesMessage
        {
            Payload = JsonSerializer.SerializeToUtf8Bytes(new HostSettings(settings.Enabled, settings.Settings, settings.Profiles))
        };
        Sessions.Add(__instance, Task.FromResult(settings.Enabled ? ModSettings.Rules : null));
        // This handler is registered before the lobby's initial game info handler on the same connection.
        __instance.ClientConnected += peer => __instance.SendMessage(packet, peer);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NetClientGameService), MethodType.Constructor, new[] { typeof(PeerVersionInfo) })]
    private static void AwaitHostRules(NetClientGameService __instance)
    {
        var snapshot = new TaskCompletionSource<RuleConfiguration?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Sessions.Add(__instance, snapshot.Task);
        __instance.RegisterMessageHandler<HostRulesMessage>((packet, sender) =>
        {
            if (sender != __instance.HostNetId || snapshot.Task.IsCompleted)
                return;

            try
            {
                var settings = JsonSerializer.Deserialize<HostSettings>(packet.Payload)
                    ?? throw new InvalidDataException("The host sent empty monster intent settings.");
                var rules = settings.Enabled
                    ? ModSettings.Parse(new StoredSettings(true, settings.Settings, settings.Profiles)) : null;
                snapshot.TrySetResult(rules);
                Log.Info("[MonsterIntentRandomizer] Received host gameplay settings.");
            }
            catch (Exception error)
            {
                Log.Error($"[MonsterIntentRandomizer] Invalid host gameplay settings: {error.Message}");
                snapshot.TrySetCanceled();
                __instance.Disconnect(NetError.InternalError);
            }
        });
        __instance.Disconnected += _ => snapshot.TrySetCanceled();
    }

    internal static async Task<RuleConfiguration?> For(INetGameService session)
    {
        try
        {
            if (!Sessions.TryGetValue(session, out var rules))
                throw new InvalidOperationException("The multiplayer rules handler was not registered.");
            return await rules.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException)
        {
            Log.Error($"[MonsterIntentRandomizer] Cannot start a multiplayer combat without host gameplay settings: {error.Message}");
            session.Disconnect(NetError.InternalError);
            throw;
        }
    }

    private static bool IsThisMod(string name) => name.StartsWith(ModNamePrefix, StringComparison.Ordinal);
}

public struct HostRulesMessage : INetMessage
{
    internal byte[] Payload { get; set; }
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Payload.Length);
        writer.WriteBytes(Payload, Payload.Length);
    }

    public void Deserialize(PacketReader reader)
    {
        int length = reader.ReadInt();
        if (length < 0 || length > (reader.Buffer.Length * 8 - reader.BitPosition) / 8)
            throw new InvalidDataException("Invalid monster intent settings packet size.");
        Payload = new byte[length];
        reader.ReadBytes(Payload, length);
    }
}
