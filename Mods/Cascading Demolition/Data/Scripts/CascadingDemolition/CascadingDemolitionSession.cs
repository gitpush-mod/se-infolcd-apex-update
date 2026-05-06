using Sandbox.Definitions;
using Sandbox.ModAPI;
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
    /// if a damage event would kill an armed warhead, we call Detonate() first,
    /// then let the damage finalize.
    ///
    /// Spatial filter: SE's vanilla MarkForExplosion mechanism damages ALL
    /// warheads on a grid when one detonates, regardless of distance. Without
    /// a spatial filter we'd chain every armed warhead on the grid. We resolve
    /// the damage event's AttackerId — if it's a warhead, we only chain when
    /// the target is within the source's base ExplosionRadius. Warheads on
    /// independent timers far from the source keep ticking on their own.
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

            // Priority 0 — runs alongside other handlers; we don't mutate damage info.
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
            // Vanilla SE's MarkForExplosion damages every warhead on the grid
            // regardless of distance — without this filter we'd chain them all,
            // which breaks intentional "warheads on staggered timers" setups.
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

                        // Outside source's actual blast — don't chain. The block
                        // will still die from this damage event (vanilla
                        // MarkForExplosion behavior), it just won't fire its own
                        // explosion. That preserves staggered-timer demolition
                        // sequences where each warhead has its own trigger.
                        if (distance > sourceRadius) return;
                    }
                }
            }

            // Note: we deliberately DON'T skip IsCountingDown warheads here.
            // A counting-down warhead caught in another blast should still get
            // a final detonation when destroyed — otherwise the timer's about
            // to fire anyway, and silencing it is just lost yield.
            warhead.Detonate();
        }

        private static float GetWarheadRadius(IMyWarhead warhead)
        {
            var slim = warhead?.SlimBlock;
            var def = slim?.BlockDefinition as MyWarheadDefinition;
            // Default to engine's hardcoded 30m cap if we can't read the
            // definition (e.g. attacker block was already closed).
            return def?.ExplosionRadius ?? 30f;
        }
    }
}
