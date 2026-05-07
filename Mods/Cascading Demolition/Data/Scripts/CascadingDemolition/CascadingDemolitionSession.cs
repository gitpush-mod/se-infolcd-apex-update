using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.ModAPI;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace CascadingDemolition
{
    /// <summary>
    /// Server-side session component. Two purposes:
    ///
    /// 1. Make the warhead chain reliable. Vanilla SE chains warheads via Explosion
    ///    damage events plus an internal MarkForExplosion grid scan, but the result
    ///    is inconsistent: counting-down warheads often vanish silently, some chained
    ///    warheads detonate at reduced strength, others don't fire at all.
    ///
    /// 2. Stop chained warheads from cratering terrain. Vanilla's auto-detonate-on-
    ///    death calls IMyWarhead.Detonate() which spawns an explosion with
    ///    AFFECT_VOXELS — so each chained warhead carves a crater wherever it sits
    ///    when it dies. Multi-warhead clusters near terrain produce stacked craters.
    ///
    /// Approach (BeforeDamageHandler):
    ///
    ///   When fatal damage is about to hit an armed warhead, we take over:
    ///   - Disarm the warhead (this is the kill switch — vanilla checks IsArmed
    ///     before auto-detonating on death, so disarming makes vanilla skip its
    ///     voxel-damaging explosion when the block finally dies).
    ///   - Stop any active countdown.
    ///   - Fire OUR custom block-only explosion at the warhead's position. We build
    ///     MyExplosionInfo manually with AFFECT_VOXELS deliberately omitted — block
    ///     damage, force, deformation, decals, debris, sound all preserved, voxels
    ///     untouched.
    ///   - Cancel the incoming damage (info.Amount = 0) so vanilla doesn't kill the
    ///     block via the damage path (which can have side effects on warheads).
    ///   - Queue the slim block for clean removal next tick via grid.RemoveBlock.
    ///
    /// Two safeguards:
    ///
    /// 1. Spatial filter — if the damage source is another warhead and the target
    ///    is outside the source's blast radius, we cancel damage entirely. This
    ///    prevents vanilla MarkForExplosion (grid-wide warhead damage regardless of
    ///    distance) from killing warheads that are physically outside the actual
    ///    blast. Independent staggered-timer setups keep their countdowns intact.
    ///
    /// 2. Self-attacker skip — when warhead A's own vanilla explosion damages its
    ///    own block (the block sits at the explosion's center), we let it die
    ///    normally. Otherwise we'd add a second explosion on top of the player-
    ///    triggered one, doubling damage to surrounding blocks.
    ///
    /// Known limitation: the FIRST warhead (the one the player triggers via UI/
    /// timer) still goes through vanilla IMyWarhead.Detonate() — we can't intercept
    /// the player's direct trigger without a per-block GameLogicComponent. So a
    /// player-triggered warhead at low altitude will leave a vanilla-sized crater.
    /// At altitude, vanilla's explosion sphere doesn't reach terrain and there's no
    /// crater. Chained warheads (which is where multi-crater "stacked" damage comes
    /// from) are fully voxel-safe.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class CascadingDemolitionSession : MySessionComponentBase
    {
        // Warhead EntityIds we've already taken over. Vanilla can fire multiple
        // damage events on the same target before it dies (MarkForExplosion +
        // chain hits), and our manual explosion produces additional events on the
        // same target until the block is actually removed next tick — without
        // this set we'd recursively re-process and double-fire.
        private readonly HashSet<long> _detonated = new HashSet<long>();

        // Slim blocks queued for manual removal. We can't safely remove during the
        // damage handler (mutating the world from inside a damage callback can
        // break vanilla iteration state), so we defer to UpdateAfterSimulation.
        private readonly List<IMySlimBlock> _pendingRemoval = new List<IMySlimBlock>();

        public override void LoadData()
        {
            // Damage handlers are server-authoritative. On a client this would
            // produce ghost detonations that don't match the server.
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            if (MyAPIGateway.Session?.DamageSystem == null) return;

            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, OnBeforeDamage);
        }

        public override void UpdateAfterSimulation()
        {
            for (int i = 0; i < _pendingRemoval.Count; i++)
            {
                var slim = _pendingRemoval[i];
                if (slim?.CubeGrid == null) continue;
                try { slim.CubeGrid.RemoveBlock(slim, true); } catch { }
            }
            _pendingRemoval.Clear();

            // Bound the dedup set so it doesn't grow forever across long sessions.
            // Chain reactions resolve in a handful of ticks; once the chain ends
            // the set's contents are stale.
            if (_detonated.Count > 1024) _detonated.Clear();
        }

        private void OnBeforeDamage(object target, ref MyDamageInformation info)
        {
            // Hot path — every bullet, grinder hit, voxel collision flows through
            // here. Bail fast on the common case (most events have nothing to do
            // with armed warheads).
            var slim = target as IMySlimBlock;
            if (slim == null) return;

            var warhead = slim.FatBlock as IMyWarhead;
            if (warhead == null) return;

            // Already taken over — suppress all subsequent damage on this warhead
            // until UpdateAfterSimulation removes the block.
            if (_detonated.Contains(warhead.EntityId))
            {
                info.Amount = 0;
                return;
            }

            // Unarmed warhead is just a block. Let it die normally.
            if (!warhead.IsArmed) return;

            // Will this damage destroy the block? If not, no need to chain.
            if (info.Amount < slim.Integrity) return;

            // Self-damage skip: when this warhead's own vanilla explosion damages
            // its own block (the block sits at the explosion's center), let it die
            // normally. Taking over here would fire a second explosion on top of
            // the one vanilla already fired, doubling damage to nearby blocks.
            if (info.AttackerId == warhead.EntityId) return;

            // Spatial filter: if attacker is a different warhead, only chain when
            // target is within source's blast radius. Otherwise cancel damage so
            // vanilla MarkForExplosion (which damages all grid warheads regardless
            // of distance) can't kill warheads physically outside the actual blast.
            if (info.AttackerId != 0)
            {
                IMyEntity attackerEntity;
                if (MyAPIGateway.Entities.TryGetEntityById(info.AttackerId, out attackerEntity))
                {
                    var sourceWarhead = attackerEntity as IMyWarhead;
                    if (sourceWarhead != null)
                    {
                        float sourceRadius = GetWarheadRadius(sourceWarhead);
                        double distance = Vector3D.Distance(
                            sourceWarhead.GetPosition(),
                            warhead.GetPosition());

                        if (distance > sourceRadius)
                        {
                            // Out of radius — preserve the warhead intact. Its
                            // independent countdown (if any) keeps running.
                            info.Amount = 0;
                            return;
                        }
                    }
                }
            }

            // Take over the detonation:
            //   1. Mark as handled (prevents recursion / double-fire).
            //   2. Disarm + stop countdown — vanilla checks IsArmed before auto-
            //      detonating on death, so this stops vanilla from firing its
            //      voxel-damaging explosion when the block dies.
            //   3. Fire our block-only explosion (no AFFECT_VOXELS).
            //   4. Cancel incoming damage so the block doesn't die via the damage
            //      path (warheads have special destroy behavior we want to skip).
            //   5. Queue the slim block for clean removal next simulation tick.
            _detonated.Add(warhead.EntityId);
            warhead.IsArmed = false;
            warhead.StopCountdown();
            FireBlockOnlyExplosion(warhead);
            info.Amount = 0;
            _pendingRemoval.Add(slim);
        }

        private static void FireBlockOnlyExplosion(IMyWarhead warhead)
        {
            float radius = GetWarheadRadius(warhead);
            float damage = GetWarheadDamage(warhead);
            Vector3D position = warhead.GetPosition();

            // Particle preset by radius — bucketing matches Modular Encounters
            // System's DamageHelper, the canonical reference for mod-driven
            // explosions in the SE ecosystem.
            MyExplosionTypeEnum particleType =
                radius <= 6.0 ? MyExplosionTypeEnum.WARHEAD_EXPLOSION_02 :
                radius <= 20.0 ? MyExplosionTypeEnum.WARHEAD_EXPLOSION_15 :
                radius <= 40.0 ? MyExplosionTypeEnum.WARHEAD_EXPLOSION_30 :
                                 MyExplosionTypeEnum.WARHEAD_EXPLOSION_50;

            var explosionInfo = new MyExplosionInfo
            {
                PlayerDamage = damage,
                Damage = damage,
                ExplosionType = particleType,
                ExplosionSphere = new BoundingSphereD(position, radius),
                LifespanMiliseconds = 700,
                ParticleScale = 1f,
                Direction = Vector3.Forward,
                VoxelExplosionCenter = position,
                OwnerEntity = warhead as MyEntity,
                HitEntity = warhead as MyEntity,
                // The deliberately-missing flag here is AFFECT_VOXELS. Without it,
                // SE's explosion routine skips the voxel-carve step. All other
                // vanilla effects (block damage, force, deformation, debris,
                // decals, particles, shrapnel, sound) are preserved.
                ExplosionFlags = MyExplosionFlags.APPLY_FORCE_AND_DAMAGE
                                 | MyExplosionFlags.CREATE_DECALS
                                 | MyExplosionFlags.CREATE_PARTICLE_EFFECT
                                 | MyExplosionFlags.CREATE_SHRAPNELS
                                 | MyExplosionFlags.APPLY_DEFORMATION
                                 | MyExplosionFlags.CREATE_DEBRIS,
                PlaySound = true,
                CheckIntersections = false,
                ObjectsRemoveDelayInMiliseconds = 40,
                VoxelCutoutScale = 0f,
            };

            MyExplosions.AddExplosion(ref explosionInfo);
        }

        private static float GetWarheadRadius(IMyWarhead warhead)
        {
            var def = warhead?.SlimBlock?.BlockDefinition as MyWarheadDefinition;
            // Default to engine's hardcoded 30m cap if the definition can't be
            // read (e.g. attacker block was already closed).
            return def?.ExplosionRadius ?? 30f;
        }

        private static float GetWarheadDamage(IMyWarhead warhead)
        {
            var def = warhead?.SlimBlock?.BlockDefinition as MyWarheadDefinition;
            // Default matches our SBC override. Falls back gracefully if the SBC
            // definition didn't load.
            return def?.WarheadExplosionDamage ?? 30000f;
        }
    }
}
