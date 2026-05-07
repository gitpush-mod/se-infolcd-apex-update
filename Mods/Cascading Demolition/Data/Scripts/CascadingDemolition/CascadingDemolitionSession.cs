using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace CascadingDemolition
{
    /// <summary>
    /// Server-side session component that converts "armed warhead destroyed by
    /// another blast" into "armed warhead detonates BEFORE its block dies".
    ///
    /// Vanilla SE has the inverse problem: when warhead A's explosion destroys
    /// warhead B's block, B is removed via normal block-destruction without
    /// firing its own explosion. We hook the damage system to pre-empt that:
    /// when a damage event would kill an armed warhead, we manually create our
    /// own explosion at that warhead's position. The original damage finalizes
    /// and destroys the block, our explosion fires alongside it, and any other
    /// armed warheads in the new blast radius go through the same path.
    ///
    /// Two safeguards on the chain:
    ///
    /// 1. Spatial filter — SE's vanilla MarkForExplosion mechanism damages ALL
    ///    warheads on a grid when one detonates, regardless of distance. We
    ///    resolve the damage event's AttackerId; if it's a warhead, we only
    ///    chain when the target is within the source's base ExplosionRadius.
    ///    For warheads OUTSIDE that radius we mutate info.Amount to 0 so they
    ///    don't even take damage — preserves staggered-timer demolition setups.
    ///
    /// 2. No voxel damage from chained warheads — instead of calling vanilla
    ///    IMyWarhead.Detonate() (which spawns an explosion with AFFECT_VOXELS),
    ///    we build a MyExplosionInfo ourselves and pass it to MyExplosions.
    ///    AddExplosion with the AFFECT_VOXELS flag deliberately omitted. Block
    ///    damage, force, deformation, decals, debris, sound — all preserved.
    ///    Voxels stay intact.
    ///
    ///    Known limitation: the FIRST warhead (the one the player triggers via
    ///    UI/timer) still uses vanilla Detonate() and damages voxels — we can't
    ///    intercept the player's direct trigger without a per-block GameLogic
    ///    component. So a single user-triggered warhead leaves a vanilla-sized
    ///    crater; chained warheads don't add to it.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class CascadingDemolitionSession : MySessionComponentBase
    {
        public override void LoadData()
        {
            // Damage handlers are server-authoritative. Running on a client in MP
            // would just produce ghost detonations that don't match the server.
            if (!MyAPIGateway.Multiplayer.IsServer) return;

            if (MyAPIGateway.Session?.DamageSystem == null) return;

            // Priority 0 — runs alongside other handlers. We mutate info.Amount
            // for out-of-radius warheads, so positioning matters less than for
            // pure-read handlers, but 0 is fine for our use case.
            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, OnBeforeDamage);
        }

        private void OnBeforeDamage(object target, ref MyDamageInformation info)
        {
            // Hot path — every bullet, grinder hit, voxel collision, etc. comes
            // through here. Bail as fast as possible on the common case.
            var slim = target as IMySlimBlock;
            if (slim == null) return;

            var warhead = slim.FatBlock as IMyWarhead;
            if (warhead == null) return;

            // Unarmed warhead is just a block. Let it die normally.
            if (!warhead.IsArmed) return;

            // Will this damage actually destroy the block? slim.Integrity is the
            // current remaining integrity. If incoming damage doesn't kill it,
            // no need to chain — let the block soak the hit and stay armed.
            if (info.Amount < slim.Integrity) return;

            // Spatial filter: if damage came from another warhead, only chain
            // when target is within the source warhead's base ExplosionRadius.
            // Otherwise we cancel the damage entirely so SE's vanilla
            // MarkForExplosion (grid-wide warhead damage) can't kill warheads
            // that are physically outside the actual blast.
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
                            // Out of radius — cancel damage AND don't chain. The
                            // warhead survives intact and any independent timer
                            // it has continues running.
                            info.Amount = 0;
                            return;
                        }
                    }
                }
            }

            // In radius (or non-warhead source). Fire our own explosion at this
            // warhead's position with no voxel damage. Original incoming damage
            // still finalizes and destroys this block normally — we just inject
            // our own explosion alongside, which damages neighbors and propagates
            // the cascade through the BeforeDamageHandler the next time it fires.
            FireBlockOnlyExplosion(warhead);
        }

        private static void FireBlockOnlyExplosion(IMyWarhead warhead)
        {
            float radius = GetWarheadRadius(warhead);
            float damage = GetWarheadDamage(warhead);
            Vector3D position = warhead.GetPosition();

            // Particle preset by radius — matches the bucketing pattern used by
            // Modular Encounters System's DamageHelper, which is the canonical
            // reference for mod-driven explosions in the SE ecosystem.
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
                // SE's explosion routine skips the voxel-carve step entirely. All
                // other vanilla effects (block damage, force, deformation, debris,
                // decals, particles, shrapnel) are preserved.
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
            var slim = warhead?.SlimBlock;
            var def = slim?.BlockDefinition as MyWarheadDefinition;
            // Default to engine's hardcoded 30m cap if we can't read the
            // definition (e.g. attacker block was already closed).
            return def?.ExplosionRadius ?? 30f;
        }

        private static float GetWarheadDamage(IMyWarhead warhead)
        {
            var slim = warhead?.SlimBlock;
            var def = slim?.BlockDefinition as MyWarheadDefinition;
            // Default matches our SBC override. If the SBC didn't load for some
            // reason, this still gives meaningful damage.
            return def?.WarheadExplosionDamage ?? 30000f;
        }
    }
}
