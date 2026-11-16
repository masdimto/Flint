using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace FlintBuffPlugin
{
    [ApiVersion(2, 1)]
    public class FlintBuff : TerrariaPlugin
    {
        public override string Name => "FlintBuff";
        public override System.Version Version => new System.Version(1, 1, 0);
        public override string Author => "CustomPlugin";
        public override string Description => "Buff senjata Flint (Dead Cells): makin lama di-charge, scale senjata (+0.1/detik) dan panjang trail asli Flint (+0.1/detik) makin besar.";

        private bool[] isCharging = new bool[Main.maxPlayers];
        private int[] chargeTicks = new int[Main.maxPlayers];
        private int[] lastNotifiedSecond = new int[Main.maxPlayers];
        private float[] activeBonus = new float[Main.maxPlayers];
        private int[] releaseWindowTicks = new int[Main.maxPlayers];
        private float[] originalItemScale = new float[Main.maxPlayers];
        private HashSet<int> boostedProjectiles = new HashSet<int>();

        // Waktu charge dasar sebelum bonus mulai dihitung (0.6 detik = 36 tick)
        private const int BaseTriggerTicks = 36;

        public FlintBuff(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ServerJoin.Register(this, OnServerJoin);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);

            Commands.ChatCommands.Add(new Command("flint.buff", CheckStatusCommand, "flint", "flintbuff")
            {
                HelpText = "Mengecek status buff senjata Flint."
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.ServerJoin.Deregister(this, OnServerJoin);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
            }
            base.Dispose(disposing);
        }

        private void CheckStatusCommand(CommandArgs args)
        {
            args.Player.SendSuccessMessage("[FlintBuff] Aktif! Tahan serangan Flint: setiap 1 detik menambah scale senjata +0.1 dan panjang trail asli +0.1.");
        }

        private void OnServerJoin(JoinEventArgs args)
        {
            ResetPlayerState(args.Who);
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            ResetPlayerState(args.Who);
        }

        private void ResetPlayerState(int plrIdx)
        {
            if (plrIdx >= 0 && plrIdx < Main.maxPlayers)
            {
                isCharging[plrIdx] = false;
                chargeTicks[plrIdx] = 0;
                lastNotifiedSecond[plrIdx] = 0;
                activeBonus[plrIdx] = 0f;
                releaseWindowTicks[plrIdx] = 0;
                originalItemScale[plrIdx] = 1f;
            }
        }

        private bool IsFlintWeapon(Item item)
        {
            if (item == null || item.IsAir)
                return false;

            string rawName = Lang.GetItemNameValue(item.type);
            if (!string.IsNullOrEmpty(rawName) && rawName.Equals("Flint", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(item.Name) && item.Name.Equals("Flint", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private void OnGameUpdate(EventArgs args)
        {
            // Bersihkan daftar proyektil yang sudah tidak aktif
            boostedProjectiles.RemoveWhere(idx => idx < 0 || idx >= Main.maxProjectiles || !Main.projectile[idx].active);

            for (int i = 0; i < Main.maxPlayers; i++)
            {
                if (releaseWindowTicks[i] > 0)
                {
                    releaseWindowTicks[i]--;
                    if (releaseWindowTicks[i] <= 0)
                    {
                        activeBonus[i] = 0f;
                    }
                }

                TSPlayer tsPlayer = TShock.Players[i];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer == null)
                    continue;

                if (!tsPlayer.HasPermission("flint.buff"))
                    continue;

                Player player = tsPlayer.TPlayer;
                Item heldItem = player.HeldItem;

                if (!IsFlintWeapon(heldItem))
                {
                    if (isCharging[i] || activeBonus[i] > 0f)
                        ResetPlayerState(i);
                    continue;
                }

                // 1. Pemain sedang menahan tombol serang (charging)
                if (player.controlUseItem)
                {
                    if (!isCharging[i])
                    {
                        isCharging[i] = true;
                        originalItemScale[i] = heldItem.scale > 0f ? heldItem.scale : 1f;
                    }

                    chargeTicks[i]++;

                    if (chargeTicks[i] >= BaseTriggerTicks)
                    {
                        int extraTicks = chargeTicks[i] - BaseTriggerTicks;
                        int extraSeconds = extraTicks / 60;
                        float currentBonus = extraSeconds * 0.1f;
                        activeBonus[i] = currentBonus;

                        // Perbesar scale senjata Flint secara real-time saat di-charge
                        float targetItemScale = originalItemScale[i] + currentBonus;
                        if (Math.Abs(heldItem.scale - targetItemScale) > 0.01f)
                        {
                            heldItem.scale = targetItemScale;
                            NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, i, player.selectedItem);
                        }

                        // Tanda percikan api di atas kepala pemain setiap naik 1 detik (+0.1)
                        if (extraSeconds > lastNotifiedSecond[i])
                        {
                            lastNotifiedSecond[i] = extraSeconds;
                            SpawnChargePulseVisual(player, extraSeconds);
                        }
                    }
                }
                else
                {
                    // 2. Pemain melepas tombol serang (ayunan & trail asli Flint keluar)
                    if (isCharging[i])
                    {
                        if (chargeTicks[i] >= BaseTriggerTicks)
                        {
                            int extraTicks = chargeTicks[i] - BaseTriggerTicks;
                            activeBonus[i] = (extraTicks / 60) * 0.1f;
                            // Buka jendela 30 tick (0.5 detik) untuk menangkap seluruh proyektil ayunan & trail asli Flint yang baru muncul
                            releaseWindowTicks[i] = 30;
                        }
                        else
                        {
                            activeBonus[i] = 0f;
                            heldItem.scale = originalItemScale[i];
                            NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, i, player.selectedItem);
                        }

                        isCharging[i] = false;
                        chargeTicks[i] = 0;
                        lastNotifiedSecond[i] = 0;
                    }
                    else if (releaseWindowTicks[i] <= 0 && Math.Abs(heldItem.scale - originalItemScale[i]) > 0.01f)
                    {
                        // Kembalikan ukuran senjata ke normal setelah ayunan selesai
                        heldItem.scale = originalItemScale[i];
                        NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, i, player.selectedItem);
                    }
                }

                // 3. Perbesar scale & perpanjang trail proyektil ASLI milik senjata Flint (baik saat ditahan maupun dilepas)
                float bonus = activeBonus[i];
                if (bonus > 0f && (isCharging[i] || releaseWindowTicks[i] > 0))
                {
                    for (int p = 0; p < Main.maxProjectiles; p++)
                    {
                        Projectile proj = Main.projectile[p];
                        if (!proj.active || proj.owner != i)
                            continue;

                        // Abaikan percikan api indikator di atas kepala pemain
                        if (proj.type == ProjectileID.MolotovFire)
                            continue;

                        // Terapkan pembesaran scale (+0.1/detik) dan perpanjangan trail (+0.1/detik) pada proyektil asli Flint
                        if (!boostedProjectiles.Contains(p))
                        {
                            boostedProjectiles.Add(p);

                            float scaleMultiplier = 1.0f + bonus;
                            proj.scale *= scaleMultiplier;
                            proj.width = (int)Math.Round(proj.width * scaleMultiplier);
                            proj.height = (int)Math.Round(proj.height * scaleMultiplier);

                            // Perpanjang durasi & jangkauan rambat trail asli Flint (+0.1 per detik)
                            proj.timeLeft = (int)Math.Round(proj.timeLeft * scaleMultiplier);
                            proj.velocity *= (1.0f + bonus * 0.5f);
                            proj.damage = (int)Math.Round(proj.damage * (1.0f + bonus));
                            proj.netUpdate = true;

                            NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, p);
                        }
                    }
                }
            }
        }

        private void SpawnChargePulseVisual(Player player, int level)
        {
            int visualIdx = Projectile.NewProjectile(
                Projectile.GetNoneSource(),
                player.Center + new Vector2(0f, -24f),
                new Vector2(0f, -2f),
                ProjectileID.MolotovFire,
                0,
                0f,
                Main.maxPlayers
            );

            if (visualIdx >= 0 && visualIdx < Main.maxProjectiles)
            {
                Projectile p = Main.projectile[visualIdx];
                p.friendly = false;
                p.hostile = false;
                p.tileCollide = false;
                p.scale = 1.0f + (level * 0.1f);
                p.timeLeft = 18;
                NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, visualIdx);
            }
        }
    }
}
