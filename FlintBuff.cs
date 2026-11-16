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
        public override System.Version Version => new System.Version(1, 0, 0);
        public override string Author => "CustomPlugin";
        public override string Description => "Buff senjata Flint (Dead Cells): makin lama di-charge, scale (+0.1/detik) dan panjang trail (+0.1/detik) makin besar.";

        private class GrowingWaveData
        {
            public int ProjIndex;
            public int TicksLeft;
            public float TargetScale;
            public int Damage;
        }

        private bool[] isCharging = new bool[Main.maxPlayers];
        private int[] chargeTicks = new int[Main.maxPlayers];
        private int[] lastNotifiedSecond = new int[Main.maxPlayers];
        private List<GrowingWaveData> activeWaves = new List<GrowingWaveData>();

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
            args.Player.SendSuccessMessage("[FlintBuff] Aktif! Tahan serangan Flint: setiap 1 detik menambah scale +0.1 dan panjang trail +0.1.");
        }

        private void OnServerJoin(JoinEventArgs args)
        {
            if (args.Who >= 0 && args.Who < Main.maxPlayers)
            {
                isCharging[args.Who] = false;
                chargeTicks[args.Who] = 0;
                lastNotifiedSecond[args.Who] = 0;
            }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            if (args.Who >= 0 && args.Who < Main.maxPlayers)
            {
                isCharging[args.Who] = false;
                chargeTicks[args.Who] = 0;
                lastNotifiedSecond[args.Who] = 0;
            }
        }

        private bool IsFlintWeapon(Item item)
        {
            if (item == null || item.IsAir)
                return false;

            // Cek nama item agar aman di semua versi OTAPI/TShock 1.4.5
            string rawName = Lang.GetItemNameValue(item.type);
            if (!string.IsNullOrEmpty(rawName) && rawName.Equals("Flint", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(item.Name) && item.Name.Equals("Flint", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private void OnGameUpdate(EventArgs args)
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                TSPlayer tsPlayer = TShock.Players[i];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer == null)
                    continue;

                if (!tsPlayer.HasPermission("flint.buff"))
                    continue;

                Player player = tsPlayer.TPlayer;
                Item heldItem = player.HeldItem;

                if (!IsFlintWeapon(heldItem))
                {
                    isCharging[i] = false;
                    chargeTicks[i] = 0;
                    lastNotifiedSecond[i] = 0;
                    continue;
                }

                // Pemain sedang menahan tombol serang (charging)
                if (player.controlUseItem)
                {
                    isCharging[i] = true;
                    chargeTicks[i]++;

                    // Setelah charge awal terpicu (BaseTriggerTicks), hitung setiap kelipatan 1 detik (60 tick)
                    if (chargeTicks[i] >= BaseTriggerTicks)
                    {
                        int extraTicks = chargeTicks[i] - BaseTriggerTicks;
                        int extraSeconds = extraTicks / 60;

                        // Efek visual indikator di pemain setiap kali naik +1 detik (+0.1 scale & +0.1 trail)
                        if (extraSeconds > lastNotifiedSecond[i])
                        {
                            lastNotifiedSecond[i] = extraSeconds;
                            SpawnChargePulseVisual(player, extraSeconds);
                        }
                    }
                }
                else
                {
                    // Pemain melepas tombol serang
                    if (isCharging[i])
                    {
                        if (chargeTicks[i] >= BaseTriggerTicks)
                        {
                            int extraTicks = chargeTicks[i] - BaseTriggerTicks;
                            // Setiap 1 detik (60 tick) = +0.1 scale & +0.1 panjang trail
                            float bonusMultiplier = (extraTicks / 60) * 0.1f;
                            ReleaseChargedFlintWave(player, heldItem, bonusMultiplier);
                        }

                        isCharging[i] = false;
                        chargeTicks[i] = 0;
                        lastNotifiedSecond[i] = 0;
                    }
                }
            }

            // Update proyektil gelombang api Flint yang aktif (sinkronisasi scale & damage)
            for (int k = activeWaves.Count - 1; k >= 0; k--)
            {
                GrowingWaveData wave = activeWaves[k];
                wave.TicksLeft--;

                if (wave.ProjIndex < 0 || wave.ProjIndex >= Main.maxProjectiles)
                {
                    activeWaves.RemoveAt(k);
                    continue;
                }

                Projectile proj = Main.projectile[wave.ProjIndex];
                if (wave.TicksLeft <= 0 || !proj.active)
                {
                    activeWaves.RemoveAt(k);
                    continue;
                }

                proj.scale = wave.TargetScale;

                // Berikan damage gelombang api ke musuh yang disentuh
                if (wave.TicksLeft % 6 == 0)
                {
                    for (int n = 0; n < Main.maxNPCs; n++)
                    {
                        NPC npc = Main.npc[n];
                        if (npc.active && !npc.friendly && !npc.dontTakeDamage && proj.Hitbox.Intersects(npc.Hitbox))
                        {
                            npc.StrikeNPC(wave.Damage, 6f, proj.direction);
                            NetMessage.SendData((int)PacketTypes.NpcStrike, -1, -1, null, npc.whoAmI, wave.Damage, 6f, proj.direction, 0);
                            npc.AddBuff(BuffID.OnFire3, 180);
                        }
                    }
                }
            }
        }

        private void SpawnChargePulseVisual(Player player, int level)
        {
            // Memunculkan kilau api singkat di tubuh pemain sebagai tanda charge bertambah +0.1
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

        private void ReleaseChargedFlintWave(Player player, Item heldItem, float bonusMultiplier)
        {
            float finalScale = 1.0f + bonusMultiplier;
            float trailLengthFactor = 1.0f + bonusMultiplier;

            // Panjang gelombang (jumlah segmen trail merambat ke depan) bertambah seiring bonus +0.1/detik
            int baseSegments = 5;
            int totalSegments = Math.Max(5, (int)Math.Round(baseSegments * trailLengthFactor));
            float stepDistance = 36f * finalScale;

            int baseDamage = player.GetWeaponDamage(heldItem);
            int finalDamage = Math.Max(1, (int)(baseDamage * (1.25f + bonusMultiplier)));

            for (int s = 1; s <= totalSegments; s++)
            {
                Vector2 spawnPos = player.Bottom + new Vector2(player.direction * (s * stepDistance), -18f * finalScale);
                Vector2 velocity = new Vector2(player.direction * (6f + bonusMultiplier * 2f), 0f);

                int projIdx = Projectile.NewProjectile(
                    Projectile.GetNoneSource(),
                    spawnPos,
                    velocity,
                    ProjectileID.DD2FlameBurstTowerT2Shot,
                    finalDamage,
                    heldItem.knockBack,
                    Main.maxPlayers
                );

                if (projIdx >= 0 && projIdx < Main.maxProjectiles)
                {
                    Projectile proj = Main.projectile[projIdx];
                    proj.friendly = true;
                    proj.hostile = false;
                    proj.tileCollide = false;
                    proj.scale = finalScale;
                    proj.width = (int)(32 * finalScale);
                    proj.height = (int)(32 * finalScale);
                    proj.timeLeft = 22 + (s * 3); // Muncul beruntun membentuk trail panjang ke depan
                    NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, projIdx);

                    activeWaves.Add(new GrowingWaveData
                    {
                        ProjIndex = projIdx,
                        TicksLeft = proj.timeLeft,
                        TargetScale = finalScale,
                        Damage = finalDamage
                    });
                }
            }
        }
    }
}
