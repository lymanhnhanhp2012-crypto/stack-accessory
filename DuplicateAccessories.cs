using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace DuplicateAccessories
{
    /// <summary>
    /// Cho phép trang bị nhiều phụ kiện cùng Item ID vào các ô phụ kiện (armor[3..9])
    /// và cộng dồn hiệu ứng của các bản sao.
    /// Target: TShock 5.x / .NET 6 / Terraria 1.4.4.
    /// </summary>
    [ApiVersion(2, 1)]
    public sealed class DuplicateAccessoriesPlugin : TerrariaPlugin
    {
        public override string Name => "DuplicateAccessories";
        public override string Author => "Custom";
        public override string Description => "Equip duplicate accessories and stack their effects.";
        public override Version Version => new Version(1, 0, 0);

        // ---- Hằng số layout slot ----
        private const int InventorySlots = 59;              // 0..58 (inventory, coin, ammo, mouse item)
        private const int FirstAccessory = 3;                // armor[3]
        private const int LastAccessory = 9;                 // armor[9]
        private const int FirstAccessoryPacketSlot = InventorySlots + FirstAccessory;
        private const int LastAccessoryPacketSlot = InventorySlots + LastAccessory;

        private const string Permission = "dupacc.use";
        private const bool Debug = true; // đặt false khi không cần log chẩn đoán
        private const double TickIntervalMs = 250;

        // Luật cộng dồn: ItemID -> (player, số bản sao THÊM ngoài bản đầu tiên).
        // Bản đầu tiên đã được vanilla áp dụng, nên chỉ cộng phần dư.
        private static readonly Dictionary<int, Action<Player, int>> StackRules =
            new Dictionary<int, Action<Player, int>>
            {
                [ItemID.FleshKnuckles] = (p, extra) =>
                {
                    p.aggro += 400 * extra;          // tăng khả năng bị quái nhắm
                    p.statDefense += 8 * extra;      // chỉnh theo phiên bản game nếu cần
                },
                [ItemID.CloudinaBottle] = (p, extra) =>
                {
                    p.hasJumpOption_Cloud = true;
                    p.jumpSpeedBoost += 0.5f * extra;
                },
            };

        private Timer _timer;
        private Command _command;

        public DuplicateAccessoriesPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            // Nếu TShock xử lý PlayerSlot trước plugin, hãy chỉnh tham số priority ở đây.
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);

            _command = new Command(Permission, CmdEquipDuplicate, "dupacc")
            {
                HelpText = "/dupacc <3-9> : chuyển phụ kiện đang cầm vào ô phụ kiện (cho phép trùng ID)."
            };
            Commands.ChatCommands.Add(_command);

            _timer = new Timer(TickIntervalMs) { AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
        }

        // ------------------------------------------------------------------
        // Packet handling
        // ------------------------------------------------------------------
        private void OnGetData(GetDataEventArgs e)
        {
            try
            {
                if (Debug && e.MsgID == PacketTypes.ItemDrop)
                {
                    TShock.Log.Info($"[DupAcc][DEBUG] ItemDrop from={e.Msg.whoAmI} alreadyHandled={e.Handled}");
                    return;
                }

                if (e.MsgID != PacketTypes.PlayerSlot)
                    return;

                byte playerId;
                short slot, stack, netId;
                byte prefix;

                using (var ms = new MemoryStream(e.Msg.readBuffer, e.Index, e.Length))
                using (var r = new BinaryReader(ms))
                {
                    playerId = r.ReadByte();
                    slot = r.ReadInt16();
                    stack = r.ReadInt16();
                    prefix = r.ReadByte();
                    netId = r.ReadInt16();
                }

                if (Debug && stack == 0)
                    TShock.Log.Info($"[DupAcc][DEBUG] PlayerSlot CLEAR from={e.Msg.whoAmI} slot={slot} alreadyHandled={e.Handled}");

                if (e.Handled)
                    return;

                if (slot < FirstAccessoryPacketSlot || slot > LastAccessoryPacketSlot)
                    return;

                var player = TShock.Players[e.Msg.whoAmI];
                if (player == null || !player.Active || player.TPlayer == null || !player.HasPermission(Permission))
                    return;

                if (playerId != player.Index) return;
                if (stack != 1 || netId <= 0 || netId >= ItemID.Count) return;

                int index = slot - InventorySlots;
                var armor = player.TPlayer.armor;

                // Chỉ can thiệp khi là phụ kiện hợp lệ VÀ thực sự trùng ID với ô khác.
                var probe = new Item();
                probe.SetDefaults(netId);
                if (!probe.accessory || probe.type != netId) return;
                if (!IsDuplicate(armor, index, netId)) return;

                if (Debug)
                    TShock.Log.Info($"[DupAcc][DEBUG] Intercept dup accessory player={player.Name} slot={slot} item={netId}");

                EquipDirect(player, index, netId, prefix);
                e.Handled = true; // bỏ qua xử lý mặc định của TShock/server
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[DuplicateAccessories] OnGetData: {ex}");
            }
        }

        private static bool IsDuplicate(Item[] armor, int selfIndex, int netId)
        {
            for (int i = FirstAccessory; i <= LastAccessory; i++)
            {
                if (i != selfIndex && armor[i].type == netId)
                    return true;
            }
            return false;
        }

        /// <summary>Ghi thẳng item vào armor[] và đồng bộ lại cho mọi client.</summary>
        private static void EquipDirect(TSPlayer player, int armorIndex, int netId, byte prefix)
        {
            var item = player.TPlayer.armor[armorIndex];
            item.SetDefaults(netId);
            item.stack = 1;
            if (prefix != 0) item.Prefix(prefix);

            NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null,
                player.Index, InventorySlots + armorIndex, item.prefix);
        }

        // ------------------------------------------------------------------
        // Command: đường đi cho client vanilla (client chặn trùng ID ở UI)
        // ------------------------------------------------------------------
        private void CmdEquipDuplicate(CommandArgs args)
        {
            var player = args.Player;
            var tp = player?.TPlayer;
            if (tp == null || !player.Active) return;

            if (args.Parameters.Count != 1 ||
                !int.TryParse(args.Parameters[0], out int index) ||
                index < FirstAccessory || index > LastAccessory)
            {
                player.SendErrorMessage($"Cú pháp: /dupacc <{FirstAccessory}-{LastAccessory}> (cầm phụ kiện trên tay).");
                return;
            }

            int heldSlot = tp.selectedItem;
            var held = tp.inventory[heldSlot];
            if (held.IsAir || !held.accessory)
            {
                player.SendErrorMessage("Bạn phải cầm một phụ kiện.");
                return;
            }
            if (!tp.armor[index].IsAir)
            {
                player.SendErrorMessage("Ô phụ kiện đó đang có vật phẩm, hãy tháo ra trước.");
                return;
            }

            EquipDirect(player, index, held.type, held.prefix);

            // Chuyển (không nhân bản) vật phẩm: xoá khỏi tay.
            held.TurnToAir();
            NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, heldSlot, 0);
            player.SendSuccessMessage($"Đã trang bị vào ô phụ kiện {index}.");
        }

        // ------------------------------------------------------------------
        // Stacking
        // ------------------------------------------------------------------
        private void OnTick(object sender, ElapsedEventArgs e)
        {
            try
            {
                foreach (var plr in TShock.Players)
                {
                    if (plr == null || !plr.Active) continue;
                    var tp = plr.TPlayer;
                    if (tp == null || !tp.active || tp.dead) continue;
                    if (!plr.HasPermission(Permission)) continue;

                    ApplyStacking(tp);
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[DuplicateAccessories] OnTick: {ex}");
            }
        }

        private static void ApplyStacking(Player tp)
        {
            var armor = tp.armor;

            for (int i = FirstAccessory; i <= LastAccessory; i++)
            {
                var item = armor[i];
                if (item == null || item.IsAir) continue;

                int id = item.type;
                if (!StackRules.TryGetValue(id, out var rule)) continue;

                // Bỏ qua nếu ID này đã được đếm ở ô trước đó (tránh áp dụng nhiều lần).
                bool counted = false;
                for (int k = FirstAccessory; k < i; k++)
                {
                    if (armor[k].type == id) { counted = true; break; }
                }
                if (counted) continue;

                int total = 1;
                for (int j = i + 1; j <= LastAccessory; j++)
                {
                    if (armor[j].type == id) total++;
                }

                if (total > 1)
                    rule(tp, total - 1);
            }
        }

        // ------------------------------------------------------------------
        // Cleanup
        // ------------------------------------------------------------------
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);

                if (_command != null)
                {
                    Commands.ChatCommands.Remove(_command);
                    _command = null;
                }

                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Elapsed -= OnTick;
                    _timer.Dispose();
                    _timer = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
